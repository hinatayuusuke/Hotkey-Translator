# GraphicsHook Frame Pipe v2 実装案

1. **概要（1–3行）**
- 本書は `Doc/GraphicsHook_OBS_Reference_Performance_Improvement_Policy.md` の「差分 2: consumer が race 吸収のコストを持っている」を解消するための shared memory v2 実装案である。
- 現状の Vulkan Hook は off-Present publish まで進んだが、frame pipe 自体は `FrameHeader v1 + 単一 payload` のままであり、writer race は C# consumer 側の confirm read と `Thread.Sleep(5)` retry に残っている。
- 方針は、producer 側が publish 完了を責任持って表現する `double buffer + publishedSeq` 契約へ切り替え、consumer 側の reopen / sleep retry 依存を縮小することにある。

2. **ゴール / 非ゴール**
### ゴール
- Vulkan Hook 改善後の shared memory 経路に対し、producer 側主導で race を吸収する契約へ切り替える。
- `GraphicsHookCaptureProvider` から `MemoryMappedFile.OpenExisting` / `CreateViewAccessor` の毎回再作成を外し、reader hot path の `Thread.Sleep(5)` 依存を減らす。
- DX11 / DX9 にも横展開できる共通 frame pipe v2 を定義する。

### 非ゴール
- shared texture 中心構成へ移行すること。
- cross-process named mutex を write/read のたびに取る設計へ寄せること。
- 恒久的な v1 / v2 ランタイム自動フォールバックを入れること。

3. **前提・仮定**
- Hook producer と C# consumer は同一リリースで更新できる前提とする。
- 現状確認時点で `Native/HookCommon/HookIpcProtocol.h` の `FrameHeader` は単一 payload 前提で、`publishedSeq` 相当の publish 完了指標を持たない。
- `Native/HookCommon/SharedFrameWriter.cpp` は payload を先に `memcpy` し、その後に header 全体を書き戻すだけである。
- `Services/GraphicsHookCaptureProvider.cs` は毎回 `MemoryMappedFile.OpenExisting` / `CreateViewAccessor` を行い、writer race を confirm read と sleep retry で吸収している。

4. **現状整理**
### 4.1 HookCommon / IPC
- `FrameHeader` は `frameId / width / height / stride / payloadBytes / timestampQpc` を 1 セットだけ持つ。
- mapping は `FrameHeader + payload` の単一レイアウトであり、inactive 側へ書いてから publish する余地がない。
- overlay v2 には `updatedSeq` があり、「payload を先に書き、最後に publish 用 seq を更新する」という契約が既に存在する。

### 4.2 SharedFrameWriter
- `SharedFrameWriter::WriteFrame(...)` は `payloadDst` へ `memcpy` した後、`MemoryBarrier()` を挟んで header を書く。
- writer 自体は「いま読んでよい slot」がどれかを表現しないため、reader 側は payload 読み出し後に header を再読込して整合性確認するしかない。

### 4.3 GraphicsHookCaptureProvider
- `TryReadBitmap(...)` は mapping を毎回 open し、header validate 後に payload を読む。
- `TryWaitForInitializedHeader(...)` と `TryWaitForNewFrame(...)` は `Thread.Sleep(5)` ベースで待機する。
- payload 読み出し後に header を再読込し、`FrameId / PayloadBytes / Width / Height / Stride` が一致した時だけ bitmap 化している。

### 4.4 Vulkan Hook
- `Native/HookAgentVulkan/VulkanPresentHook.cpp` は off-Present publish により、CPU copy と `SharedFrameWriter.WriteFrame(...)` を hook hot path から worker 側へ移している。
- ただし publish 先の mapping 契約は v1 のままなので、consumer の race 吸収コストは残っている。

5. **提案アーキテクチャ**
### 5.1 全体方針
- shared memory を v2 化し、mapping 内に 2 面の frame slot を持たせる。
- producer は「現在公開中でない slot」に payload と slot header を書き、最後に `publishedIndex` と `publishedSeq` を更新して publish 完了を示す。
- consumer は `publishedSeq -> publishedIndex -> slot header/payload -> publishedSeq confirm` の順で読む。
- confirm 前後で `publishedSeq` が変わった場合だけ即 retry し、通常系では sleep を入れない。

### 5.2 Mapping レイアウト案
```cpp
struct FramePipeHeaderV2
{
    std::uint32_t magic;
    std::uint32_t version;
    std::uint32_t api;
    std::uint32_t producerPid;
    std::uint32_t slotCount;      // 固定で 2
    std::uint32_t payloadCapacity;
    std::uint32_t publishedIndex; // 0 or 1
    std::uint32_t reserved0;
    std::uint64_t publishedSeq;   // payload/slot header の publish 完了を表す
    std::uint64_t reserved1;
};

struct FrameSlotHeaderV2
{
    std::uint64_t frameId;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint32_t payloadBytes;
    std::uint32_t pixelFormat;
    std::uint64_t timestampQpc;
    std::uint64_t slotSeq;        // デバッグ用。publishedSeq と同値を入れる
};
```

- payload は `slot0 header + slot0 payload + slot1 header + slot1 payload` の固定配置とする。
- `payloadCapacity` は現在の最大 frame byte 数を保持し、slot ごとの payload 領域サイズを固定する。
- WHY: reader は offset 計算を固定化でき、publish 中に mapping 再構築が起きない限り slot のアドレスが不変になる。

### 5.3 Producer シーケンス
1. `publishedIndex` の反対側を write target に選ぶ。
2. target slot の payload 領域へ frame bytes をコピーする。
3. `MemoryBarrier()` を入れる。
4. target slot header を更新する。
5. `MemoryBarrier()` を入れる。
6. `publishedIndex` を target slot に更新する。
7. `publishedSeq` を単調増加で更新する。

- `publishedSeq` は 0 を未初期化扱いにし、初回 publish は 1 から始める。
- `slotSeq` は診断専用で、reader が `publishedSeq` と slot header の対応を追いやすくするために残す。
- named mutex は初手では使わない。single producer 前提なので seqlock 風 publish 契約で足りる。

### 5.4 Consumer シーケンス
1. pid + api + mappingName 単位で `MemoryMappedFile` / `MemoryMappedViewAccessor` をキャッシュする。
2. `publishedSeq` を読む。0 なら未初期化として fail fast する。
3. `publishedIndex` を読む。
4. 対応 slot の header を読む。
5. slot の payload を読む。
6. `publishedSeq` を再読込する。
7. `publishedSeq` が一致し、かつ slot header の `slotSeq` も一致する時だけ bitmap 化する。

- 不一致時は `MaxReadAttempts` の範囲で即 retry し、sleep は入れない。
- `publishedSeq` が前回と同一なら cached bitmap の clone を返す。
- writer 側が更新中でも reader は古い公開 slot を読めるため、`TryWaitForNewFrame(...)` 自体が不要になる。

### 5.5 Mapping 名と切替方針
- v2 は新しい mapping 名を使う。
- 例: `Local\\HotkeyTranslator_Frame_{api}_{pid}_v2`
- WHY: v1 と同名のまま header layout を変えると、旧 consumer / 旧 hook の取り違えが fail-fast にならない。

6. **インターフェース設計**
### 6.1 HookIpcProtocol
- `FrameHeader` は残しつつ、v2 用に `FramePipeHeaderV2` / `FrameSlotHeaderV2` を追加する。
- version 定数は frame pipe v1 と v2 を分離し、consumer が誤読しないようにする。

### 6.2 SharedFrameWriter
- `SharedFrameWriter` は v2 専用 writer として更新する。
- `EnsureCapacity(...)` は `header + slot0 + slot1` 分を確保する。
- `WriteFrame(...)` は「inactive slot を選び、payload/header/publishSeq を更新する」ロジックへ置き換える。
- `MappingName()` は v2 名を返す。

### 6.3 GraphicsHookCaptureProvider
- per-read open をやめ、mapping handle と accessor をインスタンス内 cache として保持する。
- 読み出し API は `publishedSeq` 基準に変え、`TryWaitForInitializedHeader(...)` / `TryWaitForNewFrame(...)` を削除する。
- cache は `pid + mapName + publishedSeq` をキーにし、同一 seq の再読込では bitmap clone だけ返す。

### 6.4 Hook Agent 側
- Vulkan / DX11 / DX9 の publish worker から見た `WriteFrame(...)` 呼び出しシグネチャは維持してよい。
- worker 層は frame pipe v2 の存在を知らず、`SharedFrameWriter` だけが v2 publish 契約を吸収する。
- WHY: API ごとの差分は capture/readback だけに留め、IPC 契約変更の実装を HookCommon に閉じ込めるため。

7. **実装手順（ステップ分割）**
- Step 1: `HookIpcProtocol.h` に v2 header/slot 定義と mapping 名規約を追加する。
- Step 2: `SharedFrameWriter` を v2 対応へ更新し、2-slot publish を実装する。
- Step 3: `GraphicsHookCaptureProvider` に mapping/accessor cache を入れ、v2 読み出しへ切り替える。
- Step 4: `TryWaitForInitializedHeader(...)` / `TryWaitForNewFrame(...)` / header confirm read の旧 race 吸収ロジックを削除する。
- Step 5: Vulkan hook で v2 mapping 名が status / 診断ログに反映されることを確認する。
- Step 6: DX11 / DX9 に同じ `SharedFrameWriter` を適用し、API 横断で動作を揃える。

8. **非機能要件チェック**
### 性能
- `OpenExisting` / `CreateViewAccessor` の毎回再作成を止めることで host 側 CPU と GC 負荷を下げる。
- `Thread.Sleep(5)` を外すことで watch loop の遅延と jitter を減らす。
- writer 側は payload copy 回数を増やさず、単に publish 対象 slot を切り替えるだけに留める。

### 互換性
- 永続的な v1/v2 自動フォールバックは入れない。
- producer/consumer は同時更新前提にし、mapping 名を分けて fail-fast にする。

### 可観測性
- status / log に `framePipeVersion` と `publishedSeq` の最新値を出せるようにする。
- reader 側で `seq_retry_count`, `cache_hit`, `mapping_reopen_count` を計測対象にする。

### 運用
- race 不一致時の retry 上限を超えたら cached clone 再利用か明示 skip を返し、sleep retry で粘らない。

9. **リスクと緩和策**
- Risk: mapping サイズが増えるため、4K フレーム時の共有メモリ使用量が約 2 倍になる。
- Mitigation: slot 数は 2 固定にし、triple buffer 化は行わない。必要なら別途 capacity 上限を見直す。

- Risk: reader cache が detach 後の古い mapping handle を握り続ける。
- Mitigation: `producerPid` / `publishedSeq` / open 失敗時に cache を破棄する明示パスを入れる。

- Risk: v2 への切替中に旧 mapping を読んでしまう運用事故が起こる。
- Mitigation: mapping 名を v2 専用に分離し、旧 reader では見つからない形にする。

- Risk: `publishedSeq` 更新順序が崩れると reader が壊れる。
- Mitigation: `SharedFrameWriter` に publish 順序を固定し、WHY コメントと診断ログを残す。

10. **影響範囲**
- `Native/HookCommon/HookIpcProtocol.h`
- `Native/HookCommon/SharedFrameWriter.h`
- `Native/HookCommon/SharedFrameWriter.cpp`
- `Services/GraphicsHookCaptureProvider.cs`
- `Native/HookAgentVulkan/VulkanPresentHook.cpp`
- `Doc/GraphicsHook_OBS_Reference_Performance_Improvement_Policy.md`
- `Doc/`（本実装案）

11. **Definition of Done**
- [ ] `Frame pipe v2` の header / slot layout が `HookCommon` に定義されている
- [ ] `SharedFrameWriter.WriteFrame(...)` が inactive slot への publish を行う
- [ ] `GraphicsHookCaptureProvider` が mapping/accessor を毎回 reopen しない
- [ ] `Thread.Sleep(5)` ベースの frame wait が reader hot path から消えている
- [ ] writer race 時の reader retry は `publishedSeq` confirm のみで成立している
- [ ] Vulkan off-Present publish で frame 読み取りが継続し、既存 OCR 流れが壊れていない

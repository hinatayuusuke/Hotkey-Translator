# GraphicsHook OBS参照 パフォーマンス改善方針

1. **概要（1–3行）**
- 本書は OBS Studio の `graphics-hook` 実装を参照し、現行 Hotkey-Translator の Hook pipeline に対して「どこを OBS 方式へ寄せるべきか」を整理した改善方針である。
- 結論として、現行実装は DX11/Vulkan ともに delayed readback の土台は既にあるが、`Present` / `vkQueuePresentKHR` hot path から CPU publish を十分に外し切れていない。
- まずは共有メモリ契約を維持したまま「off-Present publish 化」を進め、shared texture 化は第2段階ではなく第3段階の高コスト施策として扱う。

2. **ゴール / 非ゴール**
### ゴール
- ゲーム側 render thread / present path の tail latency を下げ、平均 FPS ではなく `1% low` を改善する。
- 現行の `SharedFrameWriter` / C# consumer 契約を大きく壊さず、段階導入と切り戻しができる形で改善する。
- DX11 と Vulkan で設計方針を揃え、API ごとの差は GPU 完了判定手段だけに寄せる。

### 非ゴール
- いきなり OBS と同じ shared texture 中心構成へ全面移行すること。
- OCR パイプライン全体の再設計。
- DX9 / OpenGL / DX12 まで同時に最適化すること。

3. **参照した OBS 実装**
- Repo: `obsproject/obs-studio`
- Commit: `5533a277e4400203b50114c993f89878d50a27b9`
- 参照ファイル:
  - `plugins/win-capture/graphics-hook/d3d11-capture.cpp`
  - `plugins/win-capture/graphics-hook/graphics-hook.c`
  - `plugins/win-capture/graphics-hook/graphics-hook.h`

### OBS で確認できた重要点
- `NUM_BUFFERS=3` の staging ring を使い、capture 元フレームと publish 対象フレームをずらしている。
- D3D11 shared memory path では、hook thread は `CopyResource` と「成熟済み staging の `Map`」までを行い、shared memory への `memcpy` 自体は `copy_thread` に渡している。
- shared memory 側も単一 payload ではなく、consumer が取りやすいように 2 面の texture buffer を持ち、producer/consumer の衝突を mutex で抑えている。
- 可能な場合は shared memory ではなく shared texture を優先し、CPU copy が必要な経路を fallback にしている。

4. **現状整理（Hotkey-Translator）**
### 4.1 DX11
- `Native/HookAgentDx11/Dx11PresentHook.cpp` は既に capture ring を持ち、`Map(D3D11_MAP_READ, DO_NOT_WAIT)` による delayed readback へ寄せている。
- ただし publish 成功時は `TryPublishPendingCaptureLocked` 内で `Map -> memcpy(rt.scratch) -> SharedFrameWriter.WriteFrame(...)` までを `Present` ロック区間で実行している。
- つまり OBS の「copy thread へ渡す」部分がまだ無く、GPU 完了待ちは減っても CPU publish は render thread に残っている。

### 4.2 Vulkan
- `Native/HookAgentVulkan/VulkanPresentHook.cpp` も delayed slot を持ち、`vkGetFenceStatus` で完了確認してから publish する経路を持つ。
- しかし signaled slot が見つかった場合の `cpu memcpy + WriteFrame` は依然として `Hook_vkQueuePresentKHR` 側で実行される。
- さらに delayed readback 無効時の immediate path は `vkQueueSubmit -> vkWaitForFences -> cpu copy -> WriteFrame` の同期パスが残る。

### 4.3 共通 IPC / Consumer
- `Native/HookCommon/SharedFrameWriter.cpp` は単一 mapping に対して payload と header をその場で `memcpy` して publish するだけで、publish の非同期化は持たない。
- `Services/GraphicsHookCaptureProvider.cs` は capture ごとに `MemoryMappedFile.OpenExisting` / `CreateViewAccessor` を開き直している。
- 同 provider は `TryWaitForInitializedHeader` / `TryWaitForNewFrame` で `Thread.Sleep(5)` を使った短い spin-wait を行っており、writer race を consumer 側の再試行で吸収している。

5. **OBS との差分と解釈**
### 差分 1: publish の責務分離が不十分
- OBS は render hook と shared memory copy を別スレッドへ分けている。
- 現状は delayed readback の「readback 遅延」までは進んでいるが、「publish 遅延」は未分離である。

### 差分 2: consumer が race 吸収のコストを持っている
- OBS の shared memory 経路は producer 側の double buffer と mutex で consumer 競合を減らしている。
- 現状 C# 側は header 再読込と sleep retry を持っており、producer/consumer 契約が consumer 側の待ちに寄っている。

### 差分 3: shared texture を戦略的に使っていない
- OBS は shared texture を primary にしている。
- ただし本プロジェクトは最終的に OCR のため CPU 画像が必要なので、shared texture へ寄せても最終 readback はどこかで必要になる。
- したがって、shared texture 化は「CPU readback を target process 外へ出す」時だけ意味が大きい。

6. **改善方針**
### 方針 A: まずは off-Present publish を最優先にする
- DX11/Vulkan ともに、hook thread は「GPU copy 発行」と「ready 判定」までを担当し、CPU payload copy と `SharedFrameWriter.WriteFrame` は専用 worker に渡す。
- これにより `Present` / `vkQueuePresentKHR` 上から、少なくとも `memcpy(rt.scratch)` と `memcpy(mappedView_)` を外せる。
- WHY: 現状の delayed readback 実装は半分 OBS 化されているため、最大効果が高いのは shared memory publish の責務分離である。

### 方針 B: shared memory 契約は v1 では維持する
- いきなり shared texture 契約へ切り替えず、`FrameHeader` と C# 側の読み方は当面維持する。
- 変更対象は native hook agent と `SharedFrameWriter` 周辺に限定し、consumer 側の大規模改修を避ける。
- WHY: まず target process 側の負荷を落とすことが主目的であり、consumer 契約の全面変更は効果に対してリスクが大きい。

### 方針 C: C# consumer の reopen / sleep retry を減らす
- `GraphicsHookCaptureProvider` は mapping / accessor を pid + map 名単位でキャッシュし、毎回開き直さない。
- `TryWaitForNewFrame(..., timeoutMs:25)` の待ち合わせは hot path から外し、「新フレームが無ければ cached clone を返す or 明示 skip する」方針へ寄せる。
- 将来的に必要なら frame-ready event か `updatedSeq` 型の waitable contract を追加する。

### 方針 D: shared texture 化は第3段階に限定する
- DX11 では OBS 同様に shared texture handle を host 側へ渡せば、CPU readback を target process 外へ移せる。
- ただし Vulkan も含めて統一的に扱うには host 側 GPU 管理・デバイス共有・障害時切り戻しが一段重い。
- よって、phase 1/2 の改善で不足した場合のみ検討する。

7. **提案アーキテクチャ**
### 7.1 Native 側
- `CapturePublishWorker`
  - API ごとに 1 worker thread
  - ready slot を受け取り、CPU copy と `SharedFrameWriter.WriteFrame` を担当
  - publish 完了後に slot を `Free` へ戻す

- `CaptureSlot`
  - 既存の `Pending` に加えて `ReadyToPublish` / `Publishing` を導入
  - DX11 では `Map` 済み pointer を保持し、worker 完了まで `Unmap` を遅らせる
  - Vulkan では persistently mapped staging を worker が直接読めるため、worker は fence signaled 前提で publish のみ行う

### 7.2 DX11 シーケンス
1. `Present` で free slot に `CopyResource`
2. 次以降の `Present` で `Map(DO_NOT_WAIT)` 成功時に slot を `ReadyToPublish`
3. worker が `mapped.pData -> scratch -> SharedFrameWriter` を実行
4. worker 完了通知で slot を `Unmap` して `Free`

### 7.3 Vulkan シーケンス
1. `vkQueuePresentKHR` で capture slot に `vkCmdCopyImageToBuffer` を submit
2. hook thread か worker が `vkGetFenceStatus` / 0-timeout wait で signaled を確認
3. worker が persistently mapped staging を read して `SharedFrameWriter` へ publish
4. slot を `Free` に戻して次回再利用

### 7.4 Consumer シーケンス
1. pid と frame map 名ごとに `MemoryMappedFile` / `ViewAccessor` をキャッシュ
2. header の `frameId` が変わっていなければ cached bitmap を返す
3. writer race は必要最小限の confirm read に留め、sleep retry は常用しない

8. **実装手順（ステップ分割）**
- Step 1: DX11 に worker publish を追加し、`Present` hot path から `SharedFrameWriter.WriteFrame` を外す
- Step 2: `SharedFrameWriter` のスレッド利用前提を整理し、publish queue / 完了通知 / teardown を入れる
- Step 3: Vulkan でも publish worker を導入し、`Hook_vkQueuePresentKHR` から CPU publish を外す
- Step 4: `GraphicsHookCaptureProvider` に mapping cache を導入し、`OpenExisting` / `CreateViewAccessor` の reopen を止める
- Step 5: `TryWaitForNewFrame` と `TryWaitForInitializedHeader` の sleep retry 依存を下げる
- Step 6: ここまでで不十分なら shared texture bridge を別計画として起こす

9. **計測方針**
- Native 側
  - DX11: `capture_copy_ms`, `capture_map_ms`, `publish_queue_ms`, `publish_write_ms`, `slot_busy`
  - Vulkan: `submitMs`, `waitMs`, `publish_write_ms`, `capture_defer_count`, `capture_busy_count`
- C# 側
  - `OpenExisting` 回数
  - frame read 成功率
  - cached reuse 率
  - writer race fallback 回数
- 実ゲーム確認
  - `GraphicsHookCaptureFpsLimit=1` / `5` / `15`
  - 60 FPS 固定タイトルの `1% low`
  - overlay 有効/無効の両方

10. **リスクと緩和策**
- Risk: worker backlog により publish が古くなる
- Mitigation: ready slot が詰まったら「古い pending/ready を捨てて最新優先」にする

- Risk: DX11 の `Map` / `Unmap` と worker の slot 所有権が壊れる
- Mitigation: slot state を `Pending -> ReadyToPublish -> Publishing -> Free` に分け、`Unmap` は worker 完了後だけに限定する

- Risk: teardown / resize / Alt+Tab 中に worker が古い resource を触る
- Mitigation: `ResetDeviceStateLocked` で worker drain と世代番号チェックを入れ、古い slot publish を無効化する

- Risk: consumer 側 wait 削減で stale frame 利用が増える
- Mitigation: `frameId` と `timestampQpc` を利用し、一定以上古い frame は skip する上限を入れる

11. **影響範囲**
- `Native/HookAgentDx11/Dx11PresentHook.cpp`
- `Native/HookAgentVulkan/VulkanPresentHook.cpp`
- `Native/HookCommon/SharedFrameWriter.h`
- `Native/HookCommon/SharedFrameWriter.cpp`
- `Services/GraphicsHookCaptureProvider.cs`
- `Doc/`（本方針書、および API 別実装案へのリンク整理）

12. **Definition of Done**
- [ ] DX11 で `Present` hot path から `SharedFrameWriter.WriteFrame` が外れている
- [ ] Vulkan で `Hook_vkQueuePresentKHR` hot path から CPU publish が外れている
- [ ] C# 側で `MemoryMappedFile.OpenExisting` / `CreateViewAccessor` の reopen が常態化していない
- [ ] `Thread.Sleep(5)` ベースの wait が hot path から縮小されている
- [ ] `GraphicsHookCaptureFpsLimit=1` で 1% low 悪化が現行より改善している
- [ ] overlay 有効時も描画破綻・クラッシュ・detach 不整合がない

13. **参考リンク**
- OBS `graphics-hook.h`
  - https://github.com/obsproject/obs-studio/blob/5533a277e4400203b50114c993f89878d50a27b9/plugins/win-capture/graphics-hook/graphics-hook.h
- OBS `graphics-hook.c`
  - https://github.com/obsproject/obs-studio/blob/5533a277e4400203b50114c993f89878d50a27b9/plugins/win-capture/graphics-hook/graphics-hook.c
- OBS `d3d11-capture.cpp`
  - https://github.com/obsproject/obs-studio/blob/5533a277e4400203b50114c993f89878d50a27b9/plugins/win-capture/graphics-hook/d3d11-capture.cpp

# DX11 Graphics Hook Off-Present Publish 実装案

1. **概要（1–3行）**
- 本計画は、現行 DX11 hook の delayed readback を前提に、`Present` hot path に残っている CPU publish を worker thread へ逃がす実装案である。
- 目的は `Map(D3D11_MAP_READ, DO_NOT_WAIT)` による GPU 完了待ち回避だけでなく、`memcpy(rt.scratch)` と `SharedFrameWriter.WriteFrame(...)` も `Present` 外へ出して tail latency をさらに下げることにある。
- 共有メモリ契約と C# consumer 契約は当面維持し、変更対象はまず `HookAgentDx11.dll` と `SharedFrameWriter` 周辺に限定する。

2. **ゴール / 非ゴール**
### ゴール
- `Present` ロック区間から CPU payload copy と shared memory publish を外す。
- 既存の delayed readback ring を活かしつつ、`GraphicsHookCaptureFpsLimit=1` のような低頻度 capture でも 1% low 悪化をさらに抑える。
- resize / Alt+Tab / detach を含めて安全に drain できる worker モデルを入れる。

### 非ゴール
- shared texture 契約への変更。
- Vulkan / DX9 への同時適用。
- C# 側 `GraphicsHookCaptureProvider` の本格改修。

3. **前提・仮定**
- 現状 DX11 は `CopyResource` と `Map(DO_NOT_WAIT)` の delayed readback までは導入済みである。
- 現在の主な残課題は、`TryPublishPendingCaptureLocked(...)` が `Present` 内で `Map -> CPU copy -> WriteFrame` まで実施している点である。
- 数フレーム古い画像は OCR 上は許容可能であり、publish の少しの遅れより `Present` tail latency の削減を優先する。

4. **現状整理**
- `Native/HookAgentDx11/Dx11PresentHook.cpp` では `CaptureSlotState::Pending` の slot を次フレーム以降で `Map(D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT)` している。
- `Map` 成功後はその場で `rt.scratch.resize(...)`、`std::memcpy(...)`、必要なら RGBA->BGRA swizzle、`SharedFrameWriter.WriteFrame(...)` を行っている。
- そのため GPU 完了待ちは軽減されていても、CPU copy と shared memory write が render thread に残り、OBS の `copy_thread` 的な責務分離が未実施である。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `Dx11CaptureRing`
  - 既存の staging texture ring を維持
  - slot state を `Free / Pending / ReadyToPublish / Publishing` に拡張

- `Dx11PublishWorker`
  - 1 worker thread
  - `ReadyToPublish` slot を受け取って CPU copy / swizzle / `SharedFrameWriter.WriteFrame(...)` を担当
  - 完了後に `Unmap` と slot 解放を行う

- `Dx11PublishQueue`
  - lock-free に拘らず、まずは単純な `std::mutex + std::condition_variable` で十分
  - `Present` 側は queue へ積むだけに留める

### 5.2 シーケンス
1. `Present` で capture 対象フレームなら free slot に `CopyResource`
2. 後続 `Present` で `Pending` slot の `Map(DO_NOT_WAIT)` を試す
3. `Map` 成功時は slot を `ReadyToPublish` にし、publish queue へ積む
4. worker が `mapped.pData` を `scratch` へコピーし、必要なら swizzle 後に `SharedFrameWriter.WriteFrame(...)`
5. worker 完了後に `Unmap`、slot を `Free` へ戻す

6. **インターフェース設計**
### 6.1 外部 I/F
- `FrameHeader` 形式は変更しない。
- `GraphicsHookCaptureProvider` の読み取り方式は変更しない。
- v1 では新しい設定 UI は追加しない。

### 6.2 内部データ構造（案）
- `enum class CaptureSlotState`
  - `Free`
  - `Pending`
  - `ReadyToPublish`
  - `Publishing`

- `CaptureSlot`
  - `ID3D11Texture2D* texture`
  - `DXGI_FORMAT format`
  - `std::uint32_t width`
  - `std::uint32_t height`
  - `std::uint64_t issuedQpc`
  - `std::uint64_t sourceFrameSeq`
  - `std::uint64_t generation`
  - `D3D11_MAPPED_SUBRESOURCE mapped`
  - `bool mappedValid`
  - `CaptureSlotState state`

- `Dx11PublishRequest`
  - `CaptureSlot* slot`
  - `DWORD pid`
  - `std::uint64_t publishQpc`
  - `std::uint64_t generation`

### 6.3 補助フィールド（Runtime）
- `std::thread publishThread`
- `std::mutex publishMutex`
- `std::condition_variable publishCv`
- `std::deque<Dx11PublishRequest> publishQueue`
- `std::atomic_bool publishStop`
- `std::uint64_t captureGeneration`

7. **実装詳細**
### 7.1 基本方針
- `Present` 側は slot が ready になったことを確定するだけに留め、payload 処理は worker へ移す。
- `SharedFrameWriter` は v1 では worker 専有で使い、`Present` thread と共有しない。
- WHY: まず hot path から外したいのは `WriteFrame` と CPU copy であり、複雑な multi-writer 化は初手では不要である。

### 7.2 `Present` 側の責務
現行:
1. `Map(DO_NOT_WAIT)`
2. `memcpy(rt.scratch)`
3. RGBA->BGRA swizzle
4. `WriteFrame`
5. `Unmap`
6. slot `Free`

変更後:
1. `Map(DO_NOT_WAIT)`
2. `mapped` を slot に保持
3. slot `ReadyToPublish`
4. queue push
5. すぐ return

### 7.3 worker 側の責務
1. `ReadyToPublish` request を pop
2. generation を確認し、古い世代なら `Unmap` して破棄
3. `mapped.pData` を worker 専用 scratch にコピー
4. 必要なら RGBA->BGRA swizzle
5. `SharedFrameWriter.WriteFrame(...)`
6. `Unmap`
7. slot `Free`

### 7.4 scratch バッファ
- `rt.scratch` は `Present` thread から切り離し、worker 専用 buffer へ移す。
- worker が 1 本なら `std::vector<std::uint8_t> publishScratch` を runtime に 1 本持てばよい。
- WHY: `Present` 側が scratch capacity 調整に入るだけでも CPU コストと lock hold が残るため。

### 7.5 frameId / timestamp
- `frameId` は publish 成功時にのみ worker が進める。
- `timestampQpc` は worker の write 時点ではなく、可能なら `Present` で ready 化した時点の QPC を request に載せて使う。
- WHY: consumer 側が「画面上のフレーム時刻」を見たいなら publish 完了時刻より capture 確定時刻の方が意味がある。

### 7.6 queue 詰まり時のポリシー
- queue 長は ring size 以下に抑える。
- 同一 slot の二重 enqueue は禁止する。
- `ReadyToPublish` slot が残っていて free slot が枯れる場合は、最古の ready/pending を捨てて最新優先に寄せる。
- WHY: OCR は最新寄りの画が重要で、古い publish を律儀に完了する価値は低い。

8. **ロック / 所有権設計**
### 8.1 slot 所有権
- `Pending` までは `Present` thread 所有
- `ReadyToPublish` になった瞬間に queue 所有
- worker が pop したら `Publishing`
- 完了時に `Free`

### 8.2 `Unmap` 規則
- `Map` した thread と別 thread で `Unmap` しても D3D11 immediate context 利用上の事故を避けるため、`Unmap` 実行は worker ではなく render-thread 側 deferred cleanup に戻す案も比較検討する。
- ただし初期案は worker が `Unmap` まで持つ設計を採る。
- NOTE: ここは実装前に API 制約を再確認し、危険なら「worker は CPU copy のみ、`Unmap` は次回 `Present` の cleanup パス」で分離する。

### 8.3 generation
- `ResetDeviceStateLocked` / resize / detach のたびに `captureGeneration++`
- queue に入る request は generation を保持
- worker は generation mismatch を見たら publish せず破棄する

9. **失敗時挙動**
- `Map(DO_NOT_WAIT)` が `DXGI_ERROR_WAS_STILL_DRAWING`
  - slot は `Pending` 維持
  - queue へは積まない

- worker 中に `WriteFrame` 失敗
  - ログ記録
  - `Unmap`
  - slot `Free`
  - 次回 retry は通常フローで任せる

- resize / detach 中に stale request
  - generation mismatch で publish せず破棄

10. **実装手順（ステップ分割）**
- Step 1: `CaptureSlotState` に `ReadyToPublish` / `Publishing` を追加
- Step 2: runtime に publish worker / queue / generation 管理を追加
- Step 3: `TryPublishPendingCaptureLocked(...)` を「Map 成功なら enqueue」へ変更
- Step 4: worker に CPU copy / swizzle / `WriteFrame` / slot 解放を実装
- Step 5: resize / detach / reset で queue drain と generation bump を入れる
- Step 6: perf log に publish queue / worker write の指標を追加

11. **非機能要件チェック**
- 性能
  - `Present` 側から `SharedFrameWriter.WriteFrame(...)` が消えていること
  - `capture_ms p99/max` の改善

- 安定性
  - resize / Alt+Tab / detach で worker が stale resource を触らない

- 可観測性
  - `publish_enqueued`
  - `publish_dropped_generation`
  - `publish_write_ms`
  - `publish_queue_depth`

- 互換性
  - 共有メモリ format と consumer 契約は不変

12. **リスクと緩和策**
- Risk: D3D11 context の `Unmap` を worker に寄せると thread affinity 的な問題が出る可能性がある。
- Mitigation: 実装前に API 制約を確認し、危険なら worker は CPU copy まで、`Unmap` は次回 `Present` の cleanup へ戻す二段階案に切り替える。

- Risk: queue 詰まりで stale frame が増える。
- Mitigation: queue 深さを ring size 以下に制限し、古い request を捨てて最新優先にする。

- Risk: `Present` と worker 間で slot state 競合が起きる。
- Mitigation: slot state 遷移を単方向に限定し、state 変更箇所を helper 関数に集約する。

13. **影響範囲**
- `Native/HookAgentDx11/Dx11PresentHook.cpp`
- `Native/HookCommon/SharedFrameWriter.h`
- `Native/HookCommon/SharedFrameWriter.cpp`
- `Doc/`

14. **Definition of Done**
- [ ] DX11 `Present` hot path から `SharedFrameWriter.WriteFrame(...)` が外れている
- [ ] DX11 `Present` hot path から payload `memcpy(rt.scratch)` が外れている
- [ ] queue / worker / drain が resize / detach で安全に止まる
- [ ] perf log で `publish_write_ms` と `publish_queue_depth` が見える
- [ ] `GraphicsHookCaptureFpsLimit=1` で現行より 1% low が改善している

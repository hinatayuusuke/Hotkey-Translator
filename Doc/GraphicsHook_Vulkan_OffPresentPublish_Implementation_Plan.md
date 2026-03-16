# Vulkan Graphics Hook Off-Present Publish 実装案

1. **概要（1–3行）**
- 本計画は、`Doc/GraphicsHook_OBS_Reference_Performance_Improvement_Policy.md` の方針 A と、実装済み DX11 off-Present publish のパターンを踏まえ、Vulkan hook でも `vkQueuePresentKHR` hot path から CPU publish を外す実装案である。
- 現行 Vulkan は delayed readback の土台自体はあるが、fence signaled 後の `cpu copy + SharedFrameWriter.WriteFrame(...)` がまだ `Hook_vkQueuePresentKHR` 内に残っている。
- したがって、Vulkan では「GPU submit / ready 判定は hook thread」「CPU copy / publish は worker thread」という責務分離を導入する。

2. **ゴール / 非ゴール**
### ゴール
- `Hook_vkQueuePresentKHR` / `SubmitPresentWorkLocked(...)` から CPU payload copy と `SharedFrameWriter.WriteFrame(...)` を外す。
- 現行の shared memory 契約と C# consumer 契約を維持したまま、Vulkan capture の tail latency を下げる。
- DX11 と同じく worker lifecycle を Win32 handle/event ベースで持ち、process exit でも destructor 起因クラッシュを持ち込まない。

### 非ゴール
- Vulkan shared texture bridge の導入。
- immediate path の完全撤廃。
- DX11 / C# consumer 契約の追加変更。

3. **前提・仮定**
- `Native/HookAgentVulkan/VulkanPresentHook.cpp` には delayed readback ring が既にあり、各 `CaptureSlot` は `stagingBuffer` / `stagingMemory` / `stagingMapped` / `fence` を持つ。
- 現行 delayed path は `vkGetFenceStatus(...) == VK_SUCCESS` 後に、その場で `gpu.scratch` へコピーし、`rt.frameWriter.WriteFrame(...)` まで実行している。
- `stagingMapped` は persistent map 済みなので、worker は Vulkan API を呼ばずに CPU copy だけ実行できる。
- OCR 用途では「最新寄りのフレームを落とさず拾う」より、「ゲーム側の present path を止めない」方を優先する。

4. **現状整理**
### 4.1 delayed path
- `SubmitPresentWorkLocked(...)` は `FindOldestPendingCaptureSlotLocked(gpu)` で oldest pending slot を探し、`vkGetFenceStatus(gpu.device, pendingSlot->fence)` で GPU 完了を確認している。
- `VK_SUCCESS` の場合、現状は `pendingSlot->stagingMapped -> gpu.scratch` の CPU copy、必要なら RGBA/BGRA swizzle、`rt.frameWriter.WriteFrame(...)` まで `vkQueuePresentKHR` 側で実行している。
- publish 完了後に `pendingSlot->state = CaptureSlotState::Free` として再利用している。

### 4.2 capture issue path
- ready slot を処理した後、free slot があれば `vkResetCommandPool` / `vkResetFences` / `vkBeginCommandBuffer` / `vkCmdCopyImageToBuffer` / `vkQueueSubmit` を発行し、slot を `Pending` にしている。
- つまり Vulkan でも OBS/DX11 と同様に「issue は delayed 化済み」「publish だけ present hot path に残っている」状態である。

### 4.3 immediate path
- delayed readback 無効時は `gpu.commandBuffer` / `gpu.fence` を使った同期 path が残っており、`vkWaitForFences -> cpu copy -> WriteFrame` を同フレームで実施している。
- これは診断・切り戻し用として残し、今回の主対象は delayed path の off-Present publish 化とする。

### 4.4 DX11 実装から流用すべき点
- publish worker は `std::thread` ではなく Win32 thread handle + event ベースにする。
- hook thread は queue へ request を積むだけに留め、worker が publish 後に completed queue へ返し、最終 cleanup は hook thread 側で回収する。
- teardown / reset / process exit では graceful stop と process-exit fast path を分ける。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `VulkanPublishWorker`
  - API 単位で 1 worker thread
  - `ReadyToPublish` request を受け取り、CPU copy / swizzle / `SharedFrameWriter.WriteFrame(...)` を担当
  - Vulkan API は呼ばない

- `VulkanPublishQueue`
  - DX11 実装と同じく `CRITICAL_SECTION` + `std::deque` + wake/stop/idle event を使う
  - backlog は bounded にし、古い publish を溜めすぎない

- `QueueGpuState`
  - 既存 capture ring を維持
  - `CaptureSlotState` を `Free / Pending / ReadyToPublish / Publishing` に拡張
  - queue state ごとに `publishGeneration` を持ち、destroy/recreate 時の stale request を弾く

### 5.2 データフロー
1. `Hook_vkQueuePresentKHR` 内で oldest pending slot の fence を poll
2. signaled なら slot を `ReadyToPublish` にし、publish request を worker queue へ enqueue
3. 同じ present で free slot があれば次の `vkCmdCopyImageToBuffer` を issue
4. worker が persistently mapped staging を CPU copy し、`SharedFrameWriter.WriteFrame(...)` を実行
5. worker は completed queue に返却通知だけを積む
6. 次回 `Hook_vkQueuePresentKHR` / reset / uninstall 側で completed slot を `Free` に戻す

### 5.3 なぜ worker は Vulkan API を触らないか
- persistent mapping 済みなので publish 自体に `vkMapMemory` / `vkUnmapMemory` は不要である。
- `vkResetFences` / `vkResetCommandPool` / slot 再利用は現在どおり hook thread 側に残した方が、queue resource lifecycle を一か所で管理できる。
- WHY: DX11 と同様に「render/present thread が GPU 所有権を持ち、worker は CPU publish だけ」という境界にすると teardown が単純になる。

6. **インターフェース設計**
### 6.1 外部 I/F
- `FrameHeader` と `SharedFrameWriter` の public 契約は変更しない。
- C# 側 `GraphicsHookCaptureProvider` は変更しない。
- `HT_HOOK_VK_DISABLE_DELAYED_READBACK` は従来どおり immediate path 切り戻し用として維持する。

### 6.2 内部データ構造（案）
- `enum class CaptureSlotState`
  - `Free`
  - `Pending`
  - `ReadyToPublish`
  - `Publishing`

- `struct VulkanPublishRequest`
  - `VkQueue queue`
  - `std::uint32_t slotIndex`
  - `std::uint64_t generation`
  - `DWORD pid`
  - `std::uint32_t width`
  - `std::uint32_t height`
  - `std::uint32_t stride`
  - `std::size_t bytes`
  - `bool rgbaNeedsSwap`
  - `std::uint64_t publishQpc`

- `struct VulkanCompletedPublish`
  - `VkQueue queue`
  - `std::uint32_t slotIndex`
  - `std::uint64_t generation`

### 6.3 Runtime 追加フィールド（案）
- `CRITICAL_SECTION publishQueueLock`
- `bool publishQueueLockInitialized`
- `std::deque<VulkanPublishRequest> publishQueue`
- `std::vector<VulkanCompletedPublish> publishCompleted`
- `HANDLE publishThreadHandle`
- `DWORD publishThreadId`
- `HANDLE publishWakeEvent`
- `HANDLE publishStopEvent`
- `HANDLE publishIdleEvent`
- `LONG publishActiveCount`
- `std::vector<std::uint8_t> publishScratch`

### 6.4 QueueGpuState 追加フィールド（案）
- `std::uint64_t publishGeneration = 1`
- `std::uint64_t lastPublishQueuedQpc = 0`

7. **実装詳細**
### 7.1 `SubmitPresentWorkLocked(...)` の変更点
現行 delayed path:
1. `vkGetFenceStatus`
2. `stagingMapped -> gpu.scratch` CPU copy
3. swizzle
4. `rt.frameWriter.WriteFrame(...)`
5. `slot.state = Free`

変更後:
1. `vkGetFenceStatus`
2. signaled なら `slot.state = ReadyToPublish`
3. `VulkanPublishRequest` を queue へ積む
4. `slot.state = Publishing`
5. そのまま次の capture issue 判定へ進む

### 7.2 worker 側の責務
1. request を pop
2. `queue + slotIndex + generation` で state を再確認
3. slot が `Publishing` かつ generation 一致なら `slot.stagingMapped` を読む
4. `publishScratch` へ copy、必要なら swizzle
5. `rt.frameWriter.WriteFrame(...)`
6. `publishCompleted` に完了通知を積む

### 7.3 completed cleanup
- `DrainCompletedPublishesLocked(rt)` を追加し、hook thread 側で `publishCompleted` を回収する。
- completed item は `queueGpuStates[queue]` を引き、generation 一致時だけ対象 slot を `Free` に戻す。
- generation 不一致なら既に destroy/recreate 済みなので何もしない。

### 7.4 ready 判定は hook thread に残す
- v1 では `vkGetFenceStatus(...)` を worker に移さない。
- WHY: ready 判定まで worker に寄せると queue/device access の同期範囲が広がり、まず外したい CPU publish より先に複雑性が増えるから。

### 7.5 request payload を slot pointer ではなく `queue + slotIndex + generation` で持つ理由
- `QueueGpuState` は `unordered_map` 管理であり、device removal / swapchain recreate / runtime reset で state 自体が破棄される。
- raw slot pointer を worker に長く持たせるより、lookup + generation check の方が teardown 時の stale request 廃棄を明確にできる。
- WHY: Vulkan は DX11 より queue/device state の破棄パスが多く、slot lifetime を明示的に閉じた方が安全である。

### 7.6 backlog ポリシー
- queue 深さは全 queue の ring 総数以下に制限する。
- backlog が閾値を超える場合は最古 request を捨てるか、同一 queue の最古 `ReadyToPublish` を `Free` に戻して最新優先に寄せる。
- v1 では単純に「古い publish request を捨てる」方針でよい。
- WHY: OCR は逐次全件処理より最新性が重要で、present path を止めて backlog を保全する価値が低い。

8. **シーケンス設計**
### 8.1 通常フロー
1. `DrainCompletedPublishesLocked(rt)` を実行
2. oldest pending slot を `vkGetFenceStatus`
3. signaled なら enqueue
4. free slot があれば `vkCmdCopyImageToBuffer` を submit
5. original `vkQueuePresentKHR` へ戻る

### 8.2 resize / queue state rebuild
1. 対象 queue の `publishGeneration++`
2. `DrainPublishQueueLocked(rt)` で worker idle を待つ
3. `DrainCompletedPublishesLocked(rt)` で completed を回収
4. その後に `DestroyQueueGpuState(...)`

### 8.3 uninstall / reset
1. `DrainPublishQueueLocked(rt)`
2. `StopPublishWorkerLocked(rt)`
3. `ResetRuntimeLocked(rt)` または `RemoveDeviceStateLocked(rt, device)`

### 8.4 process exit
- `DllMain(DLL_PROCESS_DETACH, reserved != nullptr)` では DX11 と同じ考え方を採る。
- process exit では graceful stop を強制せず、`std::thread` を持たないことで destructor abort を避ける。
- 必要なら DX11 と同様に `dllmain.cpp` へ WHY コメントを追加する。

9. **失敗時挙動**
- `vkGetFenceStatus == VK_NOT_READY`
  - slot は `Pending` 維持
  - `captureDeferCount++`

- `WriteFrame` 失敗
  - worker は completed queue に返し、slot は `Free`
  - ログは従来どおり `CaptureSkipReason::WriteFrameFailed`

- generation mismatch
  - stale request とみなし publish せず破棄
  - completed も generation mismatch なら何もしない

- publish queue 溢れ
  - request を捨てて `captureBusyCount` とは別に drop カウンタを加算

10. **実装手順（ステップ分割）**
- Step 1: `CaptureSlotState` に `ReadyToPublish / Publishing` を追加し、request/completed 構造体を定義する
- Step 2: Vulkan runtime に DX11 同等の Win32 publish worker 基盤を追加する
- Step 3: `SubmitPresentWorkLocked(...)` の delayed publish 部分を enqueue 化する
- Step 4: worker に CPU copy / swizzle / `WriteFrame` を実装する
- Step 5: `DrainCompletedPublishesLocked` と queue/device reset 時の generation 管理を入れる
- Step 6: `RemoveDeviceStateLocked` / `ResetRuntimeLocked` / `UninstallPresentHook()` に drain/stop 順序を反映する
- Step 7: 必要なら `dllmain.cpp` に process-exit 方針コメントを追加する

11. **非機能要件チェック**
### 性能
- `Hook_vkQueuePresentKHR` 内の `cpuCopyMs` / `writeMs` を delayed path から外す
- `submitMs` と `waitMs` は現状同等以下を維持する

### 安定性
- queue/device destroy 中に worker が `stagingMapped` を触らない
- swapchain recreate / Alt+Tab / device destroy / uninstall で stale request が publish されない

### 可観測性
- `publish_queue_depth`
- `publish_write_ms`
- `publish_drop_count`
- `publish_generation_mismatch_count`
- `capture_publish_count` は enqueue ではなく actual write success 時だけ進める

### 互換性
- shared memory header format は不変
- `GraphicsHookCaptureProvider` の挙動は不変

12. **リスクと緩和策**
- Risk: queue/device destroy と worker publish が競合し、解放済み `stagingMapped` に触れる可能性がある。
- Mitigation: `DestroyQueueGpuState` / `RemoveDeviceStateLocked` / `ResetRuntimeLocked` の前に generation bump + drain を必須化する。

- Risk: overlay 有効時に command submit と publish 完了通知の順序が読みづらくなる。
- Mitigation: overlay 描画と capture submit は従来どおり hook thread 側に残し、worker は CPU publish に限定する。

- Risk: 複数 queue を単一 worker が処理すると publish backlog が偏る。
- Mitigation: v1 は単一 worker で始め、必要なら queue ごとの drop 統計を見て後から worker 数を再検討する。

- Risk: immediate path と delayed path の挙動差が広がる。
- Mitigation: immediate path は診断専用と割り切り、main path は delayed + worker を優先的に計測する。

13. **影響範囲**
- `Native/HookAgentVulkan/VulkanPresentHook.cpp`
- `Native/HookAgentVulkan/dllmain.cpp`
- `Doc/`

14. **Definition of Done**
- [ ] Vulkan delayed path で `Hook_vkQueuePresentKHR` から `SharedFrameWriter.WriteFrame(...)` が外れている
- [ ] Vulkan delayed path で `Hook_vkQueuePresentKHR` から staging CPU copy が外れている
- [ ] worker は Vulkan API を呼ばず、CPU publish のみ担当している
- [ ] `RemoveDeviceStateLocked` / `ResetRuntimeLocked` / uninstall で queue drain が成立する
- [ ] process exit で `std::thread` 由来の abort 要因を持ち込んでいない
- [ ] overlay 有効時でも delayed capture + publish が破綻しない

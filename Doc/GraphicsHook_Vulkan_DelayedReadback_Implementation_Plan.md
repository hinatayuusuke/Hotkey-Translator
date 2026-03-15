# Vulkan Graphics Hook 遅延 Readback 実装案

1. **概要（1–3行）**
- 本計画は `Vulkan` hook の `vkQueuePresentKHR` 直前で行っている同期 capture を、複数 slot を使う遅延 readback に置き換える実装案である。
- 目的は `vkWaitForFences` を含む GPU 完了待ちを `Present` hot path から外し、`1% low` を悪化させる spike を減らすことにある。
- 既存の共有メモリ出力と overlay 契約は維持し、まずは `HookAgentVulkan.dll` の capture パスだけを改修対象にする。

2. **ゴール / 非ゴール**
### ゴール
- `vkQueuePresentKHR` フレーム内で GPU 完了待ちを起こしにくい capture 経路へ移行する。
- `GraphicsHookCaptureFpsLimit=1` のような低頻度 capture でも、1 秒に 1 回の spike が `1% low` を崩しにくい構造にする。
- `SharedFrameWriter` / C# 側 consumer を壊さず、段階導入と切り戻しができるようにする。

### 非ゴール
- GPU shared texture への全面移行。
- DX11 / DX9 / OpenGL への同時展開。
- OCR 側や C# 側 consumer の根本見直し。

3. **前提・仮定**
- 現状の主因は [VulkanPresentHook.cpp](/g:/APP%20Local/Hotkey-Translator/Native/HookAgentVulkan/VulkanPresentHook.cpp) の `SubmitPresentWorkLocked` にある `vkQueueSubmit -> vkWaitForFences -> CPU copy -> WriteFrame` 同期パスである。
- 数フレーム古い画像でも OCR 品質上は許容できる。
- queue ごとに capture 状態を分ければ、Vulkan の queue/command buffer 制約を守りながら delayed readback を導入できる。

4. **現状整理**
- 現行実装は queue ごとに 1 つの `QueueGpuState` を持ち、永続 map した staging buffer を再利用している。
- capture フレームでは `vkCmdCopyImageToBuffer` を記録し、`vkQueueSubmit` の直後に `vkWaitForFences` で完了待ちしてから CPU copy と共有メモリ書き込みを行っている。
- `stagingMapped` 自体は永続 map されているため、DX11 のような `Map` ブロックではないが、代わりに `vkWaitForFences` が hot path を止めている。
- overlay command も同じ command buffer / submit 経路に載っているため、capture と overlay の責務分離を慎重に扱う必要がある。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `HookAgentVulkan.dll`
  - `VulkanCaptureRing`
    - queue ごとに複数 slot を保持
    - slot は `commandBuffer / fence / stagingBuffer / stagingMemory / stagingMapped / metadata` を持つ
  - `VulkanCaptureScheduler`
    - `vkQueuePresentKHR` ごとに copy submit と publish を分離
  - `SharedFrameWriter`
    - 既存の共有メモリ writer を再利用

### 5.2 シーケンス
1. `vkQueuePresentKHR` で capture 対象タイミングに達したら、free slot を選ぶ。
2. その slot の command buffer に `vkCmdCopyImageToBuffer` を記録して `vkQueueSubmit` する。
3. submit 後は待たずに slot を `pending` にし、その frame の `Present` を優先する。
4. 次以降の `vkQueuePresentKHR` で、古い `pending` slot を `vkGetFenceStatus` で確認する。
5. signaled になった slot だけ CPU copy と `SharedFrameWriter.WriteFrame` を行う。
6. 未完了 slot は defer し、free slot がなければ新規 capture は見送る。

### 5.3 既存パターンへの整合
- 既存の queue ごとの `QueueGpuState` を全面置換するのではなく、capture 専用部分だけを `QueueCaptureRingState` へ分離する。
- overlay 用の render pass / framebuffer / ImGui 状態は現行の queue/swapchain 管理を維持する。
- 既存の perf log 形式は踏襲しつつ、wait と copy/publish を分離して観測できるようにする。

6. **インターフェース設計**
### 6.1 外部 I/F
- C# 側共有メモリ契約は変更しない。
- `FrameHeader` と `GraphicsHookCaptureProvider` の読み取り方式は維持する。

### 6.2 内部データ構造（案）
- `CaptureSlotState`
  - `Free`
  - `Pending`
  - `ReadyToPublish`

- `CaptureSlot`
  - `VkCommandBuffer commandBuffer`
  - `VkFence fence`
  - `VkBuffer stagingBuffer`
  - `VkDeviceMemory stagingMemory`
  - `void* stagingMapped`
  - `VkDeviceSize stagingBytes`
  - `std::uint32_t width`
  - `std::uint32_t height`
  - `VkFormat format`
  - `std::uint64_t frameSeq`
  - `std::uint64_t submitQpc`
  - `CaptureSlotState state`

- `QueueCaptureRingState`
  - `VkDevice device`
  - `std::uint32_t queueFamily`
  - `std::vector<CaptureSlot> slots`
  - `std::vector<std::uint8_t> scratch`
  - `std::uint64_t lastCaptureIssueQpc`
  - `std::uint64_t lastCapturePublishQpc`

### 6.3 設定
- v1 ではユーザー設定追加なし。
- 診断用 env var のみ追加候補:
  - `HT_HOOK_VK_CAPTURE_RING_SIZE`（default 3）
  - `HT_HOOK_VK_DISABLE_DELAYED_READBACK`

7. **実装詳細**
### 7.1 基本方針
- `vkQueuePresentKHR` で重いのは `vkWaitForFences` 待ちなので、submit と publish を別フレームへ分離する。
- 直近フレームの即時性より、`Present` の tail latency を優先する。
- free slot がなければ capture をスキップし、`Present` を止めない。

### 7.2 `vkQueuePresentKHR` フロー変更
現行:
1. `vkResetCommandPool`
2. `vkResetFences`
3. command buffer 記録
4. `vkQueueSubmit`
5. `vkWaitForFences`
6. CPU copy
7. `WriteFrame`

変更後:
1. 古い `pending` slot を 1 つ選び、`vkGetFenceStatus` で完了確認
2. signaled なら CPU copy と `WriteFrame`
3. 新規 capture タイミングなら free slot を探す
4. free slot に command buffer 記録と `vkQueueSubmit` を行い、`pending` にする
5. `vkWaitForFences` は行わない
6. ready slot がなくても `Present` は継続する

### 7.3 overlay との整合
- v1 は現行と同じく「capture copy と overlay 描画を同じ submit で行う」方針を維持する。
- ただし capture の完了待ちだけを外し、overlay-only フレームは従来どおり即時 submit する。
- 将来的に overlay と capture submit を分離する余地はあるが、v1 では非ゴールとする。

### 7.4 slot 管理
- 初期実装は ring size = 3 を推奨。
- swapchain `extent/format` が変わったら queue の全 slot を破棄して再作成する。
- device destroy / swapchain destroy / queue 状態再初期化時は、pending/ready を問わず slot を全破棄する。

### 7.5 publish 処理
- `stagingMapped` は現行どおり persistent mapping を維持する。
- fence signaled 後にだけ `stagingMapped` から `scratch` へ CPU copy し、`SharedFrameWriter` へ渡す。
- `frameId` は publish 成功時だけ進める。

### 7.6 失敗時挙動
- free slot 不足: そのフレームの capture をスキップ
- `vkGetFenceStatus != VK_SUCCESS`: slot は `pending` のまま維持
- submit 失敗: slot を `free` に戻し、diag log を出す
- swapchain 変更検出: queue ring を全破棄・再初期化

8. **実装手順（ステップ分割）**
- Step 1: `QueueGpuState` の capture 要素を `QueueCaptureRingState` として分離する
- Step 2: single-slot / immediate-wait 実装を ring + deferred publish 実装へ置換する
- Step 3: perf log に `capture_submit_ms`, `capture_publish_ms`, `capture_issue`, `capture_publish`, `capture_defer`, `capture_busy` を追加する
- Step 4: swapchain resize / destroy / device destroy の cleanup を ring 前提に整理する
- Step 5: 実機検証で ring size と publish 優先順位を調整する

9. **非機能要件チェック**
- 性能:
  - `vkQueuePresentKHR` steady state の `waitMs` をほぼゼロにする
  - `totalMs p99/max` を現行より縮小する
- 安定性:
  - free slot 不足時は fail fast ではなく capture skip を優先する
- 可観測性:
  - `issue/publish/defer/busy` と `submit/publish/cpuCopy/write` を perf log で観測できるようにする
- 互換性:
  - 共有メモリフォーマットは変えない

10. **リスクと緩和策**
- Risk: capture が数フレーム遅れ、OCR で見える内容がわずかに古くなる。
- Mitigation: ring size を 3 から始め、最も古い signaled slot を優先して publish する。

- Risk: queue ごとに command buffer / fence / staging を複数持つため、実装と cleanup が複雑になる。
- Mitigation: slot の lifecycle を `Free/Pending/ReadyToPublish` に限定し、swapchain/device の破棄時は常に全 slot をまとめて破棄する。

- Risk: overlay と capture を同一 submit で維持すると、capture 由来の submit 増加が overlay に影響する可能性がある。
- Mitigation: v1 は wait だけを外す最小変更に留め、必要なら v2 で overlay submit 分離を検討する。

11. **影響範囲**
- `Native/HookAgentVulkan/VulkanPresentHook.cpp`
  - capture path の本体変更
- `Native/HookCommon/SharedFrameWriter.cpp`
  - 原則変更なし
- `Services/GraphicsHookCaptureProvider.cs`
  - 原則変更なし
- `Doc/`
  - 本計画書追加

12. **Definition of Done**
- [ ] `vkQueuePresentKHR` 内の `vkWaitForFences` を capture 完了待ち用途で使わない構造へ置き換えた
- [ ] 共有メモリ publish が現行 consumer で読める
- [ ] `GraphicsHookCaptureFpsLimit=1` で `waitMs` と `totalMs p99/max` が現行より改善する
- [ ] perf log で `issue/publish/defer/busy` を確認できる
- [ ] resize / Alt+Tab / detach / device destroy でリークやクラッシュがない

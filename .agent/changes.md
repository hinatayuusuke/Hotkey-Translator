**2026-03-16 11:59 (Asia/Taipei) — Add DX11 no-std-thread publish worker plan**

### Summary
- DX11 publish worker を `std::thread` 非依存へ寄せる実装案を `Doc/` に追加した。

### Context / Goal
- 対象アプリ終了時に `std::thread` destructor 起因の `abort()` が発生し、終了パスを process-exit 前提で見直す必要があった。
- `detach` 常用ではなく、DLL 内 runtime が `std::thread` を持たない方向の設計案を整理したかった。

### Changes
- Win32 thread handle ベースを第一候補とする DX11 publish worker lifecycle 案を文書化した。
- process exit / 通常 detach / resize の終了パスを分けた cleanup 方針を整理した。
- `std::thread` / `condition_variable` を持たない runtime への置換方針を明記した。

### Files Touched
- `Doc/GraphicsHook_DX11_NoStdThread_PublishWorker_Plan.md` — DX11 publish worker を `std::thread` 非依存へ置き換える実装案を新規追加した。
- `.agent/changes.md` — 本タスクの記録を新規作成した。

### Behavioral Impact
- 実行時挙動の変更はまだない。
- 今後の DX11 修正で process-exit 時 abort を避ける設計方針が明確になった。

### Risk & Mitigation
- Risk: process exit 中は graceful stop を捨てるため publish 中 frame が失われる。
- Mitigation: process exit は target process 全体の終了局面なので、frame 完了保証より abort 回避を優先する方針とした。

### Tests / Verification
- 未実施（ドキュメント追加のみ）

**2026-03-16 15:34 (Asia/Taipei) — Implement DX9 off-Present publish worker and staging ring**

### Summary
- DX9 hook に off-Present publish worker と staging surface ring を実装し、`Present` hot path から CPU publish を外した。

### Context / Goal
- `Doc/GraphicsHook_DX9_TwoPhase_OffPresentPublish_Plan.md` に沿って、DX9 でも DX11/Vulkan と同じ方向で publish を `Present` 外へ逃がす必要があった。
- 現行 DX9 は単発 `stagingSurface` 上で `GetRenderTargetData -> LockRect -> memcpy -> SharedFrameWriter.WriteFrame(...)` を同期実行していたため、まず CPU publish を worker 化し、次に surface 所有権を ring 化したかった。

### Changes
- `Dx9PresentHook.cpp` に Win32 thread/event ベースの publish worker、publish queue、completed queue、drain/stop helper を追加した。
- 単発 `stagingSurface` を `captureSlots` ring に置き換え、free slot へ `GetRenderTargetData` / `LockRect` した後に publish request を enqueue する構成へ変えた。
- worker は locked surface を CPU copy して `SharedFrameWriter.WriteFrame(...)` を実行し、hook thread は completed cleanup で `UnlockRect` と slot 解放を行うようにした。
- `ShouldCaptureNowLocked` の gate を `lastCaptureIssueQpc` ベースへ寄せ、publish 完了待ちで capture cadence が崩れにくいようにした。
- reset / resetEx / uninstall 前に publish queue を drain するようにし、`dllmain.cpp` に process-exit 方針コメントを追加した。

### Files Touched
- `Native/HookAgentDx9/Dx9PresentHook.cpp` — DX9 publish worker、capture slot ring、delayed cleanup、reset/uninstall drain を実装した。
- `Native/HookAgentDx9/dllmain.cpp` — process-exit 中は `DllMain` から graceful shutdown をしない理由をコメントで補足した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- DX9 `Present` / `PresentEx` / `SwapChain::Present` では `LockRect` 後の CPU publish が worker thread 側へ移った。
- publish 中の slot とは別の free slot に次回 capture を issue できるようになり、単発 staging surface 前提ではなくなった。
- reset / device lost / uninstall 時は queue drain 後に surface を解放するため、worker が古い locked surface を触る競合を避ける挙動になった。

### Risk & Mitigation
- Risk: `GetRenderTargetData` と `LockRect` 自体は依然 `Present` に残るため、改善幅は DX11/Vulkan より小さい可能性がある。
- Mitigation: まず CPU publish を外し、surface ring で capture と publish の重なりを増やす構成にした。
- Risk: completed cleanup が次フレームまで遅れると slot 解放が 1 フレーム遅延する。
- Mitigation: present path の status publish 前と reset/uninstall 前に completed queue を必ず回収するようにした。

### Tests / Verification
- `cmake --build .\\Native\\build --config Debug --target HookAgentDx9 -j 4`
- `cmake --build .\\Native\\build_x86 --config Debug --target HookAgentDx9 -j 4`

**2026-03-16 13:34 (Asia/Taipei) — Implement Vulkan off-Present publish worker**

### Summary
- Vulkan delayed capture path の CPU publish を `vkQueuePresentKHR` から切り離し、Win32 publish worker へ移した。

### Context / Goal
- `Doc/GraphicsHook_Vulkan_OffPresentPublish_Implementation_Plan.md` に沿って、Vulkan hook でも OBS 方針の「off-Present publish」を実装する必要があった。
- 現行 delayed path は fence signaled 後の CPU copy と `SharedFrameWriter.WriteFrame(...)` を present hook 内で実行しており、1% low 改善のため hot path から外したかった。

### Changes
- `VulkanPresentHook.cpp` に Win32 thread/event ベースの publish worker、publish queue、completed queue、drain/stop helper を追加した。
- delayed capture path を enqueue 化し、worker が persistently mapped staging を CPU copy / swizzle / `WriteFrame` する構成へ変更した。
- queue/device teardown 前に publish drain を行うようにし、`QueueGpuState` に publish generation を持たせた。
- immediate capture path の `WriteFrame` も `frameWriterMutex` 経由で排他するよう調整した。
- `dllmain.cpp` に process-exit では graceful shutdown を `DllMain` から行わない理由をコメントで補足した。

### Files Touched
- `Native/HookAgentVulkan/VulkanPresentHook.cpp` — Vulkan off-Present publish worker、slot state 拡張、delayed path enqueue 化、drain/teardown 順序を実装した。
- `Native/HookAgentVulkan/dllmain.cpp` — process-exit 中の worker shutdown 方針をコメントで明記した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- Vulkan delayed capture では `vkQueuePresentKHR` が CPU publish を直接行わず、worker が shared memory publish を担当するようになった。
- queue/device reset や uninstall 前に publish queue を drain するため、古い staging mapping を worker が触る競合を避ける挙動になった。
- immediate path は残るが、`SharedFrameWriter` は worker と排他して使うようになった。

### Risk & Mitigation
- Risk: publish completion は次回 present または drain 時に回収されるため、slot 解放が 1 フレーム遅れる。
- Mitigation: completed queue を present 冒頭と status publish 前に回収し、reset/uninstall 時は明示 drain を通す。
- Risk: `build_x86` では `HookAgentVulkan.vcxproj` が生成されておらず、x86 単体ターゲット確認ができない。
- Mitigation: x64 `HookAgentVulkan` のビルド成功を確認し、`build_x86` では project file 非生成を記録した。

### Tests / Verification
- `cmake --build .\\Native\\build --config Debug --target HookAgentVulkan -j 4`
- `cmake --build .\\Native\\build_x86 --config Debug -j 4`
- `Get-ChildItem .\\Native\\build_x86 -Recurse -Filter *Vulkan*.vcxproj` で x86 側に `HookAgentVulkan.vcxproj` が生成されていないことを確認

**2026-03-16 15:15 (Asia/Taipei) — Add DX9 two-phase off-Present publish plan**

### Summary
- DX9 hook 向けに、publish 分離と staging ring 化を段階導入する 2 段階実装案を `Doc/` に追加した。

### Context / Goal
- DX9 でも DX11/Vulkan と同じ方向で `Present` hot path を軽くする方針が必要だった。
- まずは `LockRect` 後の CPU publish を外し、その後 `stagingSurface` を ring 化する順序で整理したかった。

### Changes
- 第1段階として Win32 publish worker で `memcpy + SharedFrameWriter.WriteFrame(...)` を `Present` 外へ逃がす案を整理した。
- 第2段階として単発 `stagingSurface` を ring 化し、publish 中でも次の capture issue を進める案を整理した。
- DX9 固有の制約として `GetRenderTargetData` 自体は初手では残ることを明記した。

### Files Touched
- `Doc/GraphicsHook_DX9_TwoPhase_OffPresentPublish_Plan.md` — DX9 向け 2 段階 off-Present publish 実装案を新規追加した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- 実行時挙動の変更はまだない。
- DX9 最適化の着手順序と、DX11/Vulkan と揃えるべき worker lifecycle 方針が明確になった。

### Risk & Mitigation
- Risk: DX9 は GPU readback を即座に外し切れず、第1段階だけでは改善幅が限定的になる。
- Mitigation: plan で第2段階の staging ring 化までをセットで定義し、段階的に重なりを増やす方針にした。

### Tests / Verification
- 未実施（ドキュメント追加のみ）

**2026-03-16 12:09 (Asia/Taipei) — Replace DX11 publish worker std::thread with Win32 worker**

### Summary
- DX11 off-Present publish worker を `std::thread`/`condition_variable` から Win32 thread handle/event ベースへ置き換えた。

### Context / Goal
- DX11 off-Present publish 導入後、対象アプリ終了時に `std::thread` destructor 起因の `abort()` が発生していた。
- process exit 時でも `DllMain` から安全に扱える worker lifecycle に変更し、性能改善を維持したまま終了時クラッシュ要因を除去したかった。

### Changes
- `Dx11Runtime` の publish worker 管理を `CRITICAL_SECTION`、`CreateThread`、wake/stop/idle event、`Interlocked*` ベースへ置換した。
- publish queue の enqueue、drain、stop、completed slot cleanup を Win32 同期 primitive 前提で再実装した。
- `dllmain.cpp` に process exit (`reserved != nullptr`) では graceful shutdown を行わない理由をコメントで明記した。

### Files Touched
- `Native/HookAgentDx11/Dx11PresentHook.cpp` — DX11 publish worker を `std::thread` 非依存へ変更し、queue/stop/drain 処理を Win32 API ベースへ置換した。
- `Native/HookAgentDx11/dllmain.cpp` — process-exit 中は `DllMain` から wait しない設計意図をコメントで補足した。

### Behavioral Impact
- DX11 off-Present publish 自体の動作は維持したまま、runtime が `std::thread` を保持しなくなった。
- 通常 detach では worker を drain/stop し、process exit では abort 回避を優先して OS teardown に委ねる挙動になった。

### Risk & Mitigation
- Risk: process exit 中は publish 中フレームが完了せず捨てられる。
- Mitigation: process exit では frame 完了保証より abort 回避を優先し、通常 detach では従来どおり drain を通す。
- Risk: Win32 handle/event の close 漏れや順序不整合。
- Mitigation: handle close を `ClosePublishHandlesLocked` に集約し、通常 stop は `DrainPublishQueueLocked` 後にのみ close するよう整理した。

### Tests / Verification
- `cmake --build .\\Native\\build --config Debug --target HookAgentDx11 -j 4`
- `cmake --build .\\Native\\build_x86 --config Debug --target HookAgentDx11 -j 4`
- `rg -n "std::thread|condition_variable" .\\Native\\HookAgentDx11` で DX11 hook から該当依存が消えていることを確認

**2026-03-16 13:11 (Asia/Taipei) — Add Vulkan off-Present publish implementation plan**

### Summary
- OBS 方針書と現行 DX11 実装を踏まえた Vulkan off-Present publish 実装案を `Doc/` に追加した。

### Context / Goal
- OBS 参照方針の「方針 A: まずは off-Present publish を最優先にする」を Vulkan hook に落とし込む必要があった。
- 現行 Vulkan delayed path は `vkQueuePresentKHR` 内で CPU copy と `SharedFrameWriter.WriteFrame(...)` をまだ実行しているため、DX11 と同様の責務分離案を整理したかった。

### Changes
- Vulkan delayed readback の現状、immediate path の位置付け、DX11 実装から流用すべき worker lifecycle を整理した。
- Win32 worker + publish queue + completed cleanup + generation 管理を軸にした Vulkan 実装案を書いた。
- queue/device destroy、runtime reset、process exit での drain 順序と stale request 廃棄方針を明記した。

### Files Touched
- `Doc/GraphicsHook_Vulkan_OffPresentPublish_Implementation_Plan.md` — Vulkan hook 向け off-Present publish 実装案を新規追加した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- 実行時挙動の変更はまだない。
- Vulkan hook でどこまでを hook thread に残し、どこからを worker へ出すかの実装方針が明確になった。

### Risk & Mitigation
- Risk: Vulkan queue/device destroy と worker publish の競合を文書化だけで済ませると、実装時に drain 順序を落としやすい。
- Mitigation: plan 内で generation bump と `DestroyQueueGpuState` 前 drain を必須手順として明記した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）

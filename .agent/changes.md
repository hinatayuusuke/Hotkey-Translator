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

**2026-03-16 18:00 (Asia/Taipei) — Add public GitHub README**

### Summary
- GitHub 公開向けのルート README を新規作成し、機能概要とサードパーティ注意書きを整理した。

### Context / Goal
- リポジトリ公開時に、用途、ビルド前提、実行時依存関係を README だけで把握できる状態にしたかった。
- LunaTranslator 作者公開の改造版 Magpie、Dear ImGui、MinHook を使っている点を誤解なく明記する必要があった。

### Changes
- ルート `README.md` を追加し、概要、主な機能、動作環境、ビルド手順、使い始め、構成、ライセンス注意点を記載した。
- サードパーティ節で Magpie 改造版の位置づけと、Dear ImGui / MinHook の同梱・ライセンス注意を明記した。
- ルート `LICENSE` 不在のため、公開前にプロジェクト本体ライセンスを明示すべき旨を README に追記した。

### Files Touched
- `README.md` — GitHub 公開向けの紹介文、ビルド方法、依存関係、サードパーティ注意書きを新規追加した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- 実行時挙動の変更はない。
- 公開時にサードパーティ構成と再配布上の注意点を README から確認できるようになった。

### Risk & Mitigation
- Risk: Magpie 改造版の配布条件や原著作者表記が README だけでは法的に十分でない可能性がある。
- Mitigation: README で注意喚起しつつ、公開前に実際の配布物へ個別ライセンス表記を同梱する前提を明記した。
- Risk: ルート LICENSE 不在のまま公開すると、リポジトリ本体の利用条件が不明確になる。
- Mitigation: README に公開前の追加対応として明記した。

### Tests / Verification
- `Get-Content README.md` で記載内容を確認
- `Get-Content .agent/changes.md -Tail 50 -Encoding UTF8` で追記方針と既存フォーマットを確認

**2026-03-16 18:32 (Asia/Taipei) — Add English README**

### Summary
- 英語版 README を追加し、日本語版 README と相互リンクできるようにした。

### Context / Goal
- GitHub 公開時に、日本語話者以外にもプロジェクト概要とサードパーティ注意点を伝えられる状態にしたかった。
- 既存の日本語 README と同じ前提で、Magpie 改造版、Dear ImGui、MinHook の扱いを英語でも明示する必要があった。

### Changes
- `README.en.md` を新規追加し、日本語 README の内容を英語で再構成した。
- `README.md` の冒頭に英語版へのリンクを追加した。
- 英語版 README にもサードパーティ節と root `LICENSE` 不在の注意を反映した。

### Files Touched
- `README.md` — 冒頭に英語版 README への導線を追加した。
- `README.en.md` — GitHub 公開向けの英語版 README を新規追加した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- 実行時挙動の変更はない。
- GitHub 上で日本語版と英語版の README を切り替えて参照できるようになった。

### Risk & Mitigation
- Risk: 日本語版と英語版の内容が将来的に乖離する可能性がある。
- Mitigation: 同一構成で記述し、今後 README 更新時は両方を同時更新する前提を残した。
- Risk: サードパーティの説明を英語化しても、実際の再配布条件確認を省略される可能性がある。
- Mitigation: 英語版にも再配布条件を別途確認すべき旨を明記した。

### Tests / Verification
- `Get-Content README.md` でリンク追記を確認
- `Get-Content README.en.md` で英語版内容を確認

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

**2026-03-17 11:17 (Asia/Taipei) — Add beginner GitHub Pages site policy**

### Summary
- GitHub Pages で公開する配布サイト向けに、Vite + React + Tailwind CSS の初心者向け方針書を追加した。

### Context / Goal
- Hotkey Translator の公開用サイトを作る前に、Web アプリ化へ広がりすぎない最小方針を先に固めたかった。
- GitHub Pages 特有の `base` 設定や SPA ルーティング回避を含め、初心者が迷いにくい実装方針を文書化したかった。

### Changes
- GitHub Pages 上では WPF 本体ではなく紹介・配布ページを作る前提を明確化した。
- `site/` 配下に Vite プロジェクトを分離する案、1 ページ構成、React Router 非導入、GitHub Actions デプロイを基本方針として整理した。
- Hero、機能紹介、スクリーンショット、導入手順、Releases 導線を含む最低限のページ構成と実装順をまとめた。

### Files Touched
- `Doc/GitHubPages_ViteReactTailwind_Beginner_Policy.md` — GitHub Pages + Vite + React + Tailwind CSS の初心者向け公開方針書を新規追加した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- 実行時挙動の変更はない。
- 公開サイト実装時の前提、スコープ、推奨構成が明確になった。

### Risk & Mitigation
- Risk: 文書だけでは Vite / Tailwind の具体セットアップ差分がまだ未反映。
- Mitigation: 次タスクで `site/` の初期作成と Pages デプロイ設定をこの方針に沿って実装する。

### Tests / Verification
- README と配布要件ドキュメントを確認し、方針が現行の Windows デスクトップ配布前提と矛盾しないことを確認

**2026-03-18 10:02 (Asia/Taipei) — Add Frame Pipe v2 double-buffer implementation plan**

### Summary
- consumer 側の race 吸収コストを producer 側へ戻すための shared memory v2 実装案を追加した。

### Context / Goal
- `GraphicsHook_OBS_Reference_Performance_Improvement_Policy.md` の差分 2 について、現状実装を再確認した上で具体的な実装方針を文書化したかった。
- Vulkan off-Present publish 実装後も `FrameHeader v1 + 単一 payload` 契約が残っているため、`GraphicsHookCaptureProvider` の reopen / sleep retry を減らす次段の設計を明確にしたかった。

### Changes
- `HookIpcProtocol.h`、`SharedFrameWriter.cpp`、`GraphicsHookCaptureProvider.cs` の現状契約を再確認し、reader 側に race 吸収が残っている点を整理した。
- producer 側 double buffer + `publishedSeq` 契約を核にした frame pipe v2 実装案を書いた。
- mapping 名分離、mapping/accessor cache、`Thread.Sleep(5)` 依存の除去方針、DX11/DX9 への横展開方針を含めた。

### Files Touched
- `Doc/GraphicsHook_FramePipeV2_DoubleBuffer_Implementation_Plan.md` — shared memory v2 の実装案を新規追加した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- 実行時挙動の変更はまだない。
- 差分 2 を解消するための producer/consumer 契約変更案が明文化された。

### Risk & Mitigation
- Risk: v2 案は shared memory サイズ増加と producer/consumer 同時更新を伴うため、実装時の切替事故が起きやすい。
- Mitigation: Doc で mapping 名分離と fail-fast 前提を明記し、恒久的な自動フォールバックを入れない方針にした。

### Tests / Verification
- `Doc/GraphicsHook_OBS_Reference_Performance_Improvement_Policy.md` を UTF-8 で再読込し、差分 2 の前提と整合することを確認
- `Services/GraphicsHookCaptureProvider.cs`、`Native/HookCommon/SharedFrameWriter.cpp`、`Native/HookCommon/HookIpcProtocol.h` の現状コードを参照し、Doc の現状整理が実装と一致することを確認

**2026-03-18 10:25 (Asia/Taipei) — Implement frame pipe v2 double-buffer contract**

### Summary
- shared memory frame pipe を v2 化し、producer 側 double buffer + `publishedSeq` publish と C# reader 側 mapping cache を実装した。

### Context / Goal
- `Doc/GraphicsHook_FramePipeV2_DoubleBuffer_Implementation_Plan.md` を実装し、consumer 側の race 吸収コストと `Thread.Sleep(5)` retry を減らしたかった。
- Vulkan/DX11/DX9 の off-Present publish 後も残っていた `FrameHeader v1 + 単一 payload` 契約を `HookCommon` で置き換えたかった。

### Changes
- `HookIpcProtocol.h` に `FramePipeHeaderV2` / `FrameSlotHeaderV2` と v2 mapping 名、slot/pipe size helper を追加した。
- `SharedFrameWriter` を 2-slot publish へ更新し、inactive slot へ payload を書いてから `publishedIndex` / `publishedSeq` を更新する契約へ切り替えた。
- C# `GraphicsHookCaptureProvider` を v2 reader に差し替え、mapping/accessor cache と `publishedSeq` confirm read による race 検出へ変更した。
- `TryWaitForInitializedHeader` / `TryWaitForNewFrame` / `Thread.Sleep(5)` ベース wait を撤去した。
- Vulkan の write-frame 診断ログを v2 mapping 名へ更新した。

### Files Touched
- `Native/HookCommon/HookIpcProtocol.h` — frame pipe v2 の header/slot layout と v2 mapping 名を追加した。
- `Native/HookCommon/SharedFrameWriter.h` — v2 publish 用の内部 state と helper 宣言を追加した。
- `Native/HookCommon/SharedFrameWriter.cpp` — 2-slot publish と固定容量 mapping を実装した。
- `Services/GraphicsHookCaptureProvider.cs` — v2 reader、mapping/accessor cache、`publishedSeq` confirm read を実装した。
- `Native/HookAgentVulkan/VulkanPresentHook.cpp` — write-frame 診断ログの mapping 名を v2 に合わせた。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- frame mapping 名が `Local\\HT_HOOK_FRAME_{api}_{pid}_V2` に変わった。
- reader は frame ごとに `OpenExisting` / `CreateViewAccessor` を繰り返さず、同一 mapping を再利用する。
- writer race 時の reader retry は `publishedSeq` の confirm read に限定され、sleep wait は行わない。

### Risk & Mitigation
- Risk: mapping を固定容量 2-slot にしたため、共有メモリの予約サイズが大きくなる。
- Mitigation: fixed capacity にした理由をコードコメントで明記し、reader cache と同居できる安全な named mapping 契約を優先した。
- Risk: producer/consumer の v2 同時更新が揃わないと frame read が fail-fast する。
- Mitigation: mapping 名を v2 専用に変え、旧契約との取り違えを避けた。

### Tests / Verification
- `cmake --build .\Native\build --config Debug --target HookAgentDx11 HookAgentDx9 HookAgentVulkan HookHost -j 4`
- `cmake --build .\Native\build_x86 --config Debug --target HookAgentDx11 HookAgentDx9 HookHost -j 4`
- `dotnet build .\Hotkey-Translator.sln`

**2026-03-18 11:40 (Asia/Taipei) — Set WPF application icon**

### Summary
- `Ico\Hotkey-Translator.ico` を WPF アプリの実行ファイルとメインウィンドウのアイコンとして設定した。

### Context / Goal
- 既存の `.ico` ファイルをアプリのアイコンとして反映したかった。
- ビルド成果物と WPF ウィンドウ表示の両方で同じアイコンを使う状態にしたかった。

### Changes
- `.csproj` に `ApplicationIcon` を追加し、`Ico\Hotkey-Translator.ico` を参照するようにした。
- `.ico` を WPF `Resource` として埋め込み、`MainWindow.xaml` の `Icon` に設定した。

### Files Touched
- `Hotkey-Translator.csproj` — アプリケーションアイコン設定と `.ico` の埋め込み設定を追加した。
- `MainWindow.xaml` — メインウィンドウの `Icon` を追加した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- ビルドしたアプリの実行ファイルアイコンが `Hotkey-Translator.ico` になる。
- メインウィンドウのタイトルバーとタスク切り替え表示で同じアイコンが使われる。

### Risk & Mitigation
- Risk: `.ico` がプロジェクトに埋め込まれていないと、WPF 側の `Icon` 解決に失敗する。
- Mitigation: `Resource Include` を追加し、暗黙 `None` を除外して参照元を一意にした。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj`

**2026-03-18 13:37 (Asia/Taipei) — Add online distribution build script**

### Summary
- online 配布用フォルダを生成する `build-dist.ps1` を追加し、WPF publish・native build・必要資産の収集を一括化した。

### Context / Goal
- WPF 本体を single-file EXE として配布しつつ、固定相対パスで参照する helper/native/python 資産も同じ構造で揃えたかった。
- `TranslationService` と GGUF/Paddle 実モデルを除外した online 配布を再現可能なスクリプトにしたかった。

### Changes
- `build-dist.ps1` を追加し、`dotnet publish`、`cmake` x64/x86 build、配布フォルダの作成、必要ファイルのホワイトリストコピーを実装した。
- Python 系サービスは `.venv`・テスト類・GGUF payload を除外し、`uv` と manifest ベースの初回セットアップ前提にした。
- hook DLL がアンチウイルスで削除される前提を吸収するため、x64/x86 HookHost は必須、hook agent DLL は警告付き任意資産として扱うようにした。
- 配布先に `DIST-NOTES.txt` を出力し、未同梱物と初回ダウンロード挙動、欠落した hook DLL を明示するようにした。

### Files Touched
- `build-dist.ps1` — online 配布フォルダ生成の自動化スクリプトを追加した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- `.\build-dist.ps1` で `dist\Hotkey-Translator-online` を再生成できるようになった。
- 配布物には `TranslationService`、Python `.venv`、GGUF/mmproj、Paddle 実モデルは入らず、初回起動時に `uv sync` や manifest ダウンロードが走る。
- ローカルのアンチウイルスにより hook DLL が隔離された場合でも、dist 生成自体は完了し、`DIST-NOTES.txt` に欠落一覧が残る。

### Risk & Mitigation
- Risk: アンチウイルスが hook agent DLL を削除すると、対象 API の hook 機能が配布物で欠落する。
- Mitigation: HookHost のみを必須にし、hook DLL 欠落は警告と `DIST-NOTES.txt` に記録して配布生成を止めないようにした。
- Risk: online 配布では初回起動時に Python runtime やモデルのダウンロード時間が発生する。
- Mitigation: `DIST-NOTES.txt` に初回挙動を明記し、manifest/lock ファイルは必ず同梱する構成にした。

### Tests / Verification
- `powershell -ExecutionPolicy Bypass -File .\build-dist.ps1`
- 出力確認: `dist\Hotkey-Translator-online`
- 確認結果: `Hotkey-Translator.exe`、`Tools\uv\uv.exe`、`Tools\WinRtLanguagePackElevator\WinRtLanguagePackElevator.exe`、`Native\HookHost\bin\HookHost.exe`、`Native\HookHost\bin\x86\HookHost.exe` を含む配布フォルダが生成された。
- 確認結果: x64 の hook agent DLL は配布に含まれ、x86 hook agent DLL はアンチウイルス隔離前提により `DIST-NOTES.txt` の missing list に記録された。

**2026-03-19 10:00 (Asia/Taipei) — Add hotkey defaults consolidation proposal**

### Summary
- ホットキー既定値の一本化、無効設定、F6～F10 既定への整理方針を `Doc` に実装案として追加した。

### Context / Goal
- ホットキー既定値が複数箇所で管理されており、変更時のズレや空設定の扱いが不安定だった。
- 「多重管理を一本化」「無効設定できる」「デフォルトは F6～F10」という方針を実装前に整理したかった。

### Changes
- `Doc/Hotkey_Defaults_DisabledState_Implementation_Plan.md` を追加した。
- 現状の多重管理箇所、disabled 状態の設計、F6～F10 を中心にした既定ポリシー、実装手順、影響範囲、DoD を文書化した。

### Files Touched
- `Doc/Hotkey_Defaults_DisabledState_Implementation_Plan.md` — ホットキー既定値整理の実装案を追加した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- 実行挙動の変更はまだ無い。
- 次の実装で何を一本化し、どのホットキーを既定有効/無効にするかが明確になった。

### Risk & Mitigation
- Risk: 実装案だけでコードはまだ変わっていないため、現行挙動はそのまま残る。
- Mitigation: 設計を先に固定し、次の実装で保存表現・UI・登録処理を一括で変更する前提を文書化した。

### Tests / Verification
- `Get-Content -Path 'Doc\Hotkey_Defaults_DisabledState_Implementation_Plan.md' -Encoding UTF8 -TotalCount 40`

**2026-03-19 10:12 (Asia/Taipei) — Revise hotkey defaults plan to use Disable and remove fallback rule**

### Summary
- ホットキー実装案を `Disable` 明示保存と補完ルール廃止の方針へ更新し、文書全体の整合性を取り直した。

### Context / Goal
- 空文字ではなく `Disable` を保存したい方針が固まった。
- `HotkeyDefaultsRule` を廃止し、未リリース前提で既存ユーザー互換を考えない内容へ実装案を揃えたかった。

### Changes
- `Doc/Hotkey_Defaults_DisabledState_Implementation_Plan.md` の保存表現を空文字から `Disable` に変更した。
- 一般的な既定値補完ルールを廃止し、`HotkeyDefaultsRule` を削除対象とする内容へ更新した。
- 未リリース前提のため移行・既存ユーザー互換の記述を整理した。
- 文書全体を再確認し、`Disable`、補完廃止、F6～F10 既定方針で表現を統一した。

### Files Touched
- `Doc/Hotkey_Defaults_DisabledState_Implementation_Plan.md` — `Disable` 保存と補完廃止前提へ実装案を更新した。
- `.agent/changes.md` — 本タスクの記録を追記した。

### Behavioral Impact
- 実行挙動の変更はまだ無い。
- 今後の実装方針が「`Disable` 保存」「`HotkeyDefaultsRule` 廃止」「既定値補完の常時実行なし」で確定した。

### Risk & Mitigation
- Risk: 文書だけ更新され、コード側の旧方針がまだ残っている。
- Mitigation: 実装時はこの文書を基準に、設定保存・UI・登録処理・既定値定義を同時に更新する前提を明記した。

### Tests / Verification
- `Get-Content -Path 'Doc\Hotkey_Defaults_DisabledState_Implementation_Plan.md' -Encoding UTF8`
- `rg -n "未割り当て|Disable|HotkeyDefaultsRule|補完ルール|既存ユーザー互換" 'Doc\Hotkey_Defaults_DisabledState_Implementation_Plan.md'`

**2026-03-19 10:33 (Asia/Taipei) — Implement hotkey Disable defaults and remove fallback restoration**

### Summary
- ホットキー既定値を `HotkeyDefaults` に一本化し、`Disable` を明示保存する実装へ切り替えた。

### Context / Goal
- `Doc/Hotkey_Defaults_DisabledState_Implementation_Plan.md` の方針どおり、既定値の多重管理を解消したかった。
- `F6` から `F10` 以外を既定で無効化し、補完ルールで勝手に復活しない状態にしたかった。

### Changes
- `Models/HotkeyDefaults.cs` を追加し、ホットキー既定値・`Disable` 定数・キー選択肢・保存値正規化を集約した。
- `Models/AppSettings.cs` と `ViewModels/SettingsViewModel.cs` の既定値参照を `HotkeyDefaults` へ寄せ、`Disable` のとき modifier を強制的に `None` とするようにした。
- `MainWindow.xaml.cs` の hotkey 設定構築を fallback なしへ変更し、`Disable` を `Key.None` として扱い、登録対象から除外するようにした。
- `Services/Application/HotkeyController.cs` を更新し、登録対象が 0 件でも正常状態として扱えるようにした。
- `Services/Settings/Rules/HotkeyDefaultsRule.cs` を削除し、`Services/Settings/AppSettingsValidator.cs` から参照を外した。
- 未リリース前提に合わせて `Services/Settings/AppSettingsMigrator.cs` の旧ホットキー互換 migration を削除した。

### Files Touched
- `Models/HotkeyDefaults.cs` — ホットキー既定値と `Disable` の正規化ロジックを追加した。
- `Models/AppSettings.cs` — 各ホットキー既定値を単一定義参照へ置き換えた。
- `ViewModels/SettingsViewModel.cs` — `Disable` を保持したまま読み書きし、disabled 時は modifier を無効化するようにした。
- `MainWindow.xaml.cs` — `Disable` を `Key.None` に変換し、登録・ログ表示を disabled 対応へ更新した。
- `Services/Application/HotkeyController.cs` — 登録対象 0 件を正常扱いにした。
- `Services/Settings/AppSettingsValidator.cs` — `HotkeyDefaultsRule` を除去した。
- `Services/Settings/Rules/HotkeyDefaultsRule.cs` — 廃止に伴い削除した。
- `Services/Settings/AppSettingsMigrator.cs` — 旧ホットキー互換 migration を削除した。

### Behavioral Impact
- 新規既定ホットキーは `F6` ROI、`F7` Lock window、`F8` Run once、`F9` Toggle overlay、`F10` Force run のみ有効になる。
- 補助ホットキーは既定で `Disable` になり、設定保存後に自動補完で復活しない。
- すべてのホットキーを `Disable` にしても、登録失敗ではなく「登録対象なし」の正常状態として起動できる。

### Risk & Mitigation
- Risk: `Disable` に対応していない箇所が残ると、UI 表示と実際の登録状態がずれる。
- Mitigation: 設定保存、ランタイム変換、登録処理、ログ表示を同時に更新し、ソリューション全体でビルド確認した。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln`
- `rg -n 'compat_hotkey_f12_to_f7|HotkeyDefaultsRule|HotkeyConfig\.Default|ParseKey\([^\)]*,|NormalizeHotkeyKey\([^\)]*,[^\)]' .`

**2026-03-19 10:41 (Asia/Taipei) — Extend default hotkeys for unlock and mirror actions**

### Summary
- 既定ホットキーに `Shift+F7` の Window Unlock と `Ctrl+Shift+F7` の Mirror Full Screen を追加した。

### Context / Goal
- Lock 系操作を `F7` 周辺へ揃えたまま、Unlock と Mirror を初期状態から使えるようにしたかった。
- 既定値の単一定義を維持しつつ、案内ログと設計文書も新しい割り当てへ追従させたかった。

### Changes
- `Models/HotkeyDefaults.cs` で `UnlockCaptureWindow` を `Shift+F7`、`ToggleMirrorFullscreen` を `Ctrl+Shift+F7` に変更した。
- `ViewModels/SettingsViewModel.cs` の初期 modifier 状態を新しい既定値に合わせて更新した。
- `MainWindow.xaml.cs` の起動時ホットキー案内ログを新しい既定構成に更新した。
- `Doc/Hotkey_Defaults_DisabledState_Implementation_Plan.md` の既定値ポリシーとリスク記述を実装内容に合わせて更新した。

### Files Touched
- `Models/HotkeyDefaults.cs` — Unlock と Mirror の既定キー・modifier を更新した。
- `ViewModels/SettingsViewModel.cs` — Unlock と Mirror の既定 modifier 初期値を更新した。
- `MainWindow.xaml.cs` — 起動時ログ文言を新しい既定ホットキーへ合わせた。
- `Doc/Hotkey_Defaults_DisabledState_Implementation_Plan.md` — 既定値方針の記述を現実装に合わせた。

### Behavioral Impact
- 新規既定ホットキーは `F6` ROI、`F7` Lock、`Shift+F7` Unlock、`Ctrl+Shift+F7` Mirror、`F8` Run once、`F9` Toggle overlay、`F10` Force run になる。
- Unlock と Mirror は初期状態から有効になるため、追加設定なしで利用できる。

### Risk & Mitigation
- Risk: `F7` 系の修飾キー違いを誤認すると意図しない操作をしやすい。
- Mitigation: 起動ログと設定画面の既定値を揃え、`F7` / `Shift+F7` / `Ctrl+Shift+F7` の差を明示した。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln`
- `rg -n "UnlockCaptureWindowKey|UnlockCaptureWindowModifiers|ToggleMirrorFullscreenKey|ToggleMirrorFullscreenModifiers|Shift\+F7|Ctrl\+Shift\+F7" Models\HotkeyDefaults.cs ViewModels\SettingsViewModel.cs MainWindow.xaml.cs Doc\Hotkey_Defaults_DisabledState_Implementation_Plan.md`

**2026-03-19 12:29 (Asia/Taipei) — Implement fixed overlay custom frame selection**

### Summary
- Fixed ROI overlay mode に、ROI と独立したユーザ描画表示枠の選択と切替 UI/Hotkey を追加した。

### Context / Goal
- Fixed ROI overlay の表示先を ROI 固定のままにせず、ユーザが別矩形を描画して表示位置を決められるようにしたかった。
- 既存挙動を維持しつつ、Overview/Hotkeys から設定できる最小構成で実装したかった。

### Changes
- `AppSettings` に fixed overlay 表示先 enum、カスタム表示枠、専用 hotkey 設定を追加した。
- ROI 選択処理を共通化し、ROI と fixed overlay frame の両方で同じ矩形選択 UI と hook preview を使うようにした。
- Overview に `Fixed overlay target` ComboBox と `Select user frame` ボタン、Hotkeys に `Select user frame` 登録欄を追加した。
- fixed overlay 描画時は設定に応じて ROI または保存済み custom frame を使い、未設定や無効矩形は ROI にフォールバックするようにした。

### Files Touched
- `MainWindow.xaml` — OverviewControl の固定表示枠選択イベントを配線した。
- `MainWindow.xaml.cs` — 矩形選択処理の共通化、custom frame 選択処理、hotkey 登録、保存後の overlay 再反映を追加した。
- `Models/AppSettings.cs` — fixed overlay 表示先 enum、custom frame、専用 hotkey 設定を追加した。
- `Models/HotkeyDefaults.cs` — `Select user frame` hotkey の既定値を追加した。
- `Services/Application/HotkeyCommandController.cs` — `Select user frame` hotkey の実行経路を追加した。
- `Services/Orchestration/Stages/OverlayStage.cs` — fixed overlay の描画先を ROI / custom frame で切り替える解決処理を追加した。
- `Services/PipelineOrchestrator.cs` — overlay item 構築時に capture bounds を渡し、表示先変更時に最新 overlay を再構築できるようにした。
- `UI/HotkeysControl.xaml` — `Select user frame` hotkey 行を追加した。
- `UI/HotkeysControl.xaml.cs` — 新しい hotkey ComboBox の候補を初期化した。
- `UI/OverviewControl.xaml` — fixed overlay target UI と user frame 選択導線を追加した。
- `UI/OverviewControl.xaml.cs` — user frame 選択イベントを追加した。
- `ViewModels/SettingsViewModel.cs` — fixed overlay target UI 状態、ステータス文言、専用 hotkey の保存/読込を追加した。

### Behavioral Impact
- Fixed ROI overlay mode 利用時に、表示先を `ROI` または `User frame` から切り替えられる。
- `User frame` 選択時は ROI と独立した矩形へ翻訳 overlay を集約表示できる。
- custom frame が未設定または無効な場合は ROI を使う。
- `Select user frame` hotkey は既定で `Disable` のまま追加される。

### Risk & Mitigation
- Risk: custom frame と ROI の役割差が UI 上で伝わらず、誤設定しやすい。
- Mitigation: Overview に「翻訳表示位置のみ変更し、OCR ROI は変えない」注記と状態表示を追加した。
- Risk: 既存 overlay 更新経路で表示先変更が即時反映されない。
- Mitigation: 設定保存後と frame 選択後に最新データから overlay を再構築する経路を追加した。

### Tests / Verification
- `git diff --check`（LF/CRLF warning のみ）
- `dotnet build .\Hotkey-Translator.sln`（未成功: 既存の `Tools\WinRtLanguagePackElevator` 生成物で重複 AssemblyAttribute エラー）
- `dotnet build .\Hotkey-Translator.csproj /p:BuildProjectReferences=false`（未成功: 既存の `obj\Debug\...\*_wpftmp.csproj` / `artifacts\mainobj` 由来の重複生成物エラー）

**2026-03-19 13:48 (Asia/Taipei) — Fix Win32 hotkey self-collision during updates**

### Summary
- Win32 hotkey 更新時に既存登録と自己衝突して他の hotkey が落ちる不具合を修正した。

### Context / Goal
- `Select user frame` を含む hotkey を新規設定すると、変更していない既存 hotkey まで効かなくなっていた。
- hotkey 更新時に既存登録を壊さず、変更分だけ安全に差し替えられるようにしたかった。

### Changes
- `HotkeyController.TryRegisterBindingsWin32` で、変更されていない Win32 hotkey 登録は再利用するようにした。
- 同じ id で内容が変わった binding だけ旧登録を解除して再登録するようにした。
- 使われなくなった旧 Win32 登録だけを最後に dispose するようにした。

### Files Touched
- `Services/Application/HotkeyController.cs` — Win32 hotkey 更新時の自己衝突を避けるため、 unchanged binding の再利用と差分更新に変更した。

### Behavioral Impact
- `Select user frame` を設定しても、変更していない既存 hotkey は引き続き有効のままになる。
- Win32 backend で hotkey を 1 本追加・変更したとき、同一アプリ内の旧登録との再登録衝突が起きなくなる。

### Risk & Mitigation
- Risk: 同じ id の変更 hotkey が外部競合で再登録失敗した場合、その binding だけ失われる可能性がある。
- Mitigation: 変更されていない binding は再利用し、影響範囲を変更対象 id のみに限定した。

### Tests / Verification
- `git diff --check -- Services/Application/HotkeyController.cs`（LF/CRLF warning のみ）
- コード確認: unchanged binding は再登録せず再利用、変更 id のみ解除後に再登録することを確認

**2026-03-19 14:08 (Asia/Taipei) — Fix Win32 hotkey swap updates**

### Summary
- Win32 hotkey の入れ替え更新で途中衝突する不具合を修正した。

### Context / Goal
- `Run=F8` と `Overlay Toggle=F10` の状態から、片方を相手のキーへ変更するような swap 更新で、最終状態は重複していないのに登録失敗していた。
- 変更対象 hotkey 同士の入れ替えでも、自己衝突せずに新しい組み合わせへ更新できるようにしたかった。

### Changes
- `HotkeyController.TryRegisterBindingsWin32` を 2 段階更新に変更した。
- 変更されていない binding は再利用し、変更/廃止対象の旧 binding は先に一括解除するようにした。
- 旧 binding をすべて外した後で、新しい変更 binding 群をまとめて登録するようにした。

### Files Touched
- `Services/Application/HotkeyController.cs` — swap 更新で途中状態が衝突しないよう、 changed binding の一括解除後に再登録する順序へ変更した。

### Behavioral Impact
- `F8 <-> F10` のような hotkey 入れ替え後も、最終状態が一意なら正しく有効化される。
- 変更していない binding は引き続き再利用される。

### Risk & Mitigation
- Risk: 外部アプリとの競合で再登録失敗した changed binding は無効のまま残る。
- Mitigation: 変更なし binding は再利用し、衝突ログを残すことで影響範囲を changed binding のみに限定した。

### Tests / Verification
- `git diff --check -- Services/Application/HotkeyController.cs`（LF/CRLF warning のみ）
- コード確認: changed/retired old binding を先に dispose し、その後に changed binding を登録する順序へ変わったことを確認

**2026-03-19 15:13 (Asia/Taipei) — Highlight conflicting hotkey settings**

### Summary
- Hotkey 設定の重複を UI で即時検知し、ライト/ダークテーマ対応の赤表示と保存保留を追加した。

### Context / Goal
- これまでは hotkey 登録時に重複がログへ出るだけで、設定画面では衝突箇所が分からなかった。
- アプリ内で同じ hotkey を複数行へ割り当てたとき、その場で衝突箇所を視認できるようにしたかった。

### Changes
- `SettingsViewModel` に hotkey 衝突検知ロジックと、行ごとの衝突メッセージ/サマリー状態を追加した。
- `HotkeysControl` を行単位の `Border` 構成へ更新し、衝突行の枠・背景・補助文を赤系で表示するようにした。
- `AppThemeController` と `ThemeResources.xaml` に衝突表示用ブラシを追加し、ライト/ダークで見やすい色へ切り替えるようにした。
- `SettingsUiController` で hotkey 衝突中の保存を保留し、最後の有効な登録状態を維持するようにした。

### Files Touched
- `ViewModels/SettingsViewModel.cs` — hotkey 下書き値から重複を計算し、UI が参照する衝突状態を追加した。
- `UI/HotkeysControl.xaml` — hotkey 行を衝突表示しやすいレイアウトへ更新し、衝突サマリー表示を追加した。
- `UI/HotkeysControl.xaml.cs` — `SettingsViewModel` の衝突状態を監視し、各行の赤表示を切り替える処理を追加した。
- `UI/ThemeResources.xaml` — hotkey 衝突表示の既定ブラシを追加した。
- `Services/Application/AppThemeController.cs` — ライト/ダークテーマごとに hotkey 衝突ブラシを差し替えるようにした。
- `Services/Application/SettingsUiController.cs` — hotkey 衝突中は保存を進めないガードを追加した。
- `MainWindow.xaml.cs` — `ISettingsUiBridge` 経由で hotkey 衝突状態を `SettingsUiController` へ渡すようにした。

### Behavioral Impact
- 同一アプリ内で同じ hotkey 組み合わせを複数行に設定すると、該当行と説明文が赤く表示される。
- 衝突が解消されるまで新しい hotkey 設定は保存・適用されず、直前の有効な登録状態が維持される。
- ライトテーマとダークテーマの切り替え後も、衝突表示がそれぞれの背景で読める配色を使う。

### Risk & Mitigation
- Risk: hotkey 衝突中は他の設定変更も autosave で保留される。
- Mitigation: 画面上に常時サマリーを表示し、衝突を解消すべき状態を明示した。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln`

**2026-03-20 14:25 (Asia/Taipei) — Enable Google Web on first run defaults**

### Summary
- 初回起動時の既定設定で Google Web 翻訳を有効化した。

### Context / Goal
- API キー不要で初回から翻訳を体験できるようにしたかった。
- 既存ユーザー設定や一時的な `new AppSettings()` 利用箇所には影響させず、初回デフォルトだけを変えたかった。

### Changes
- `SettingsService.CreateFirstRunDefaults()` で `settings.EnableGoogleWeb = true;` を追加した。

### Files Touched
- `Services/SettingsService.cs` — 初回作成される `AppSettings` に対して Google Web を既定 ON にした。

### Behavioral Impact
- `settings.json` が存在しない初回起動では、Google Web 翻訳がデフォルトで有効になる。
- 既存の `settings.json` を持つユーザー設定や migration 挙動は変更しない。

### Risk & Mitigation
- Risk: Google Web は非公式 endpoint 依存のため、外部仕様変更時に初回既定が機能しなくなる可能性がある。
- Mitigation: 変更範囲を first-run defaults のみに限定し、既存ユーザー設定は上書きしない。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln`
- `git diff --check`（LF/CRLF warning のみ）

**2026-03-19 15:32 (Asia/Taipei) — Reorder overview quick display controls**

### Summary
- Overview の Quick Display Settings で、表示系スライダーと Fixed ROI 設定の順序を整理した。

### Context / Goal
- `Overlay opacity` が Fixed ROI 設定群と同じ列にあり、表示調整と配置設定の文脈が混ざっていた。
- `Overlay font size` と `Overlay opacity` を同じ列へまとめ、`Fixed ROI overlay mode` を右列の先頭へ移動したかった。

### Changes
- `Overlay opacity` のラベルとスライダーを左列へ移動した。
- `Fixed ROI overlay mode` を右列の先頭に残し、その下に target / user frame 操作群が続く並びへ整理した。

### Files Touched
- `UI/OverviewControl.xaml` — Quick Display Settings のコントロール順序だけを変更した。

### Behavioral Impact
- 機能やバインディングは変わらず、Overview 内の表示順だけが変わる。
- 表示調整系スライダーが左列にまとまり、Fixed ROI 設定の依存関係が右列で上から読めるようになる。

### Risk & Mitigation
- Risk: 既存スクリーンショットや手順書の見た目と位置がずれる。
- Mitigation: 機能変更は伴わず、ラベルと操作内容はそのまま維持した。

### Tests / Verification
- `dotnet build Hotkey-Translator.sln`

**2026-03-20 17:01 (Asia/Taipei) — Add OneOCR experiment scaffold and plan**

### Summary
- Snipping Tool 系 OneOCR を検証するための独立した実験フォルダと作成方針書を追加した。

### Context / Goal
- private OCR 実装を本体へ直接混ぜる前に、画像 1 枚から `text + bbox + confidence` を抜けるかを安全に切り分けたかった。
- 依存物や検証成果物を Git 管理対象外にしつつ、後続 PoC の前提と手順を明文化したかった。

### Changes
- `Tools/OneOcrExperiment/` を追加し、`input/`、`output/`、`vendor/` の作業ディレクトリを用意した。
- `Tools/OneOcrExperiment/README.md` に、実験のゴール、非ゴール、提案アーキテクチャ、CLI 入出力、実装手順、リスクを記載した。
- `Tools/OneOcrExperiment/.gitignore` で private DLL やテスト画像、出力成果物を Git 管理対象外にした。

### Files Touched
- `Tools/OneOcrExperiment/README.md` — OneOCR 実験の作成方針と手順を新規追加した。
- `Tools/OneOcrExperiment/.gitignore` — 実験依存物と生成物を除外する設定を追加した。
- `Tools/OneOcrExperiment/input/.gitkeep` — テスト画像置き場を初期化した。
- `Tools/OneOcrExperiment/output/.gitkeep` — OCR 結果出力先を初期化した。
- `Tools/OneOcrExperiment/vendor/.gitkeep` — private OCR 依存物の配置先を初期化した。

### Behavioral Impact
- 本体アプリの挙動やビルドには影響しない。
- リポジトリ内に OneOCR 検証用の独立作業領域が追加され、以後の PoC をこの配下へ限定できる。

### Risk & Mitigation
- Risk: README の方針と今後の PoC 実装が乖離する可能性がある。
- Mitigation: 実装は `Tools/OneOcrExperiment` 配下に閉じ、方針変更があれば同ディレクトリ内で更新する。

### Tests / Verification
- `Get-ChildItem Tools\OneOcrExperiment -Force -Recurse`

**2026-03-20 17:05 (Asia/Taipei) — Add UV-based OneOCR experiment implementation plan**

### Summary
- `uv` 前提で OneOCR 実験を進めるための実装案を `Doc/` に追加した。

### Context / Goal
- OneOCR の PoC をシステム Python 依存にせず、再現しやすい `uv` 実行環境で進める方針を先に固定したかった。
- 本体統合前に、CLI 契約、依存方針、実装ステップをドキュメントとして残したかった。

### Changes
- `Doc/OneOCR_Uv_Experiment_Implementation_Plan.md` を新規追加した。
- `uv` を使う理由、`pyproject.toml` / `probe.py` / `oneocr_bridge.py` の責務、`uv sync` / `uv run` の運用方針を明記した。

### Files Touched
- `Doc/OneOCR_Uv_Experiment_Implementation_Plan.md` — `uv` ベースの OneOCR 実験構成、CLI 契約、実装手順、リスクを新規記載した。

### Behavioral Impact
- 本体アプリの挙動には影響しない。
- OneOCR 実験は `uv` 起点で進める前提がドキュメント上で固定された。

### Risk & Mitigation
- Risk: 実装が進む中で Doc と実際の PoC 構成がずれる可能性がある。
- Mitigation: `Tools/OneOcrExperiment` 配下の実装追加時に、この計画書を同時更新する。

### Tests / Verification
- `Get-Content 'Doc\OneOCR_Uv_Experiment_Implementation_Plan.md' -Encoding UTF8 | Select-Object -First 40`

**2026-03-20 17:16 (Asia/Taipei) — Implement UV-based OneOCR experiment CLI**

### Summary
- `Tools/OneOcrExperiment` に `uv` ベースの OneOCR 実験 CLI と thin bridge を実装した。

### Context / Goal
- `Doc/OneOCR_Uv_Experiment_Implementation_Plan.md` に沿って、画像 1 枚から `text + bbox + confidence` を抽出する最小 PoC を実体化したかった。
- 先行実装の exported function 定義と画素フォーマットを踏襲しつつ、依存を最小化した独立実験環境を作りたかった。

### Changes
- `pyproject.toml`、`.python-version`、`uv.lock` を追加し、`uv` で再現可能な Python 実験環境を固定した。
- `oneocr_bridge.py` を追加し、`AuroraWright/oneocr` と `b1tg/win11-oneocr` を参考に private DLL の初期化、OCR 実行、line / word / polygon / confidence 抽出を実装した。
- `probe.py` を追加し、単一画像入力から JSON 出力と overlay 出力を行う CLI を実装した。
- `README.md` を実装後の実行手順に更新し、`bbox` の意味と `uv` コマンド例を明記した。
- `.gitignore` を更新し、`.venv/` を Git 管理対象外にした。

### Files Touched
- `Tools/OneOcrExperiment/pyproject.toml` — `uv` プロジェクト定義と `Pillow` 依存を追加した。
- `Tools/OneOcrExperiment/.python-version` — 実験用 Python バージョンを固定した。
- `Tools/OneOcrExperiment/uv.lock` — `uv sync` により依存ロックファイルを生成した。
- `Tools/OneOcrExperiment/oneocr_bridge.py` — OneOCR DLL の thin bridge と結果変換を追加した。
- `Tools/OneOcrExperiment/probe.py` — 画像入力、JSON 出力、overlay 出力を行う CLI を追加した。
- `Tools/OneOcrExperiment/README.md` — 実行手順と出力形式を実装に合わせて更新した。
- `Tools/OneOcrExperiment/.gitignore` — `.venv/` を除外対象に追加した。

### Behavioral Impact
- 本体アプリには影響しない。
- `.\Tools\uv\uv.exe run --project .\Tools\OneOcrExperiment ...` で独立 OneOCR CLI を実行できるようになった。
- `vendor/` に必須 DLL が無い場合は、自動フォールバックせず明示エラーで停止する。

### Risk & Mitigation
- Risk: private DLL の関数契約が Snipping Tool 更新で変わると、この bridge は壊れる。
- Mitigation: 実装を `Tools/OneOcrExperiment` 配下に閉じ、thin bridge に限定して差し替えしやすくした。
- Risk: 実機 DLL が未配置のため、OCR 成功ケースは未確認である。
- Mitigation: vendor 欠落時の fail-fast と CLI/構文検証までは通し、次段で実機ファイル配置後に OCR 実行確認を行う。

### Tests / Verification
- `.\Tools\uv\uv.exe sync --project .\Tools\OneOcrExperiment`
- `.\Tools\uv\uv.exe run --project .\Tools\OneOcrExperiment python .\Tools\OneOcrExperiment\probe.py --help`
- `.\Tools\uv\uv.exe run --project .\Tools\OneOcrExperiment python -m py_compile .\Tools\OneOcrExperiment\probe.py .\Tools\OneOcrExperiment\oneocr_bridge.py`
- `.\Tools\uv\uv.exe run --project .\Tools\OneOcrExperiment python .\Tools\OneOcrExperiment\probe.py --image .\Tools\OneOcrExperiment\output\smoke.png`（vendor 未配置時に明示エラーで停止することを確認）

**2026-03-20 17:26 (Asia/Taipei) — Add OneOCR integration direction document**

### Summary
- OneOCR を本体へ統合するための方針書を `Doc/` に追加した。

### Context / Goal
- OneOCR の実験実行と精度確認が終わったため、本体へどう差し込むかを既存アーキテクチャ前提で整理したかった。
- direct CLI 呼び出しではなく、既存の gRPC host パターンに合わせる実装方針を先に固定したかった。

### Changes
- `Doc/OneOCR_Integration_Implementation_Plan.md` を新規追加した。
- OneOCR を `uv + Python` の長寿命 gRPC host として統合する案、必要な設定項目、影響範囲、実装ステップ、リスクを記載した。

### Files Touched
- `Doc/OneOCR_Integration_Implementation_Plan.md` — OneOCR 本体統合の推奨アーキテクチャ、UI/設定方針、resource host 接続方針を新規記載した。

### Behavioral Impact
- 本体アプリの挙動には影響しない。
- OneOCR 統合は「manual vendor 配置前提の long-lived local gRPC host」で進める方針が文書化された。

### Risk & Mitigation
- Risk: 実装時に一部設計が変わり、Doc とズレる可能性がある。
- Mitigation: 実装着手時はこの方針書を基準にし、必要な設計変更は同ファイルへ追記して同期を保つ。

### Tests / Verification
- `Get-Content 'Doc\OneOCR_Integration_Implementation_Plan.md' -Encoding UTF8 | Select-Object -First 80`

**2026-03-20 17:33 (Asia/Taipei) — Add OneOCR native helper integration plan**

### Summary
- OneOCR を `small native helper process` で統合するための実装案を `Doc/` に追加した。

### Context / Goal
- `gRPC + Python` より軽い統合方式として、OneOCR 専用の native helper を採る案を具体化したかった。
- private DLL を本体から隔離しつつ、Python/uv 依存を増やさない統合方針を先に固定したかった。

### Changes
- `Doc/OneOCR_NativeHelper_Integration_Plan.md` を新規追加した。
- native helper の責務、Named Pipe ベース IPC、helper / C# 側の分割、設定項目、実装ステップ、リスクを記載した。

### Files Touched
- `Doc/OneOCR_NativeHelper_Integration_Plan.md` — OneOCR を native helper process で統合する推奨アーキテクチャと実装方針を新規記載した。

### Behavioral Impact
- 本体アプリの挙動には影響しない。
- OneOCR の推奨統合方式として `native helper process + Named Pipe` 案がドキュメント化された。

### Risk & Mitigation
- Risk: 実装時に helper の IPC 方式やディレクトリ配置が変わる可能性がある。
- Mitigation: 実装着手時はこの計画書を基準にし、変更が必要なら同ドキュメントへ反映して同期を保つ。

### Tests / Verification
- `Get-Content 'Doc\OneOCR_NativeHelper_Integration_Plan.md' -Encoding UTF8 | Select-Object -First 120`

**2026-03-20 17:41 (Asia/Taipei) — Refine OneOCR native helper plan for merge-skip integration**

### Summary
- `OneOCR` を重い共通 OCR 枠へ寄せすぎない前提に合わせて、native helper 統合案を整理し直した。

### Context / Goal
- OneOCR は実測上すでに line 単位が十分まとまっており、既存 OCR 向けの枠統合や merge tuning をそのまま当てる必要が薄いことを文書へ反映したかった。
- `OneOCR` 専用 helper、軽量 supervisor、`OcrAndGroupStage` の merge skip を軸に、全体の整合を取りたかった。

### Changes
- `Doc/OneOCR_NativeHelper_Integration_Plan.md` を更新し、OneOCR を `PaddleVllm / VisionLlm` と同様の merge skip エンジンとして扱う方針を追記した。
- OneOCR のライフサイクルは `ResourceHostFacade` の重い gRPC 前提へ無理に統合せず、専用 supervisor を第一候補とする記述へ整理した。
- UI/設定は OneOCR 専用の最小構成に寄せ、`Restart OCR host` 便乗ではなく helper 専用再起動経路を推奨する文言へ修正した。

### Files Touched
- `Doc/OneOCR_NativeHelper_Integration_Plan.md` — merge skip 前提、専用 supervisor、最小 UI/設定方針に合わせて全体の整合を更新した。

### Behavioral Impact
- 本体アプリの挙動には影響しない。
- OneOCR 統合方針は「native helper + Named Pipe + merge skip」が明確な前提として固定された。

### Risk & Mitigation
- Risk: 実装時に helper と UI の責務分割がさらに簡略化される可能性がある。
- Mitigation: 変更があっても、この文書の中で OneOCR 専用最小統合という軸を維持して更新する。

### Tests / Verification
- `Get-Content 'Doc\OneOCR_NativeHelper_Integration_Plan.md' -Encoding UTF8`

**2026-03-20 18:31 (Asia/Taipei) — Implement OneOCR native helper integration**

### Summary
- OneOCR native helper process を実装し、WPF 本体から選択可能な OCR エンジンとして統合した。

### Context / Goal
- `Doc/OneOCR_NativeHelper_Integration_Plan.md` の方針どおり、private `oneocr.dll` を本体へ直結せず別プロセスへ隔離したかった。
- OneOCR の完成済み line geometry を既存パイプラインへ最小差分で流し込み、既存 merge を過剰適用しない構成にしたかった。

### Changes
- `Native/OneOcrHelper/` を追加し、`oneocr.dll` をロードする C++ helper、WIC PNG decode、Named Pipe length-prefixed JSON IPC を実装した。
- C# 側に `OneOcrProtocolClient`、`OneOcrProcessHost`、`OneOcrProcessOcrProvider` を追加し、helper 起動・ready 待機・PNG 送信・OCR 応答の `OcrResultModel` 変換を実装した。
- `OcrEngineKind.OneOcr`、`AppSettings` の OneOCR 設定、UI 選択肢、概要表示、`OcrEngine` 分岐、`OcrAndGroupStage` の merge skip を追加した。
- native build / dist 手順に OneOCR helper target と配布物コピーを追加した。

### Files Touched
- `Native/OneOcrHelper/CMakeLists.txt` — OneOCR helper の native target と出力先を追加。
- `Native/OneOcrHelper/main.cpp` — OneOCR DLL bridge、WIC decode、Named Pipe JSON protocol、OCR 実行ループを実装。
- `Native/OneOcrHelper/README.md` — vendor 配置前提を明記。
- `Native/OneOcrHelper/.gitignore` — vendor 配下を ignore し `.gitkeep` のみ残すようにした。
- `Native/CMakeLists.txt` — `OneOcrHelper` subdirectory を追加。
- `Services/OneOcrProtocolClient.cs` — length-prefixed JSON の送受信と protocol DTO を追加。
- `Services/OneOcrProcessHost.cs` — helper の起動・接続・再起動・終了管理を追加。
- `Services/OneOcrProcessOcrProvider.cs` — bitmap を PNG 化して helper 結果を `OcrResultModel` に変換する provider を追加。
- `Services/OcrEngine.cs` — `OcrEngineKind.OneOcr` 分岐と dispose を追加。
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` — OneOCR を merge skip 対象へ追加。
- `Models/OcrEngineKind.cs` — `OneOcr = 6` を追加。
- `Models/AppSettings.cs` — helper path / vendor path / pipe name / timeout / max line count を追加。
- `ViewModels/SettingsViewModel.cs` — `OneOcr` の load/apply マッピングを追加。
- `ViewModels/MainWindowViewModel.cs` — OneOCR の概要表示名を追加。
- `UI/OverviewControl.xaml` — OCR engine selector に OneOCR を追加。
- `MainWindow.xaml.cs` — OneOCR helper バイナリ欠落の前提チェックを追加。
- `Cpp_Build.md` — OneOCR helper target の build 手順を追記。
- `build-dist.ps1` — OneOCR helper binary と vendor stub の配布コピーを追加。

### Behavioral Impact
- 設定 UI から `OneOCR (native helper)` を選べるようになり、選択時は native helper 経由で OCR を実行する。
- OneOCR の line 結果は `PaddleOCR-VL / VisionLLM` と同様に追加 merge をスキップするため、helper 側の geometry をそのまま活かす。
- helper または OneOCR 実行が失敗した場合は既存方針どおり WinRT へ fallback する。

### Risk & Mitigation
- Risk: private OneOCR DLL の更新で export や戻り値仕様が変わると helper が起動不能になる。
- Mitigation: helper 起動時に vendor / export を fail fast で検証し、アプリ側はエラーをログ化して WinRT fallback する。
- Risk: 配布物に vendor ファイルを含めないため、helper binary だけでは OneOCR は動かない。
- Mitigation: `README.md` と dist note で manual vendor 配置前提を明示し、helper / model 欠落は事前チェックで通知する。

### Tests / Verification
- `cmake -S Native -B Native/build -A x64`
- `cmake --build Native/build --config Release --target OneOcrHelper`
- `dotnet build .\Hotkey-Translator.sln`
- `Copy-Item .\Tools\OneOcrExperiment\vendor\{oneocr.dll,oneocr.onemodel,onnxruntime.dll} .\Native\OneOcrHelper\vendor`
- OneOCR helper を起動し、Named Pipe 経由で `Tools\OneOcrExperiment\input\test.png` を送信して `ok=true / 20 lines / 2427x989 / durationMs=289.209` を確認

**2026-03-20 18:40 (Asia/Taipei) — Fix OneOCR helper packaging for x64-only native builds**

### Summary
- OneOCR helper を x64 native build のみに限定し、`build-dist.ps1` が最後まで成功するように修正した。

### Context / Goal
- 配布確認で `build-dist.ps1` の Win32 native build でも `OneOcrHelper` がビルド対象に入り、x64 と同じ出力先へリンクしようとして失敗していた。
- OneOCR helper は現状 x64 配布専用なので、Win32 tree から外すのが最小で整合的だった。

### Changes
- `Native/CMakeLists.txt` で `OneOcrHelper` を `CMAKE_SIZEOF_VOID_P == 8` のときだけ追加するようにした。
- 修正後に `build-dist.ps1` を再実行し、OneOCR helper を含む dist 作成が完了することを確認した。

### Files Touched
- `Native/CMakeLists.txt` — OneOCR helper を x64 native build のみで生成する条件分岐を追加。

### Behavioral Impact
- `build-dist.ps1` 実行時、x86 native build と OneOCR helper の出力衝突が起きなくなった。
- dist には `Native\OneOcrHelper\bin\OneOcrHelper.exe` が含まれ、vendor は `.gitkeep` のみで手動配置前提を維持する。

### Risk & Mitigation
- Risk: 将来 x86 helper を本当に必要にしたとき、この条件分岐だけでは足りない。
- Mitigation: 現状の WPF 配布は x64 前提なので helper も x64 専用に固定し、x86 対応が必要になった時点で出力先と packaging を別設計にする。

### Tests / Verification
- `powershell -ExecutionPolicy Bypass -File .\build-dist.ps1`
- `Get-ChildItem .\dist\Hotkey-Translator-online\Native\OneOcrHelper -Recurse -Force`
- `Test-Path .\dist\Hotkey-Translator-online\Native\OneOcrHelper\bin\OneOcrHelper.exe`
- `Test-Path .\dist\Hotkey-Translator-online\Native\OneOcrHelper\vendor\.gitkeep`
- `Test-Path .\dist\Hotkey-Translator-online\Native\OneOcrHelper\vendor\oneocr.dll` が `False` であることを確認

**2026-03-20 18:48 (Asia/Taipei) — Fix OneOCR request JSON casing mismatch**

### Summary
- OneOCR helper への request JSON が PascalCase で送られていた問題を修正し、helper の lower camel-case 契約に合わせた。

### Context / Goal
- 実行ログで `OneOCR helper failed: Unsupported request type.` が出ており、helper は起動しているのに OCR request だけ解釈できていなかった。
- helper 側は `type` / `imageBytesBase64` / `maxLineCount` を文字列検索しているため、C# 側の JSON キー名と一致させる必要があった。

### Changes
- `Services/OneOcrProtocolClient.cs` の `JsonSerializerOptions` に `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` を追加した。

### Files Touched
- `Services/OneOcrProtocolClient.cs` — helper へ送る request JSON を camelCase でシリアライズするよう修正。

### Behavioral Impact
- OneOCR helper は `type=recognize` を正しく読めるようになり、`Unsupported request type.` で即 fallback する挙動が解消する見込み。

### Risk & Mitigation
- Risk: 将来 protocol DTO に新しいフィールドを足したとき、helper 側と casing 契約が再びずれる可能性がある。
- Mitigation: OneOCR protocol は camelCase JSON を前提とし、送受信の serializer option をこのクライアントに固定した。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln`
- ログ原因の確認: helper は起動済みで、`Unsupported request type.` は request JSON の `type` 不一致で説明できることを確認

**2026-03-20 18:53 (Asia/Taipei) — Fix OneOCR JSON unicode escaping in base64 payloads**

### Summary
- OneOCR request JSON の base64 が `\uXXXX` へ再エスケープされないようにし、helper の簡易 JSON parser と整合させた。

### Context / Goal
- 新しい実行ログでは `OneOCR helper failed: Unsupported JSON escape sequence.` が出ており、request の `type` 解釈は通ったが base64 文字列のパースで失敗していた。
- `System.Text.Json` の既定 encoder は base64 内の一部文字を `\u002B` などへ変換しうるため、`\u` を未対応の helper parser が落としていた。

### Changes
- `Services/OneOcrProtocolClient.cs` の `JsonSerializerOptions` に `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` を追加した。

### Files Touched
- `Services/OneOcrProtocolClient.cs` — helper へ送る JSON で base64 payload が不要に `\uXXXX` へ変換されないよう修正。

### Behavioral Impact
- OneOCR helper は `imageBytesBase64` をそのまま読めるようになり、`Unsupported JSON escape sequence.` による即 fallback が解消する見込み。

### Risk & Mitigation
- Risk: encoder を緩めると JSON 文字列のエスケープ量が減る。
- Mitigation: この protocol はローカル helper 向けの内部 IPC に限定され、HTML へ埋め込む用途ではないため `UnsafeRelaxedJsonEscaping` の方が適切。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln`
- ログ原因の確認: `Unsupported JSON escape sequence.` は helper 側の `\u` 未対応と request base64 payload の再エスケープで説明できることを確認

**2026-03-20 19:04 (Asia/Taipei) — Add OneOCR line merge enablement plan**

### Summary
- OneOCR を `merge skip` ではなく shared line merge へ流すための新規計画書を `Doc/` に追加した。

### Context / Goal
- `test2.png` 比較で Python CLI と native helper の raw 出力が一致し、枠結合の弱さは wrapper 差ではなくアプリ後段の merge 方針で説明できる状態になった。
- 既存の native helper 計画書は維持したまま、この件だけを切り出した新規実装方針が必要だった。

### Changes
- `Doc/OneOCR_LineMerge_Enablement_Plan.md` を新規追加した。
- OneOCR を shared merge 経路へ戻す背景、実装差分、検証ステップ、リスクを整理した。

### Files Touched
- `Doc/OneOCR_LineMerge_Enablement_Plan.md` — OneOCR line merge 有効化専用の新規実装計画書を追加。

### Behavioral Impact
- アプリ動作自体にはまだ影響しない。
- 実装時の方針として、OneOCR を `merge skip` から shared merge へ切り替える前提が明文化された。

### Risk & Mitigation
- Risk: 既存計画書と新規計画書の方針が一時的に並立する。
- Mitigation: 今回は既存文書を変更せず、line merge 論点だけを新規計画書に分離して判断しやすくした。

### Tests / Verification
- `Get-Content -Path .\Doc\OneOCR_LineMerge_Enablement_Plan.md -Encoding UTF8`

**2026-03-20 19:08 (Asia/Taipei) — Enable shared line merge for OneOCR**

### Summary
- OneOCR を `merge skip` 対象から外し、WinRT と同じ shared line merge 経路へ戻した。

### Context / Goal
- `test2.png` 比較で Python CLI と native helper の raw line 出力が一致し、OneOCR の枠結合の弱さは wrapper 差ではなく app 後段の merge 方針で説明できる状態だった。
- OneOCR を `PaddleVllm / VisionLlm` と同列の skip 扱いにするのをやめ、既存 `_lineGrouper.MergeLines(...)` を適用したかった。

### Changes
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` から `OcrEngineKind.OneOcr` を merge skip 条件から外した。
- コメントも `PaddleOCR-VL / VisionLLM` のみが skip 対象である内容に合わせて更新した。

### Files Touched
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` — OneOCR を shared line merge 経路へ戻すよう条件分岐を修正。

### Behavioral Impact
- OneOCR 実行時、helper raw line はそのまま overlay へ流れず、既存 line grouper を通る。
- `test2.png` のようなケースでは WinRT に近い line 結合結果になる見込み。

### Risk & Mitigation
- Risk: 一部画像では shared merge が OneOCR 行を過剰結合する可能性がある。
- Mitigation: まず既存 merge のみ適用し、問題が残るケースが出たら OneOCR 専用 tuning を別途検討する。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln`

**2026-03-20 19:15 (Asia/Taipei) — Tune horizontal merge strength for vertical paragraph gaps**

### Summary
- Horizontal Merge Strength が横書き時の上下結合コストにも効くよう simple merge tuning を調整した。

### Context / Goal
- Horizontal Merge Strength を 50 以下へ下げても、横書きの上下方向結合がほとんど弱まらない挙動があった。
- 既存の simple merge tuning は overlap / threshold / row gap だけを変え、上下結合コストの `MergeVerticalWeight` は固定のままだった。

### Changes
- `Services/OcrLineGrouper.cs` に `ResolveHorizontalVerticalWeight` を追加した。
- `ApplySimpleMergeTuning` で `HorizontalMergeStrength` から `MergeVerticalWeight` も上書きするよう変更した。
- simple merge tuning ログに `weight` を追加し、実効値を追跡しやすくした。
- 低い Horizontal Merge Strength ほど vertical gap のペナルティが強くなるカーブを追加した。

### Files Touched
- `Services/OcrLineGrouper.cs` — horizontal simple merge tuning に `MergeVerticalWeight` の解決関数と適用処理、ログ出力を追加した。

### Behavioral Impact
- 横書き時、Horizontal Merge Strength を下げると同一行内の横結合だけでなく、行どうしの上下結合も従来より明確に弱くなる。
- 特に 50 以下で、段落的な縦結合が以前より保守的になる。

### Risk & Mitigation
- Risk: 既存設定で、これまで 1 ブロックにまとまっていた行が分割されるケースが増える可能性がある。
- Mitigation: 変更は simple merge tuning 有効時だけに限定し、ログへ `weight` を出して実効閾値を確認できるようにした。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln`
- 実効値確認: Horizontal Merge Strength の weight は `0=5.000`, `25=0.981`, `50=0.743`, `75=0.537`, `100=0.100` を確認した。

**2026-03-20 19:37 (Asia/Taipei) — Rename writing-mode merge strength labels**

### Summary
- OCR 設定 UI の merge strength ラベルを writing mode 基準の名称へ変更した。

### Context / Goal
- `Horizontal Merge Strength` と `Vertical Merge Strength` は方向ではなく writing mode に対応しており、名称が挙動を誤解させやすかった。
- UI 上で、横書き時の merge 全体 / 縦書き時の merge 全体だと分かる名称へ揃えたかった。

### Changes
- `Horizontal merge strength` を `Horizontal writing merge strength` に変更した。
- `Vertical merge strength` を `Vertical writing merge strength` に変更した。

### Files Touched
- `UI/OcrSettingsControl.xaml` — simple merge tuning の 2 つのスライダーラベルを writing mode 基準の文言へ更新した。

### Behavioral Impact
- UI 表示のみの変更で、設定値や merge ロジックの動作自体は変わらない。
- ユーザーはスライダーが「方向」ではなく「横書き / 縦書きモード」の merge 強度を表すと理解しやすくなる。

### Risk & Mitigation
- Risk: 既存ユーザーが旧名称に慣れていて一時的に違和感を持つ可能性がある。
- Mitigation: 用語は挙動により近く、補助 NOTE も残っているため、実際の意味は従来より分かりやすい。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln` を実行したが、起動中の `Hotkey-Translator.exe` によるファイルロックで最終コピーに失敗した。
- 変更箇所は `UI/OcrSettingsControl.xaml` の表示文字列のみであることを確認した。

**2026-03-20 19:50 (Asia/Taipei) — Rebase merge defaults to strength 35**

### Summary
- simple merge tuning の既定基準を 50 から 35 へ移し、OFF 時の granular default も ON+35 と揃えた。

### Context / Goal
- 検証上、merge strength は 35 付近が最も扱いやすく、現在の 50 基準は実運用とズレていた。
- `EnableSimpleMergeTuning=false` の既定挙動と `EnableSimpleMergeTuning=true` かつ slider=35 の挙動を一致させたかった。

### Changes
- `AppSettings` の merge 関連 default を slider=35 相当へ更新した。
- `HorizontalMergeStrength` / `VerticalMergeStrength` の default と clamp fallback を 35 に変更した。
- `SettingsViewModel` の初期表示値も 35 に揃えた。
- `OcrLineGrouper` の vertical Stage A 固定閾値を 35 基準へ更新し、simple tuning OFF の baseline と一致させた。

### Files Touched
- `Models/AppSettings.cs` — merge strength 既定値と、simple tuning OFF 時に使う granular threshold default を 35 相当へ更新した。
- `ViewModels/SettingsViewModel.cs` — merge strength スライダーの初期表示値を 35 に変更した。
- `Services/Settings/Rules/WritingModeSettingsRule.cs` — 設定欠損時の clamp fallback を 35 に変更した。
- `Services/OcrLineGrouper.cs` — vertical writing baseline の固定閾値を 35 基準へ更新し、理由コメントを追加した。

### Behavioral Impact
- 新規設定や欠損設定では、merge の基準点が 50 ではなく 35 になる。
- simple merge tuning を OFF から ON に切り替えても、slider=35 のときは従来より挙動差が出にくくなる。
- 既存の settings.json に保存済みの個別値は自動変更されない。

### Risk & Mitigation
- Risk: 新規ユーザーと既存保存設定なしの環境で、merge の見え方が従来の 50 基準より少し保守的になる可能性がある。
- Mitigation: 変更後の既定値は実測済みの 35 基準に合わせており、simple tuning ON/OFF の整合性も改善している。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -p:UseAppHost=false`
- 35 基準値確認: `OFF` default が `ON + slider=35` の解決値 (`MergeOverlapRatioThreshold=0.129560`, `MergeVerticalWeight=0.880399`, `MergeThresholdRatio=0.961320` など) と一致することを確認した。
**2026-03-20 21:01 (Asia/Taipei) — Auto-provision OneOCR vendor files on selection**

### Summary
- `OneOCR` を選択した時、vendor DLL/モデルが無ければダイアログ確認後に Snipping Tool パッケージから自動コピーするようにした。

### Context / Goal
- `OneOCR` は helper と vendor 3 ファイルに依存するが、既存実装は helper 側で遅延失敗しやすく、選択時に不足を解消できなかった。
- `OneOCR` を選んだ時点で不足ファイルを補完し、失敗時は設定を元に戻せるようにしたかった。

### Changes
- settings save フローに `OneOCR` vendor 補完ガードを追加し、不足解消が完了しなければ保存をロールバックするようにした。
- Snipping Tool の installed package を `PackageManager` で検出し、`oneocr.dll` / `oneocr.onemodel` / `onnxruntime.dll` を vendor フォルダへコピーする provisioner を追加した。
- ユーザー確認ダイアログ、busy overlay、失敗時メッセージ、ログ出力をまとめた `OneOcrVendorUiController` を追加した。

### Files Touched
- `Services/Application/OneOcrVendorProvisioner.cs` — Snipping Tool パッケージ検出、OneOCR vendor 状態判定、3 ファイルのコピーと再検証を追加した。
- `Services/Application/OneOcrVendorUiController.cs` — `OneOCR` 選択時の確認ダイアログ、busy overlay、失敗通知、ログ出力を追加した。
- `Services/Application/SettingsUiController.cs` — settings save 中に `OneOCR` vendor 準備を必須化し、未完了時に設定をロールバックするようにした。
- `MainWindow.xaml.cs` — `ISettingsUiBridge` 経由で `OneOcrVendorUiController` を呼び出す配線を追加した。

### Behavioral Impact
- `OneOCR` を選択して vendor ファイルが不足している場合、確認ダイアログが出て、OK なら Snipping Tool から自動コピーされる。
- helper 未配置、Snipping Tool 未インストール、コピー失敗のいずれでも設定変更は元に戻り、`OneOCR` が半端な状態で有効化されない。

### Risk & Mitigation
- Risk: Snipping Tool の private ファイル配置や package API の挙動が将来変わると自動コピーが失敗する。
- Mitigation: package 未検出、source 側欠落、copy 失敗を区別して明示メッセージを出し、設定は fail fast でロールバックする。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -c Release`

**2026-03-21 10:11 (Asia/Taipei) — Document ForceGemini image layout translation plan**

### Summary
- `ForceGeminiStrict` を OCR スキップの Gemini 画像直送モードへ変える実装案を `Doc/` に追加した。

### Context / Goal
- `Force Gemini` を OCR 後の text translation 固定ではなく、ROI 画像を Gemini に直接渡してレイアウト保持翻訳させる機能へ変えたかった。
- 実装前に pipeline 分岐、Gemini API、overlay 表示方針を整理したかった。

### Changes
- `Doc/ForceGemini_ImageLayoutTranslation_Implementation_Plan.md` を追加し、専用 pipeline 分岐、Gemini image API、single-block overlay 方針を整理した。
- v1 は座標要求なし、ROI 全体単一ブロック表示、通常 OCR 経路非変更を前提とすることを明記した。

### Files Touched
- `Doc/ForceGemini_ImageLayoutTranslation_Implementation_Plan.md` — ForceGemini 画像直送翻訳の実装案を新規追加した。

### Behavioral Impact
- 実装は未着手で、現行アプリの挙動に変化はない。
- 今後の実装方針として、`TranslationFallbackService` ではなく `PipelineOrchestrator` の専用分岐で扱う方針が明確になった。

### Risk & Mitigation
- Risk: 現行の `ForceGeminiStrict` の意味変更による利用者認識ズレがある。
- Mitigation: ドキュメント上で hotkey 意味変更と単一ブロック overlay の制約を先に明記した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）

**2026-03-21 10:22 (Asia/Taipei) — Implement ForceGemini image layout translation mode**

### Summary
- `ForceGeminiStrict` を OCR スキップの Gemini 画像直送モードとして実装した。

### Context / Goal
- `Force Gemini` を OCR 後の text translation 固定ではなく、ROI 画像を Gemini に直接渡してレイアウト保持翻訳させたかった。
- 座標要求なしで、既存 overlay へ最小差分で載せる必要があった。

### Changes
- `GeminiClient` に ROI bitmap を PNG/base64 で Gemini へ送り、plain text のレイアウト保持翻訳を返す `TranslateImagePreservingLayoutAsync(...)` を追加した。
- `PipelineOrchestrator` に `ForceGeminiStrict` 専用分岐を追加し、OCR / diff / translate stage を通さず、ROI 全体を覆う synthetic reading unit 1 件として overlay へ流すようにした。
- hotkey ログ文言を新しい挙動に合わせて更新し、`PipelineOrchestrator` 生成時に `GeminiClient` を直接注入するようにした。

### Files Touched
- `Services/GeminiClient.cs` — Gemini 画像直送 API、画像用 prompt、plain text 応答整形を追加した。
- `Services/PipelineOrchestrator.cs` — `ForceGeminiStrict` 時の OCR バイパス分岐と single-block overlay 経路を追加した。
- `Services/Application/HotkeyCommandController.cs` — hotkey 実行ログを OCR スキップ画像モード向けに更新した。
- `MainWindow.xaml.cs` — `GeminiClient` を `PipelineOrchestrator` へ渡すようにした。

### Behavioral Impact
- `ForceGeminiStrict` 実行時は OCR を行わず、ROI 画像を Gemini へ直接送って翻訳結果を ROI 全体 single block として表示する。
- 通常 OCR モード、通常の translation provider fallback、VisionLLM など既存経路の挙動は変わらない。

### Risk & Mitigation
- Risk: 座標なしのため、複数吹き出しや複雑なレイアウトでも ROI 全体単一ブロック表示になる。
- Mitigation: v1 は single-block overlay に限定し、座標復元は行わない前提を維持した。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -c Release`

**2026-03-20 21:23 (Asia/Taipei) — Reorder OCR engine combo so OneOCR follows WinRT**

### Summary
- 通常 OCR のコンボボックスで `OneOCR` を `WinRT` の直後へ移動した。

### Context / Goal
- 通常 OCR の選択肢表示順を見直し、`OneOCR` を `WinRT` のすぐ後に置きたかった。
- 設定保存値や enum 互換性は変えず、UI の表示順だけを調整したかった。

### Changes
- 通常 OCR コンボボックスの `ComboBoxItem` 並び順を変更した。
- `Tag` 値は維持し、表示順だけを変更した。

### Files Touched
- `UI/OverviewControl.xaml` — OCR engine コンボボックスで `OneOCR (native helper)` を `WinRT (Windows)` の直後へ移動した。

### Behavioral Impact
- 通常 OCR のドロップダウン表示順が `WinRT -> OneOCR -> PaddleOCR -> PaddleOCR-VL -> NDLOCR-Lite -> VisionLLM` になる。
- 保存値やロード処理には影響しない。

### Risk & Mitigation
- Risk: `OneOCR` が上位に見えることで未セットアップ時の選択が増える可能性がある。
- Mitigation: `Tag` 値は変更しておらず、既存の prerequisite / vendor setup 導線はそのまま機能する。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -c Release`

**2026-03-20 21:18 (Asia/Taipei) — Add OneOCR as VisionLLM hybrid base OCR**

### Summary
- VisionLLM の geometry assist hybrid OCR で `OneOCR` を補助 OCR として選べるようにした。

### Context / Goal
- VisionLLM の text を primary のまま使いつつ、geometry 補助 OCR に `OneOCR` を使いたかった。
- `OneOCR` は helper/vendor 依存があるため、主 OCR だけでなく hybrid base engine として使う場合も prerequisite と vendor setup が必要だった。

### Changes
- `VisionGeometryHybridBaseEngineKind` に `OneOcr` を追加し、settings / UI から選択できるようにした。
- Vision hybrid geometry provider 解決と effective engine kind 解決に `OneOCR` を追加した。
- `OneOCR` vendor setup と prerequisite dialog の発火条件を拡張し、VisionLLM hybrid base engine が `OneOcr` の時にも helper/vendor を要求するようにした。

### Files Touched
- `Models/VisionGeometryHybridBaseEngineKind.cs` — `OneOcr = 3` を追加し、既存 enum 値の互換性を維持した。
- `ViewModels/SettingsViewModel.cs` — hybrid base OCR の load/apply switch に `OneOcr` を追加した。
- `UI/VisionLlmSettingsControl.xaml` — VisionLLM hybrid base OCR のコンボに `OneOCR (native helper)` を追加した。
- `Services/OcrEngine.cs` — Vision geometry provider 解決で `OneOCR` provider を返せるようにした。
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` — hybrid geometry の effective engine kind として `OneOcr` を返すようにした。
- `Services/Application/OneOcrVendorUiController.cs` — `OneOCR` が VisionLLM hybrid 補助 OCR として必要な場合も vendor setup を実行するようにした。
- `MainWindow.xaml.cs` — prerequisite dialog の helper チェックを VisionLLM hybrid + `OneOcr` にも適用するようにした。

### Behavioral Impact
- VisionLLM 設定で `Hybrid base OCR` に `OneOCR (native helper)` を選べる。
- VisionLLM 本体を使う設定でも、補助 OCR が `OneOCR` の場合は helper / vendor が不足していれば保存時に補完または失敗ロールバックされる。

### Risk & Mitigation
- Risk: Snipping Tool private 依存の `OneOCR` が Vision hybrid 経路でも失敗しうる。
- Mitigation: helper/vendor は設定保存時に事前確認し、実行時は既存の Vision hybrid fallback 経路を維持する。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -c Release`

**2026-03-21 02:04 (Asia/Taipei) — Document Japanese ruby OCR handling plan**

### Summary
- 日本語ルビ対処のおすすめ実装案を `Doc/` に新規追加した。

### Context / Goal
- ルビが本文 merge や翻訳入力へ混入する問題に対して、実装前に方針を固定したかった。
- OCR 直後・line merge 前にルビ候補を分離する案を、最小スコープで整理することを目標にした。

### Changes
- `Doc/JapaneseRuby_OcrHandling_Implementation_Plan.md` を追加し、ゴール、非ゴール、判定方針、差し込み位置、リスクを整理した。
- v1 を「ルビを翻訳入力から除外する」に限定し、overlay 再表示や完全復元は非ゴールとして明記した。

### Files Touched
- `Doc/JapaneseRuby_OcrHandling_Implementation_Plan.md` — 日本語ルビ対処の実装案を新規追加した。

### Behavioral Impact
- 実装は未着手で、アプリ挙動の変化はない。
- 今後の実装時に `OcrAndGroupStage` 前処理として差し込む方針が明確になった。

### Risk & Mitigation
- Risk: ドキュメントだけが先行し、実装時に現物とズレる可能性がある。
- Mitigation: v1 の責務を限定し、I/F 変更なしの前処理案として記載した。

### Tests / Verification
- 未実施（ドキュメント追加のみ）

**2026-03-21 02:13 (Asia/Taipei) — Implement Japanese ruby filtering before OCR line merge**

### Summary
- 日本語 OCR の line merge 前にルビ候補を分離し、本文だけを merge / translation input に流す処理を追加した。

### Context / Goal
- ルビが本文 line merge を壊し、翻訳入力に混ざるケースを減らしたかった。
- `Doc/JapaneseRuby_OcrHandling_Implementation_Plan.md` の v1 方針に沿って、最小スコープで実装したかった。

### Changes
- `RubyCandidateDetector` を追加し、日本語限定で geometry と文字種の軽量 heuristic によるルビ候補判定を実装した。
- `OcrAndGroupStage` に detector を統合し、通常 OCR と geometry helper OCR で本文 line のみを `_lineGrouper.MergeLines(...)` に流すようにした。
- ルビ候補が除外された場合の既存 logger 向け情報ログを追加した。

### Files Touched
- `Services/RubyCandidateDetector.cs` — 日本語ルビ候補を本文から分離する detector と結果 record を追加した。
- `Services/Orchestration/Stages/OcrAndGroupStage.cs` — line merge 前にルビ検出を呼び出し、本文のみを merge / reading unit 化するようにした。

### Behavioral Impact
- `SourceLanguage` が日本語の OCR では、かな中心で小さい注釈 box が本文の上側または右側に付くケースで、translation input から外れるようになった。
- `VisionLLM` / `PaddleOCR-VL` の coarse block 経路は直接の対象外にし、geometry helper 側だけ本文抽出に反映する。

### Risk & Mitigation
- Risk: 小さい注釈や UI 小文字列をルビと誤判定する可能性がある。
- Mitigation: 日本語限定、かな比率、位置関係、サイズ差、本文側の漢字存在を複合条件にして、迷うケースは除外しない実装にした。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -c Release`

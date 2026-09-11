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

**2026-03-21 11:52 (Asia/Taipei) — Rename ForceGemini UI label to Gemini Image Translate**

### Summary
- `ForceGeminiStrict` の UI 表示名を `Gemini Image Translate` に変更した。

### Context / Goal
- 現在の機能内容が「Force Gemini」より「Gemini 画像直送翻訳」に近く、UI 名称を合わせたかった。
- まずは内部名を変えず、ユーザーに見える表示文言だけを調整したかった。

### Changes
- ホットキー設定画面のラベル文言を `Gemini Image Translate` に変更した。
- ホットキー一覧の表示名を `Gemini Image Translate` に変更した。

### Files Touched
- `UI/HotkeysControl.xaml` — ホットキー設定のラベルを `Gemini Image Translate` に変更した。
- `MainWindow.xaml.cs` — ホットキー一覧向け表示名を `Gemini Image Translate` に変更した。

### Behavioral Impact
- ユーザーに見えるホットキー名が `Gemini Image Translate` になる。
- 内部キー名、設定保存キー、処理ロジックには影響しない。

### Risk & Mitigation
- Risk: ログや内部名との用語差が一時的に残る。
- Mitigation: 今回は UI 名だけに限定し、内部リネームは別タスクに分けた。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -c Release`

**2026-03-21 10:47 (Asia/Taipei) — Raise ForceGemini image path to low thinking budget**

### Summary
- `ForceGemini` の画像直送経路だけ `Thinking Low` 相当の budget に変更した。

### Context / Goal
- 画像直送の Gemini 応答で、text 経路は変えずに少しだけ reasoning を増やしたかった。
- 通常の text translation schema 応答には影響を出したくなかった。

### Changes
- `GeminiClient.TranslateImagePreservingLayoutAsync(...)` の `generationConfig.thinkingConfig.thinkingBudget` を `0` から `1024` に変更した。
- text translation 側の `thinkingBudget = 0` はそのまま維持した。

### Files Touched
- `Services/GeminiClient.cs` — 画像直送の Gemini request だけ low thinking budget に変更した。

### Behavioral Impact
- `ForceGeminiStrict` の画像直送モードでのみ、Gemini が少量の reasoning budget を使うようになる。
- 通常の text translation 経路の latency / schema 応答挙動は変わらない。

### Risk & Mitigation
- Risk: 画像経路の応答時間が少し増える可能性がある。
- Mitigation: budget は小さめに留め、thought text は引き続き非表示にしている。

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
**2026-03-21 12:34 (Asia/Taipei) — Prevent duplicate app launches**

### Summary
- `App` 起動時に single-instance guard を追加し、2重起動時は既存ウィンドウを前面化して終了するようにした。

### Context / Goal
- アプリの重複起動を防ぎ、誤って複数プロセスを立ち上げても UI を増やさないようにしたかった。
- 2個目の起動でも既存インスタンスへ戻れる最低限の UX を確保したかった。

### Changes
- `App.xaml` から `StartupUri` を外し、起動フローを `App.OnStartup(...)` 管理へ切り替えた。
- `App.xaml.cs` に named mutex ベースの single-instance guard を追加した。
- 2重起動時は同じ exe path / session の既存プロセスを探し、最小化解除と前面化を試みてから終了するようにした。

### Files Touched
- `App.xaml` — `StartupUri` を削除し、`App` 側で起動制御できるようにした。
- `App.xaml.cs` — mutex 取得、既存インスタンス前面化、明示的な `MainWindow` 生成を実装した。

### Behavioral Impact
- 同一セッションでアプリを再起動しても、新しいメインウィンドウは開かれず、既存インスタンスのウィンドウ復元を試みてから終了する。
- 起動シーケンスは `App` 管理に変わるが、初回起動時の通常表示は従来どおり `MainWindow` が開く。

### Risk & Mitigation
- Risk: 既存インスタンスのメインウィンドウハンドルがまだ無い場合、前面化できずに静かに終了する可能性がある。
- Mitigation: 既存候補は exe path と session を突き合わせて誤検出を避け、前面化は best effort に留めた。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -c Release`
**2026-03-21 23:09 (Asia/Taipei) — Add current-pipeline user glossary term fixing**

### Summary
- 通常翻訳経路に user glossary の保護・復元を追加し、cache と直近訳再利用も glossary 変更で正しく切り替わるようにした。

### Context / Goal
- `Doc/UserGlossary_TermFix_CurrentPipeline_Implementation_Plan.md` に沿って、現行 `TranslateStage` ベースの glossary 固定訳を実装したかった。
- provider 共通で用語を安定化しつつ、`ForceGeminiStrict` 画像直送のような別経路には影響を広げたくなかった。

### Changes
- `UserGlossaryEntry` と `UserGlossaryService` を追加し、言語フィルタ付き glossary の最長一致保護、ASCII 単語境界ガード、placeholder 復元を実装した。
- `AppSettings` に `UserGlossaryEntries` を追加した。
- `CacheKeyBuilder` を更新し、`GlossaryVersion` に加えて有効 glossary entries の deterministic hash を含む scope を cache key に使うようにした。
- `TranslateStage` に glossary 適用を統合し、provider へは protected text を送信、復元後の最終訳を cache / `_lastTranslations` / overlay へ流すようにした。
- `_lastTranslations` のキーも glossary scope を含む形に変え、settings.json 手編集時でも stale reuse を避けるようにした。
- glossary hit / restore の既存 logger 向け情報ログを追加し、payload preview に protected text も出せるようにした。

### Files Touched
- `Models/UserGlossaryEntry.cs` — glossary entry model を追加した。
- `Models/AppSettings.cs` — `UserGlossaryEntries` を追加した。
- `Services/Translation/UserGlossaryService.cs` — glossary 保護・復元・scope hash 計算を実装した。
- `Services/CacheKeyBuilder.cs` — glossary scope を cache key に反映するようにした。
- `Services/Orchestration/Stages/TranslateStage.cs` — glossary の prepare/restore と `_lastTranslations` の scope-aware 化を実装した。

### Behavioral Impact
- 通常翻訳経路では、settings に登録した glossary 用語が provider 共通で placeholder 保護され、翻訳後に固定訳へ戻るようになった。
- glossary entries や `GlossaryVersion` が変わると、cache と `_lastTranslations` の再利用キーも変わる。
- `ForceGeminiStrict` の画像直送モードは `TranslateStage` を通らないため、本実装の対象外のままである。
- UI はまだ無いため、初期利用は `settings.json` の `UserGlossaryEntries` 手編集前提である。

### Risk & Mitigation
- Risk: placeholder を provider が壊すと復元できない可能性がある。
- Mitigation: 壊れにくい ASCII placeholder を使い、未復元件数をログへ出すようにした。
- Risk: 短い ASCII glossary が部分一致で誤爆する可能性がある。
- Mitigation: ASCII-only terms には単語境界ガードを入れ、CJK terms は従来どおり部分一致で扱うようにした。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -c Release`
**2026-03-21 23:14 (Asia/Taipei) — Switch user glossary to startup-loaded glossary files**

### Summary
- user glossary の読込元を `Settings.json` から辞書フォルダ配下の JSON ファイルへ切り替え、起動時に一度だけロードする方式へ変更した。

### Context / Goal
- glossary が設定ファイル肥大化の原因になりやすく、翻訳資産として別管理したかった。
- 読み込み失敗した辞書は無視し、まずは起動時ロードだけで十分という要件に合わせたかった。

### Changes
- `SettingsService` に `UserGlossaryDirectoryPath` を追加し、`%AppData%\Hotkey-Translator\UserGlossaries` を glossary ルートとして起動時に作成するようにした。
- `UserGlossaryService` を folder-backed 実装へ置き換え、`*.json` を起動時に読み込んで保持するようにした。
- glossary ファイルは `{ name, entries }` 形式と bare array 形式の両方を受け付け、壊れたファイルはログを出して無視するようにした。
- `CacheKeyBuilder` と `TranslateStage` は `Settings.json` ではなく、起動時にロードした glossary snapshot を共有するようにした。
- `AppSettings` から `UserGlossaryEntries` を削除し、glossary 本体を settings 永続化対象から外した。

### Files Touched
- `Services/SettingsService.cs` — glossary directory path を追加し、起動時に辞書フォルダを作成するようにした。
- `Services/Translation/UserGlossaryService.cs` — folder-backed glossary loader と保護・復元処理を実装した。
- `Services/CacheKeyBuilder.cs` — 起動時 glossary snapshot 由来の scope を cache key に使うようにした。
- `Services/Orchestration/Stages/TranslateStage.cs` — injected glossary service を参照するようにした。
- `Services/PipelineOrchestrator.cs` — glossary service を `TranslateStage` へ流すようにした。
- `MainWindow.xaml.cs` — app load 後に glossary service を生成して pipeline へ渡すようにした。
- `Models/AppSettings.cs` — `UserGlossaryEntries` を削除した。

### Behavioral Impact
- glossary は `%AppData%\Hotkey-Translator\UserGlossaries\*.json` からだけ読み込まれる。
- 読み込み失敗した glossary ファイルは無視され、アプリ起動自体は継続する。
- glossary 変更は次回起動時に反映される。runtime watcher や自動再読込はまだ無い。

### Risk & Mitigation
- Risk: フォルダ移行後に既存 `Settings.json` 内 glossary を参照しなくなる。
- Mitigation: `AppSettings` から本体を外し、今後の glossary source を folder に一本化した。
- Risk: 壊れた glossary ファイルに気づきにくい可能性がある。
- Mitigation: ファイルごとの skip を既存 logger に出し、他ファイルの読込は継続するようにした。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -c Release`

**2026-03-23 14:59 (Asia/Taipei) — Add reset-all-settings action with confirmation**

### Summary
- System ページに全設定リセットボタンを追加し、確認後に first-run default へ戻せるようにした。

### Context / Goal
- 全ての設定を既定値へ戻す操作を追加したかった。
- 誤操作コストが高いため、System ページに配置し、確認ダイアログを必須にしたかった。

### Changes
- `SystemSettingsControl` に `Reset All Settings` ボタンと説明文を追加した。
- `SystemSettingsControl` から親ウィンドウへ click を中継するイベントを追加した。
- `MainWindow` に確認ダイアログ付きの reset handler を追加した。
- reset は `SettingsService.CreateDefaultSettings()` を使って first-run default を生成し、既存の save pipeline に流すようにした。
- `SettingsService` に既定設定生成メソッドを公開し、初期化時にも同じ既定値経路を使うよう揃えた。

### Files Touched
- `UI/SystemSettingsControl.xaml` — System ページ下部に `Reset All Settings` ボタンと補助文を追加した。
- `UI/SystemSettingsControl.xaml.cs` — reset ボタンの RoutedEvent を親へ中継するイベントを追加した。
- `MainWindow.xaml` — `SystemSettingsControl` の reset イベントを `OnResetAllSettingsClicked` へ配線した。
- `MainWindow.xaml.cs` — 確認ダイアログ表示、default 設定ロード、既存 save pipeline 実行、結果ログ出力を追加した。
- `Services/SettingsService.cs` — first-run default を返す `CreateDefaultSettings()` を公開し、サービス初期状態にも適用した。

### Behavioral Impact
- System ページから全設定を既定値へ戻せるようになった。
- リセット時は必ず確認ダイアログが表示され、承認後にのみ保存処理へ進む。
- 実際の保存は既存の settings save pipeline を通るため、通常の設定変更と同じ validation / host setup / rollback が適用される。

### Risk & Mitigation
- Risk: 全設定リセットは影響範囲が広く、誤操作すると ROI や hotkey を含む保存設定が失われる。
- Mitigation: ボタンを System ページへ隔離し、確認ダイアログを必須にしたうえで、キャンセル時はログを出して何も変更しないようにした。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.sln -p:UseAppHost=false`
- `rg -n "Reset All Settings|ResetAllSettingsClicked|OnResetAllSettingsClicked|CreateDefaultSettings\(" UI/SystemSettingsControl.xaml UI/SystemSettingsControl.xaml.cs MainWindow.xaml MainWindow.xaml.cs Services/SettingsService.cs`

**2026-03-23 15:08 (Asia/Taipei) — Update Japanese and English README for bundled uv and llama.cpp**

### Summary
- 日本語 / 英語 README を、配布版に `uv` と llama.cpp ランタイムが同梱される前提へ更新した。

### Context / Goal
- 配布物では `Tools\uv\uv.exe` と `TranslationServiceLlama\LlamaCpp\` が含まれるようになっており、README の依存関係説明が古くなっていた。
- ソース実行時の前提と、配布版に含まれる実行要素 / 含まれない要素を両言語で明確にしたかった。

### Changes
- `README.md` を全面更新し、配布版に含まれるもの、未同梱物、初回実行時の挙動、ソース実行時の前提を整理した。
- `README.en.md` も同じ構成で更新し、英語版の内容を現在の distribution に合わせた。
- OCR エンジン一覧に OneOCR を反映した。
- llama.cpp ランタイムは同梱、ただし GGUF / mmproj は未同梱である点を明記した。

### Files Touched
- `README.md` — 日本語 README を現行 distribution 前提で全面更新した。
- `README.en.md` — 英語 README を現行 distribution 前提で全面更新した。

### Behavioral Impact
- コード動作への影響はない。
- 利用者は、配布版では `uv` と llama.cpp ランタイムを別途用意しなくてよい一方、モデルや OneOCR vendor は別扱いであることを README から理解できるようになる。

### Risk & Mitigation
- Risk: README の説明が build-dist の実装とズレると、配布時の期待値が再び不一致になる。
- Mitigation: `build-dist.ps1` の同梱内容 (`uv`, `TranslationServiceLlama`, `Magpie`, `OneOcrHelper`) と `DIST-NOTES` 相当の内容に合わせて記述した。

### Tests / Verification
- `rg -n "配布版に含まれるもの|Tools\\uv\\uv.exe|llama.cpp runtime|OneOCR vendor|ソースから動かす場合の前提|What The Distribution Includes|Requirements For Running From Source|model payloads|OneOCR / PaddleOCR" README.md README.en.md`

**2026-03-23 15:13 (Asia/Taipei) — Add uv and llama.cpp to third-party notes**

### Summary
- 日本語 / 英語 README のサードパーティ節に `uv` と llama.cpp runtime の記述を追加した。

### Context / Goal
- 配布版に `Tools\uv\uv.exe` と `TranslationServiceLlama\LlamaCpp\` を含めている一方、サードパーティ節にはその記載がなかった。
- 再配布時の注意点として、同梱 third-party runtime を README 上でも明示したかった。

### Changes
- 日本語 README のサードパーティ節に Astral `uv` と llama.cpp runtime の項目を追加した。
- 英語 README のサードパーティ節にも同内容を追加した。
- どちらも upstream license / notice と、model file 再配布条件の確認が必要である点を明記した。

### Files Touched
- `README.md` — サードパーティ節へ `uv` と llama.cpp runtime の説明を追加した。
- `README.en.md` — サードパーティ節へ `uv` と llama.cpp runtime の説明を追加した。

### Behavioral Impact
- コード動作への影響はない。
- README 上で、配布物に含まれる third-party runtime の一覧がより完全になった。

### Risk & Mitigation
- Risk: upstream 側の実際のライセンス文書同梱状況と README の説明がズレる可能性がある。
- Mitigation: README では断定的なライセンス本文を書かず、upstream license / notice の確認を促す表現に留めた。

### Tests / Verification
- `rg -n "Astral|llama.cpp runtime|Tools/uv/uv.exe|TranslationServiceLlama/LlamaCpp" README.md README.en.md`

**2026-03-23 15:36 (Asia/Taipei) — Add MIT license file and update README license sections**

### Summary
- ルート `LICENSE` に MIT License を追加し、日本語 / 英語 README のライセンス節を更新した。

### Context / Goal
- MIT ライセンスを使う前提で、プロジェクト本体のライセンスを明示したかった。
- README 末尾には「LICENSE がない」と残っていたため、実際の状態へ合わせて修正する必要があった。

### Changes
- ルートに MIT License の本文を持つ `LICENSE` を追加した。
- `README.md` のライセンス節を、プロジェクト本体は MIT である旨に更新した。
- `README.en.md` のライセンス節も同様に更新した。

### Files Touched
- `LICENSE` — プロジェクト本体の MIT License を追加した。
- `README.md` — ライセンス節を MIT 前提の説明へ更新した。
- `README.en.md` — ライセンス節を MIT 前提の説明へ更新した。

### Behavioral Impact
- コード動作への影響はない。
- リポジトリのルートでプロジェクト本体のライセンスを明示できるようになった。

### Risk & Mitigation
- Risk: 著作権者表記の名義が公開時の運用方針と異なる可能性がある。
- Mitigation: `Hotkey Translator contributors` として一般的な表記に留め、必要なら後で名義だけ差し替えやすい独立ファイルにした。

### Tests / Verification
- `Get-Content -Path LICENSE -Encoding UTF8 | Select-Object -First 6`
- `Get-Content -Path README.md -Encoding UTF8 | Select-Object -Last 6`
- `Get-Content -Path README.en.md -Encoding UTF8 | Select-Object -Last 6`

**2026-03-24 00:10 (Asia/Taipei) — Resolve SettingsService merge conflict**

### Summary
- `SettingsService` の未解決 merge conflict を解消し、glossary ディレクトリ設定と既定設定初期化の両方を保持した。

### Context / Goal
- `Services/SettingsService.cs` に conflict marker が残っており、ビルド不能状態になっていた。
- glossary フォルダ対応を維持しつつ、初回起動時の既定設定も `CreateDefaultSettings()` に統一する必要があった。

### Changes
- `<<<<<<<` / `=======` / `>>>>>>>` を削除した。
- `UserGlossaryDirectoryPath` プロパティと `Settings = CreateDefaultSettings()` 初期化を両立する形に整理した。

### Files Touched
- `Services/SettingsService.cs` — merge conflict を解消し、glossary directory と default settings 初期化を同時に保持した。

### Behavioral Impact
- glossary ディレクトリ作成処理を維持したまま、初回起動時の既定設定が従来どおり適用される。
- ビルド不能状態が解消された。

### Risk & Mitigation
- Risk: conflict 解消時にどちらか一方の初期化を落とすと、glossary 読み込みまたは初期設定が壊れる。
- Mitigation: `UserGlossaryDirectoryPath` と `CreateDefaultSettings()` の両方を残し、Release ビルドで確認した。

### Tests / Verification
- `dotnet build .\\Hotkey-Translator.sln -c Release`

**2026-03-24 13:21 (Asia/Taipei) — Document bundled NDLOCR-Lite models in README**

### Summary
- 日本語 / 英語 README に、配布物へ NDLOCR-Lite モデルを含む前提の注意書きを追加した。

### Context / Goal
- 配布物には `ndl-lab/ndlocr-lite` 系のモデルも含まれるため、README の配布内容とサードパーティ節にその記載が必要だった。
- upstream リポジトリでは CC BY 4.0 公開と明記されているため、再配布条件と attribution 注意を README で補足したかった。

### Changes
- `README.md` の配布版同梱物一覧へ NDLOCR-Lite モデルを追加した。
- `README.en.md` の distribution section にも NDLOCR-Lite model files を追加した。
- 両 README のサードパーティ節へ、`ndl-lab/ndlocr-lite` と CC BY 4.0 に基づく再配布 / 表示義務の確認事項を追記した。
- 再配布時の総合注意文にも NDLOCR-Lite assets を含めた。

### Files Touched
- `README.md` — NDLOCR-Lite モデル同梱と upstream ライセンス注意を追記した。
- `README.en.md` — NDLOCR-Lite model bundling and upstream license note を追記した。

### Behavioral Impact
- コード動作への影響はない。
- README 上で、配布物に含まれる NDLOCR-Lite モデルと、その再配布条件の確認必要性が明確になった。

### Risk & Mitigation
- Risk: upstream 側のライセンス表記や再配布条件が将来変わる可能性がある。
- Mitigation: README では upstream リポジトリ基準の確認を促す表現に留め、固定的な法的断定は避けた。

### Tests / Verification
- `rg -n "NDLOCR-Lite|ndl-lab/ndlocr-lite|CC BY 4.0|OcrServiceNDL" README.md README.en.md`

**2026-03-24 13:41 (Asia/Taipei) — Clarify non-bundled Paddle, LlamaCpp, and VisionLLM models in README**

### Summary
- 日本語 / 英語 README に、未同梱だが実行時に利用されうる Paddle / LlamaCpp / VisionLLM モデルの注意書きを追加した。

### Context / Goal
- PaddleOCR モデルは配布していない一方で、実行時に取得・利用される可能性がある。
- 同様に、LlamaCpp と VisionLLM の既定モデルも未同梱であるため、README 上で同じ区分として明示する必要があった。

### Changes
- `README.md` の未同梱一覧へ LlamaCpp と VisionLLM の既定モデルを追加した。
- `README.en.md` の not bundled section にも default LlamaCpp / VisionLLM models を追加した。
- 両 README のサードパーティ節に、PaddleOCR / PaddleOCR-VL managed models、LlamaCpp 既定 GGUF、VisionLLM 既定 GGUF / mmproj は未同梱であり、利用時は upstream 条件確認が必要と追記した。
- 再配布時の総合注意文にも Paddle / LlamaCpp / VisionLLM models を含めた。

### Files Touched
- `README.md` — 未同梱モデルと再配布注意を追記した。
- `README.en.md` — not bundled models and redistribution note を追記した。

### Behavioral Impact
- コード動作への影響はない。
- README 上で、配布物に含まれないが実行時に使われるモデル群の扱いが明確になった。

### Risk & Mitigation
- Risk: 各 upstream モデルの配布条件は個別で異なり、README だけで完全には網羅できない。
- Mitigation: README では upstream 条件確認を促す表現に留め、固定的な法的断定は避けた。

### Tests / Verification
- `rg -n "LlamaCpp|VisionLLM|PaddleOCR / PaddleOCR-VL|Paddle / LlamaCpp / VisionLLM|Default LlamaCpp|Default VisionLLM" README.md README.en.md`

**2026-03-27 15:42 (Asia/Taipei) — Add text export implementation plan via sub-agent**

### Summary
- OCR 原文と翻訳文の出力機能について、推奨実装案の計画書を `Doc/` に追加した。

### Context / Goal
- 直近の pipeline 結果から OCR 原文と翻訳文を出力する機能の推奨方針を、計画書として残したかった。
- overlay 表示ではなく committed pipeline state を正本にする方針を、後続実装へそのまま渡せる形に整理したかった。

### Changes
- `Doc/TextExport_OCR_Translation_Implementation_Plan.md` を新規追加した。
- 内容は `TextExportSnapshot`、`TextExportService`、`PipelineOrchestrator` の read API、clipboard export v1、paired text を既定とする方針で構成した。
- サブエージェントに文書作成を委譲し、戻り後に UTF-8 で内容確認した。

### Files Touched
- `Doc/TextExport_OCR_Translation_Implementation_Plan.md` — OCR 原文 / 翻訳文 export 機能の推奨実装案を記述した。

### Behavioral Impact
- コード動作への影響はない。
- 後続実装の前提となる設計方針が `Doc/` に明文化された。

### Risk & Mitigation
- Risk: 実装前提の一部が今後の UI 要件や出力形式要求で変わる可能性がある。
- Mitigation: v1 を clipboard + paired text に限定し、JSON や hotkey は段階追加にできる構成として整理した。

### Tests / Verification
- `Get-Content -Path Doc\TextExport_OCR_Translation_Implementation_Plan.md -Encoding UTF8 | Select-Object -First 120`
**2026-03-30 10:06 (Asia/Taipei) — Implement WPF resx localization for settings UI**

### Summary
- 設定コンソール UI と主要な利用者向けメッセージを `resx` ベースで日英切替できるようにした。

### Context / Goal
- `Doc/Wpf_Resx_Localization_Implementation_Plan.md` に沿って、WPF UI 文言をコード/XAML 直書きから切り離したかった。
- `System / English / 日本語` を保存可能な UI 言語設定として追加し、起動時反映と即時反映の両方を成立させたかった。

### Changes
- `Resources/Strings.resx` / `Resources/Strings.ja.resx`、`LocalizationService`、`LocExtension` を追加し、XAML と C# の両方から同じ翻訳キーを参照できるようにした。
- `AppSettings.UiLanguage`、`UiLanguageSettingsRule`、`SettingsViewModel.UiLanguageTag` を追加し、UI 言語設定の保存・正規化・即時適用を実装した。
- `App` で設定を先読みして保存済み UI 言語を初回描画前に適用し、`MainWindow` / `Overview` / `SystemSettings` / `Translation` / `RuntimeLogs` の主要文言を `LocExtension` に置換した。
- `MessageBox`、WinRT language pack UI、OneOCR vendor setup、resource host busy/failure message、Overview 要約文をローカライズ基盤経由へ変更した。

### Files Touched
- `App.xaml.cs` — 設定先読みと保存済み UI 言語の起動時適用を追加した。
- `Models/AppSettings.cs` — `UiLanguage` 設定を追加した。
- `Services/LocalizationService.cs` — `resx` 解決、`CultureInfo` 適用、変更通知を実装した。
- `UI/Localization/LocExtension.cs` — XAML からローカライズ文字列を参照する `MarkupExtension` を追加した。
- `Resources/Strings.resx` — 既定英語の UI / ダイアログ文言を追加した。
- `Resources/Strings.ja.resx` — 日本語 UI / ダイアログ文言を追加した。
- `Services/Settings/Rules/UiLanguageSettingsRule.cs` — 不正な UI 言語値を `system` に正規化するルールを追加した。
- `Services/Settings/AppSettingsValidator.cs` — UI 言語正規化ルールを登録した。
- `Services/SettingsService.cs` — 設定ロード済み状態を保持するようにした。
- `ViewModels/SettingsViewModel.cs` — UI 言語の保存・即時反映、固定オーバーレイ文言のローカライズを追加した。
- `ViewModels/MainWindowViewModel.cs` — Overview 要約文のローカライズと言語変更時の再通知を追加した。
- `ViewModels/RuntimeStatusViewModel.cs` — 初期ランタイム文言をローカライズ対応にした。
- `MainWindow.xaml` — サイドバー、共通ボタン、タイトルをローカライズ参照へ置換した。
- `MainWindow.xaml.cs` — 主要 `MessageBox`、ROI/翻訳状態文言、前提条件メッセージをローカライズ参照へ置換した。
- `UI/OverviewControl.xaml` — 主要ラベル、ボタン、言語選択、固定オーバーレイ文言をローカライズ参照へ置換した。
- `UI/SystemSettingsControl.xaml` — UI 言語選択 UI を追加し、Appearance/Performance/Reset 文言をローカライズ参照へ置換した。
- `UI/TranslationControl.xaml` — 翻訳優先順位と Llama 設定の主要文言をローカライズ参照へ置換した。
- `UI/RuntimeLogsControl.xaml` — Preview/Log/Hint 文言をローカライズ参照へ置換した。
- `Services/Application/WinRtLanguagePackUiController.cs` — WinRT language pack の確認/進捗 UI をローカライズ対応にした。
- `Services/Application/OneOcrVendorUiController.cs` — OneOCR vendor setup のダイアログと busy 文言をローカライズ対応にした。
- `Services/Application/ResourceHostFacade.cs` — resource host の busy/failure user message をローカライズ対応にした。
- `Services/GrpcHost/GrpcHostDescriptor.cs` — failure user message を動的解決できるようにした。
- `Services/GrpcHost/GrpcHostOrchestrator.cs` — 現在言語で failure user message を表示するようにした。

### Behavioral Impact
- 利用者は `System / English / 日本語` から UI 言語を選べ、設定は保存され次回起動時も維持される。
- `MainWindow` 配下の主要設定 UI と主要ダイアログが現在の UI 言語で表示される。
- `system` は OS UI 言語を `ja / en` に正規化して追従する。

### Risk & Mitigation
- Risk: まだ未移行の画面やログ文言には英語直書きが残る可能性がある。
- Mitigation: 今回は計画書対象の主要設定 UI と主要ダイアログに範囲を絞り、`LocalizationService` / `LocExtension` を追加済みなので残りも同じ方式で拡張できる。
- Risk: 実行中プロセスが既存 `bin\Debug` 出力をロックして通常ビルドが失敗する。
- Mitigation: 回帰確認は別出力先 `artifacts\localization-build` へのビルドで実施した。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -o .\artifacts\localization-build`

**2026-05-13 01:27 (Asia/Taipei) — Geminiモデル選択実装案の追加**

### Summary
- Gemini API のモデル一覧取得と ComboBox 選択化に向けた実装方針を `Doc/` に追加した。

### Context / Goal
- Gemini 翻訳モデルが設定値としては存在するが、UI から選べず実質固定になっている。
- Gemini API から安定版候補を取得し、ユーザが設定画面で選択できる設計を先に整理したい。

### Changes
- 現状仕様、ゴール/非ゴール、API 取得方針、stable フィルタ、UI/ViewModel 接続、検証項目を整理した。
- 既存の `AppSettings.GeminiModel` と `GeminiClient.BuildEndpoint()` を活かす前提で実装ステップを分割した。

### Files Touched
- `Doc/Gemini_ModelSelection_Implementation_Plan.md` — Gemini モデル一覧取得と ComboBox 選択化の新規実装案を追加した。

### Behavioral Impact
- ドキュメント追加のみのため、アプリ実行時の挙動変更はない。

### Risk & Mitigation
- Risk: 実装前の設計書のため、実際の API レスポンス差分により調整が必要になる可能性がある。
- Mitigation: 実装時は Google の models list レスポンスを実データで確認し、保存済みモデルを維持する方針で破壊的変更を避ける。

### Tests / Verification
- 未実施。ドキュメント追加のみのためビルドは実行していない。
- `dotnet build .\Hotkey-Translator.csproj` は実行中 `Hotkey-Translator.exe` による `bin\Debug` ロックのため出力コピー段階で失敗

**2026-03-30 10:51 (Asia/Taipei) — Localize remaining side-panel settings**

### Summary
- サイドパネル内で未移行だった Capture / OCR Settings / OCR Engines / VisionLLM / Overlay / Auto Translate / Hook / Hotkey の文言を `resx` 化した。

### Context / Goal
- 初回の `resx` 移行後も、設定パネル配下の複数画面に `Text=` / `Content=` 直書きが残っていた。
- UI 言語切替時にサイドパネル内の設定文言も英語固定にならない状態へ揃えたかった。

### Changes
- 対象 XAML に `uiLoc:Loc` を追加し、見出し、ラベル、チェックボックス、ボタン、補足文、サイドバー内カテゴリ名をローカライズ参照へ置換した。
- `Resources/Strings.resx` / `Resources/Strings.ja.resx` に Capture / OCR tuning / PaddleOCR / VisionLLM / Overlay / Auto Translate / Hook / Hotkey 用のキーを追加した。
- `MainWindow.xaml` の `Overview` / `Settings` タブ見出し、`VisionLLM` サイドバー項目、設定カテゴリ一覧も同じキー系へ寄せた。

### Files Touched
- `MainWindow.xaml` — タブ見出し、`VisionLLM` を含む設定カテゴリ表示をローカライズ参照へ置換した。
- `UI/CaptureControl.xaml` — Capture 設定の見出し・モード・プロバイダー補助文言をローカライズ参照へ置換した。
- `UI/OcrSettingsControl.xaml` — OCR tuning / preprocess / input の各ラベルと補足文をローカライズ参照へ置換した。
- `UI/OcrEnginesControl.xaml` — PaddleOCR / PaddleOCR-VL の各設定名、モデル選択、実行ボタンをローカライズ参照へ置換した。
- `UI/VisionLlmSettingsControl.xaml` — VisionLLM 設定、共有翻訳、hybrid OCR 関連文言をローカライズ参照へ置換した。
- `UI/OverlayControl.xaml` — Overlay readability 設定の文言をローカライズ参照へ置換した。
- `UI/OverlayBehaviorControl.xaml` — Auto Translate の各設定文言をローカライズ参照へ置換した。
- `UI/HookFullscreenControl.xaml` — Hook / Launcher / Mirror Fullscreen の各設定文言をローカライズ参照へ置換した。
- `UI/HotkeysControl.xaml` — Hotkey 行ラベル、RawInput 説明、Win key 注意文をローカライズ参照へ置換した。
- `Resources/Strings.resx` — 追加したサイドパネル向け英語リソースを定義した。
- `Resources/Strings.ja.resx` — 追加したサイドパネル向け日本語リソースを定義した。

### Behavioral Impact
- UI 言語を `English` / `日本語` / `System` へ切り替えた際、サイドパネル配下の設定画面も同じ言語で表示される。
- `Overview` / `Settings` タブ見出しと設定カテゴリ一覧も切替対象に含まれる。

### Risk & Mitigation
- Risk: `DX11`、`Vulkan`、`Ctrl` など技術識別子として扱う項目は英字のまま残しているため、完全和訳を期待すると一部英字 UI が残る。
- Mitigation: 利用者が操作時に参照する説明文と設定名は `resx` 化し、識別子は実際のキー名・API 名との対応を優先して保持した。
- Risk: 新規キー追加漏れがあると `LocExtension` のキー名がそのまま表示される。
- Mitigation: 追加後に `artifacts\localization-build` へのビルドでリソース解決を含めて検証した。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -o .\artifacts\localization-build`

**2026-05-13 01:38 (Asia/Taipei) — Geminiモデル選択UIの実装**

### Summary
- Gemini API のモデル一覧から stable な `generateContent` 対応モデルを取得し、翻訳設定 UI で選択保存できるようにした。

### Context / Goal
- `AppSettings.GeminiModel` は存在していたが、UI から変更できず実質固定だった。
- Gemini API から利用可能な安定版候補を取得し、ユーザがモデルを選べる設定にしたい。

### Changes
- `GeminiClient` に `models.list` 呼び出し、ページング、`generateContent` 対応判定、preview/experimental/latest/deprecated 除外、`models/` prefix 正規化を追加した。
- `SettingsViewModel` / `MainWindowViewModel` に Gemini モデル選択値、候補リスト、再読み込みコマンドを追加した。
- 翻訳設定 UI に Gemini モデル ComboBox と更新ボタンを追加した。
- 保存済みモデルが API 候補から消えても現在値として候補に残すようにした。

### Files Touched
- `Services/GeminiClient.cs` — Gemini モデル一覧取得とモデル名正規化を追加した。
- `Models/GeminiModelOption.cs` — UI 表示用の Gemini モデル候補 DTO を追加した。
- `Models/AppSettings.cs` — Gemini 既定モデル定数を追加し、既定値参照へ変更した。
- `ViewModels/SettingsViewModel.cs` — `GeminiModel` の読み込み・保存・自動保存通知を追加した。
- `ViewModels/MainWindowViewModel.cs` — Gemini モデル候補リストと再読み込みコマンドを追加した。
- `MainWindow.xaml.cs` — Gemini モデル候補の初期化と API 再読み込み処理を追加した。
- `UI/TranslationControl.xaml` — Gemini モデル ComboBox と更新ボタンを追加した。
- `Resources/Strings.resx` — Gemini モデルラベルの英語リソースを追加した。
- `Resources/Strings.ja.resx` — Gemini モデルラベルの日本語リソースを追加した。

### Behavioral Impact
- Gemini 翻訳と ForceGemini 画像翻訳は、設定画面で選択した `GeminiModel` を使う。
- Gemini API key が空、一覧取得失敗、候補0件の場合も保存済みモデルは維持される。
- `models/gemini-...` 形式の API 名は保存・実行時に `gemini-...` へ正規化される。

### Risk & Mitigation
- Risk: Google 側のモデル命名変更により stable フィルタが候補を過剰除外する可能性がある。
- Mitigation: 候補が空でも保存済みモデルを ComboBox に残し、自動で別モデルへ切り替えない。
- Risk: models.list がページングされると候補漏れが起きる可能性がある。
- Mitigation: `pageSize=1000` と `nextPageToken` 追跡で全ページを取得する。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -o .\artifacts\gemini-model-selection-build`

**2026-05-23 13:50 (Asia/Taipei) — LlamaCpp長文複数ブロック分割の追加**

### Summary
- LlamaCpp 翻訳で長文を含む複数ブロックを事前分割し、JSON 応答構造の崩れを抑えるようにした。

### Context / Goal
- 複数ブロックを一括で LlamaCpp に渡すと、ブロック内文字数が多い場合に JSON 構造や件数対応が崩れやすい。
- 長文複数ブロックでは速度より構造安定性を優先したい。

### Changes
- Llama 翻訳エンジンに構造安定性用の分割閾値を追加した。
- 複数 item で最大 600 文字以上、または合計 1200 文字以上の場合に既存 adaptive split を使って小バッチ化するようにした。
- 分割理由に最大文字数・合計文字数・閾値を含め、ログから調整しやすくした。

### Files Touched
- `TranslationServiceLlama/llama_engine.py` — 長文複数ブロックの事前分割条件を追加した。

### Behavioral Impact
- LlamaCpp 翻訳で長文を含む複数ブロックは、1回の JSON バッチではなく小さなバッチに分割される場合がある。
- 短い複数ブロックと単一ブロックの翻訳挙動は従来通り。
- 長文複数ブロックでは HTTP 呼び出し回数が増え、翻訳完了までの時間が延びる可能性がある。

### Risk & Mitigation
- Risk: 閾値が低すぎると過剰に分割され、翻訳速度が低下する。
- Mitigation: 閾値を定数化し、split reason に実測値を出すことでログから調整可能にした。
- Risk: ブロック間文脈が分割により弱くなる可能性がある。
- Mitigation: 短文複数ブロックは従来通りまとめ、長文時のみ構造安定性を優先する条件にした。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama\llama_engine.py`

**2026-05-23 17:37 (Asia/Taipei) — build-distのllama.cpp同梱ファイル絞り込み**

### Summary
- online 配布に含める llama.cpp runtime を、LlamaGrpcHost が必要とするファイルだけに限定した。

### Context / Goal
- `TranslationServiceLlama\LlamaCpp` をほぼ丸ごと配布しており、未使用の llama.cpp CLI/bench/tool 類や CPU variant DLL が含まれていた。
- 配布サイズと混入ファイルを抑えるため、起動時検証に必要な runtime ファイルだけを同梱したい。

### Changes
- `build-dist.ps1` に llama.cpp runtime の明示的な同梱ファイルリストを追加した。
- `TranslationServiceLlama` コピー時に `LlamaCpp` ディレクトリ全体を除外し、必要な7ファイルだけを個別コピーするようにした。
- `LlamaCpp\Models` は従来通り空ディレクトリとして作成する。
- 配布 notes の llama.cpp 説明を、選択された runtime binaries の同梱に合わせて更新した。

### Files Touched
- `build-dist.ps1` — llama.cpp runtime の同梱対象を明示リスト化し、不要ファイルを配布から除外した。

### Behavioral Impact
- online 配布物の `TranslationServiceLlama\LlamaCpp` には `llama-server.exe`、`llama.dll`、`ggml.dll`、`ggml-base.dll`、`ggml-cpu.dll`、`ggml-cuda.dll`、`mtmd.dll` のみが入る。
- llama.cpp の追加 CLI や bench ツール、CPU variant DLL は配布されなくなる。
- アプリの LlamaCpp 翻訳起動前チェックで要求しているファイルは維持される。

### Risk & Mitigation
- Risk: 実行時に llama.cpp が暗黙依存する追加 DLL がある場合、配布環境で起動に失敗する可能性がある。
- Mitigation: 現行 `LlamaGrpcHost` の必須ファイル一覧に合わせ、配布後の LlamaCpp 起動 smoke test で不足があればリストへ追加できる形にした。

### Tests / Verification
- `[scriptblock]::Create((Get-Content -Path .\build-dist.ps1 -Raw -Encoding UTF8)) | Out-Null`
- 未実施: `build-dist.ps1` 全体実行。publish/native build 成果物を作る重い処理を伴うため。

**2026-05-25 16:53 (Asia/Taipei) — LlamaCPP runtimeバイナリ整理**

### Summary
- 翻訳と VisionLLM OCR が共有する llama-server runtime の必須ファイルを明示し、LlamaCpp 直下を runtime 起動に必要なファイル中心に整理した。

### Context / Goal
- `TranslationServiceLlama\LlamaCpp` 直下に llama.cpp の CLI/bench/tool 類を含む全バイナリが混在していた。
- 翻訳と OCR の llama-server 起動に必要な DLL/EXE を明確化し、配布物と起動前チェックの不足を防ぎたい。

### Changes
- `llama-server.exe` の launcher 形式に必要な `llama-server-impl.dll` / `llama-common.dll` を runtime 必須ファイルへ追加した。
- 実モデルロードに必要な `ggml-cpu-*.dll` と、それらの依存である `libomp140.x86_64.dll` を runtime 必須ファイルへ追加した。
- 古い `ggml-cpu.dll` と llama.cpp の CLI/bench/quantize/rpc など non-runtime バイナリを `TranslationServiceLlama\LlamaCpp\NonRuntime` に退避した。
- 翻訳ホストと VisionLLM OCR ホストで共有する runtime 検証クラスを追加し、OCR 側も不足ファイルを起動前に検出するようにした。
- online 配布スクリプトとポータブル配布ドキュメントの LlamaCpp 同梱ファイル一覧を更新した。

### Files Touched
- `Services/LlamaCppRuntimeLayout.cs` — 共有 llama.cpp runtime 必須ファイル一覧と検証処理を追加した。
- `Services/LlamaGrpcHost.cs` — 翻訳ホストの起動前検証を共有 runtime 検証へ変更した。
- `Services/VisionLlmGrpcHost.cs` — VisionLLM OCR ホストでも共有 llama.cpp runtime を起動前検証するようにした。
- `build-dist.ps1` — 配布に含める llama.cpp runtime ファイルを実起動に必要な一覧へ更新した。
- `Doc/Portable_Distribution_Path_Requirements.md` — ポータブル配布の LlamaCpp 構成例を更新した。
- `TranslationServiceLlama/LlamaCpp/NonRuntime/` — 起動に不要な llama.cpp tool/bench/rpc 類と旧 `ggml-cpu.dll` を退避した。

### Behavioral Impact
- 翻訳と VisionLLM OCR は、同じ `TranslationServiceLlama\LlamaCpp` root に runtime 必須ファイルが不足している場合、llama-server 起動前に明示エラーで停止する。
- online 配布物には llama-server 起動に必要な runtime DLL/EXE と CPU backend variants が含まれ、CLI/bench/tool 類は含まれない。
- ローカルの `LlamaCpp` root は runtime 専用に近い構成になり、non-runtime tool 類は `NonRuntime` 配下へ移動した。

### Risk & Mitigation
- Risk: 将来の llama.cpp build で runtime DLL 名が変わると検証や配布コピーで不足扱いになる。
- Mitigation: 必須ファイル一覧を `LlamaCppRuntimeLayout` と `build-dist.ps1` に明示し、不足時は fail fast で検出できるようにした。
- Risk: `NonRuntime` 配下へ移動した llama.cpp CLI/bench/tool 類を直接実行する運用がある場合、従来の root 直下パスでは起動できない。
- Mitigation: アプリ runtime では使用していないファイルのみ退避し、必要なら `NonRuntime` から戻せる形にした。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj`
- `[scriptblock]::Create((Get-Content -Path .\build-dist.ps1 -Raw -Encoding UTF8)) | Out-Null`
- runtime 必須ファイル一覧と `LlamaCpp` root の余剰ファイルなしを PowerShell で確認
- 整理後の root から `llama-server.exe` を翻訳モデルで起動し、`/health` が ready になることを確認
- 整理後の root から `llama-server.exe --mmproj` を VisionLLM OCR モデルで起動し、`/health` が ready になることを確認

**2026-05-25 17:42 (Asia/Taipei) — LlamaCPP翻訳デフォルトモデルをHy-MT2へ変更**

### Summary
- LlamaCPP翻訳の組み込みデフォルトモデルを `Hy-MT2-1.8B-Q4_K_M.gguf` に変更した。

### Context / Goal
- 翻訳デフォルトを旧 `HY-MT1.5-1.8B-Q8_0.gguf` から Hugging Face の `tencent/Hy-MT2-1.8B-GGUF` Q4_K_M へ切り替えたい。
- 既存ユーザーの旧デフォルト設定は新デフォルトへ移行し、任意の別 `.gguf` 選択は維持したい。

### Changes
- AppSettings と LlamaGrpcHost のデフォルトモデル名を `Hy-MT2-1.8B-Q4_K_M.gguf` に変更した。
- Llama 設定正規化で、旧組み込みデフォルト `HY-MT1.5-1.8B-Q8_0.gguf` のみ新デフォルトへ互換移行するようにした。
- ResourceHost のモデルダウンロード判定でも同じ正規化を使い、旧デフォルト設定から新 manifest の自動ダウンロードへ進めるようにした。
- `TranslationServiceLlama/model_manifest.json` を Hy-MT2 の download URL / SHA256 / size に更新した。
- `test_translation_engine.py` の既定モデルパスを Hy-MT2 に更新した。

### Files Touched
- `Models/AppSettings.cs` — 新規設定の Llama 選択モデル既定値を Hy-MT2 に変更した。
- `Services/LlamaGrpcHost.cs` — 起動時のデフォルトモデル名と選択モデル正規化を Hy-MT2 に合わせた。
- `Services/Settings/SettingsHostNormalizer.cs` — 旧デフォルトのみを新デフォルトへ移行する互換正規化を追加した。
- `Services/Application/ResourceHostFacade.cs` — Llama モデルの bootstrap 判定で共通正規化を使うようにした。
- `TranslationServiceLlama/model_manifest.json` — Hy-MT2 Q4_K_M の manifest に更新した。
- `TranslationServiceLlama/test_translation_engine.py` — smoke test の既定モデルを Hy-MT2 に変更した。

### Behavioral Impact
- 新規設定または旧デフォルト設定の環境では、LlamaCPP翻訳の既定モデルが `Hy-MT2-1.8B-Q4_K_M.gguf` になる。
- 旧デフォルト以外の `.gguf` を指定している環境は、その選択を維持する。
- Hy-MT2 が未配置の場合、manifest に基づいて約 1.13GB のモデルを自動ダウンロード対象として扱う。

### Risk & Mitigation
- Risk: 旧デフォルト名を意図的に使い続けたい環境でも、設定正規化により新デフォルトへ置換される。
- Mitigation: 移行対象を旧組み込みデフォルトの完全一致に限定し、その他のカスタム `.gguf` は維持する。
- Risk: upstream の Hugging Face ファイルが差し替えられるとダウンロード後の SHA256 検証に失敗する。
- Mitigation: manifest に確認済み SHA256 と size を固定し、不一致時は fail fast で検出する。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj`
- `python -m py_compile TranslationServiceLlama\test_translation_engine.py`
- `TranslationServiceLlama\model_manifest.json` の size/SHA256 がローカル `Hy-MT2-1.8B-Q4_K_M.gguf` と一致することを確認
- 整理済み LlamaCpp runtime root から `Hy-MT2-1.8B-Q4_K_M.gguf` で `llama-server.exe` を起動し、`/health` が ready になることを確認

**2026-05-27 10:30 (Asia/Taipei) — VisionLLM Thinking無効化の強化**

### Summary
- VisionLLM の llama-server 起動を `--reasoning off` に変更し、応答に混入した `<think>` ブロックを出力前に除去するようにした。

### Context / Goal
- VisionLLM OCR/翻訳は Thinking なしで動作させ、Think 内容を OCR/翻訳結果へ出力させないようにしたい。
- 既存の `--reasoning-format none` は think を content に残す可能性があり、OCR のプレーンテキストや翻訳 JSON に混入すると不正な出力になり得る。

### Changes
- VisionLLM llama-server の disable-thinking 起動引数に `--reasoning off` を追加した。
- `--reasoning-format none` を削除し、think 内容を content に残す方向の指定をやめた。
- `extract_message_content()` の戻り値に `<think>...</think>` / 未閉じ `<think>` / 先頭 orphan `</think>` の除去処理を追加した。

### Files Touched
- `OcrServiceVisionLlm/vision_llama_engine.py` — Thinking 無効化の起動引数を見直し、応答テキスト sanitizer を追加した。

### Behavioral Impact
- VisionLLM OCR/翻訳では、llama-server 側で reasoning/thinking を明示的に OFF にする。
- モデルや chat template の差分で `<think>` が content に混入しても、アプリへ返す OCR/翻訳テキストからは除去される。
- `--enable-thinking` を使う診断実行でも、アプリが消費する message content から `<think>` ブロックは除去される。

### Risk & Mitigation
- Risk: 実際の画面文字列にリテラルの `<think>...</think>` が含まれる場合、それも除去される。
- Mitigation: OCR/翻訳の通常用途では think tag 混入防止を優先し、除去対象を `<think>` タグ形式に限定した。
- Risk: llama.cpp の将来バージョンで reasoning 引数の意味が変わる可能性がある。
- Mitigation: 起動確認で `thinking = 0` と `/health` ready を確認し、応答後 sanitizer も併用した。

### Tests / Verification
- `python -m py_compile OcrServiceVisionLlm\vision_llama_engine.py OcrServiceVisionLlm\server.py OcrServiceVisionLlm\test_vision_llama_engine.py`
- `uv run python -c "from vision_llama_engine import strip_thinking_content; ..."` による `<think>` 除去ケース確認
- `llama-server.exe` を `--reasoning off --reasoning-budget 0 --chat-template-kwargs {"enable_thinking":false}` 付きで起動し、`/health` ready とログ上の `thinking = 0` を確認

**2026-05-28 10:36 (Asia/Taipei) — LlamaCPP/VisionLLM MTP対応**

### Summary
- LlamaCPP翻訳とVisionLLMに、advanced UIから有効化できるMTP speculative decoding設定を追加した。

### Context / Goal
- MTP対応GGUFモデルで `llama-server` の `draft-mtp` を使えるようにしたい。
- 既存モデルへの影響を避けるため、MTPは既定OFFとし、詳細設定から明示的に有効化する。

### Changes
- アプリ設定にLlamaCPP翻訳/VisionLLMそれぞれのMTP有効化フラグとdraft token数を追加した。
- 設定正規化でdraft token数を1-16へクランプするようにした。
- C# hostからPython gRPCサービスへ `--enable-mtp` / `--mtp-draft-tokens` を渡すようにした。
- Python側でMTP有効時に `llama-server` へ `--spec-type draft-mtp` / `--spec-draft-n-max` を渡すようにした。
- 翻訳設定UIとVisionLLM設定UIへadvanced MTP設定を追加した。
- MTP検証用スクリプトにMTP引数を追加した。
- Thinking無効化は `--reasoning off` / `--reasoning-budget 0` に整理し、非推奨の `chat_template_kwargs` / `reasoning_format` 指定を外した。
- LlamaCPP翻訳の応答境界にも `<think>` 除去処理を追加した。

### Files Touched
- `Models/AppSettings.cs` — MTP設定値を追加した。
- `Services/Settings/SettingsHostNormalizer.cs` — MTP draft token数の正規化を追加した。
- `ViewModels/SettingsViewModel.cs` — MTP設定の読み書きと保存通知を追加した。
- `Services/Application/ResourceHostFacade.cs` — LlamaCPP host configへMTP設定を渡すようにした。
- `Services/LlamaGrpcHost.cs` — 翻訳gRPCサービス起動引数へMTP設定を追加した。
- `Services/VisionLlmGrpcHost.cs` — VisionLLM gRPCサービス起動引数へMTP設定を追加した。
- `TranslationServiceLlama/llama_engine.py` — `draft-mtp`起動引数、Thinking抑止整理、thinkタグ除去を追加した。
- `TranslationServiceLlama/server.py` — MTP CLI引数を追加した。
- `TranslationServiceLlama/test_translation_engine.py` — MTP検証引数を追加し、旧Thinking payload指定を整理した。
- `TranslationServiceLlama/test_llama_vision_ocr.py` — 直接llama-server検証用のMTP引数を追加し、旧Thinking payload指定を整理した。
- `OcrServiceVisionLlm/vision_llama_engine.py` — `draft-mtp`起動引数とThinking抑止整理を追加した。
- `OcrServiceVisionLlm/server.py` — MTP CLI引数を追加した。
- `OcrServiceVisionLlm/test_vision_llama_engine.py` — MTP検証引数を追加した。
- `Resources/Strings.resx` — MTP UI文言を追加した。
- `Resources/Strings.ja.resx` — MTP UI文言を追加した。
- `UI/TranslationControl.xaml` — LlamaCPP翻訳のadvanced MTP UIを追加した。
- `UI/VisionLlmSettingsControl.xaml` — VisionLLMのadvanced MTP UIを追加した。

### Behavioral Impact
- 既定ではMTPは無効のため、既存モデルの起動引数はThinking整理を除き変わらない。
- MTPを有効化した場合のみ、対応モデルで追加のMTP contextを作成し speculative decoding を使う。
- MTP非対応モデルで有効化した場合は、`llama-server` 起動時に明示的に失敗する可能性がある。
- Thinking無効時は非推奨payloadを送らず、翻訳/VisionLLMとも `--reasoning off` を使う。

### Risk & Mitigation
- Risk: MTP有効時は追加メモリを消費し、非対応モデルでは起動失敗する。
- Mitigation: 既定OFF、advanced UI配置、draft token数を1-16へクランプ。
- Risk: llama.cppのMTP引数仕様が将来変わる可能性がある。
- Mitigation: 現在同梱/配置バイナリの `--spec-type draft-mtp` / `--spec-draft-n-max` で起動検証済み。
- Risk: 実文字列にリテラルの `<think>` タグが含まれる場合、翻訳結果から除去される。
- Mitigation: ユーザー可視のThinking漏れ防止を優先し、除去対象をタグ形式に限定した。

### Tests / Verification
- `python -m py_compile TranslationServiceLlama\llama_engine.py TranslationServiceLlama\server.py TranslationServiceLlama\test_translation_engine.py TranslationServiceLlama\test_llama_vision_ocr.py OcrServiceVisionLlm\vision_llama_engine.py OcrServiceVisionLlm\server.py OcrServiceVisionLlm\test_vision_llama_engine.py`
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="<temp>"` — 成功。通常出力先は実行中の `Hotkey-Translator.exe` / `.dll` ロック回避のため未使用。
- `uv run python test_translation_engine.py --auto-start-server ... --model ".\LlamaCpp\Models\Qwen3.5-4B-UD-MTP-Q4_K_XL.gguf" --enable-mtp --mtp-draft-tokens 3 --disable-thinking` — `draft-mtp` 初期化、draft acceptance統計、翻訳出力を確認。
- `uv run python test_vision_llama_engine.py --mode translate ... --model "..\TranslationServiceLlama\LlamaCpp\Models\Qwen3.5-4B-UD-MTP-Q4_K_XL.gguf" --mmproj "..\TranslationServiceLlama\LlamaCpp\Models\mmproj-Qwen3.5-4B-BF16.gguf" --enable-mtp --mtp-draft-tokens 3 --disable-thinking` — VisionLLM経路でMTP有効の翻訳出力を確認。

**2026-06-16 14:28 (Asia/Taipei) — 通常オーバーレイのtopmost再同期**

### Summary
- 通常WPFオーバーレイ表示中もMagpie mirror同等のtopmost再同期を行うようにした。

### Context / Goal
- 一部ゲームで画面取得、翻訳、overlay描画データは正常だが、WPF overlayがゲーム画面の下に回る可能性がある。
- まずZ-order負けかどうかを低コストに確認できる暫定対策を入れる。

### Changes
- Magpie専用だったtopmost再同期timerを通常WPF overlay兼用に変更した。
- 通常overlayは表示内容がある間だけ `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` を再投入するようにした。
- overlay表示/更新時に即時topmost昇格し、表示内容が消えたらtimerを停止するようにした。
- `OverlayPresenter` に表示内容有無を判定する読み取り状態を追加した。

### Files Touched
- `MainWindow.xaml.cs` — topmost再同期timerと昇格処理をMagpie専用から通常overlay兼用へ変更した。
- `Services/OverlayPresenter.cs` — overlay有効状態と表示内容有無を外部から参照できる状態を追加した。

### Behavioral Impact
- 通常WPF overlayに翻訳テキストが表示されている間、500ms間隔でtopmost再同期する。
- 空の常駐overlay windowでは再同期しないため、他のtopmost UIとの不要な競合を抑える。
- Graphics Hook overlayでWPF overlayがclearされる経路では、通常overlay用timerは停止する。

### Risk & Mitigation
- Risk: 他アプリのtopmost UIとZ-order競合する可能性がある。
- Mitigation: `SWP_NOACTIVATE` を維持し、表示内容がある場合のみtimerを有効化する。
- Risk: exclusive fullscreenやindependent flipではWPF overlay自体が前面に出ない可能性が残る。
- Mitigation: 本変更で改善しない場合はhook overlayまたは表示モード側の調査へ切り分ける。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 成功。警告0、エラー0。

**2026-06-22 13:56 (Asia/Taipei) — 自動clipboardコピー設定のSystemタブ移動**

### Summary
- 自動clipboardコピーのチェック項目をAuto TranslateタブからSystemタブへ移動した。

### Context / Goal
- `Copy OCR and translation to clipboard after overlay update` は翻訳条件ではなくOS clipboardへ副作用を持つ全体設定である。
- Systemタブ内のClipboard設定として見えるようにする。

### Changes
- Auto Translateタブから自動clipboardコピーのチェックボックスを削除した。
- Systemタブに `Clipboard` セクションを追加し、同じ設定バインディングのチェックボックスを配置した。
- UI文言キーを `AutoTranslate_*` から `SystemSettings_*` へ移した。

### Files Touched
- `UI/OverlayBehaviorControl.xaml` — 自動clipboardコピーのチェックボックスを削除した。
- `UI/SystemSettingsControl.xaml` — Clipboardセクションとチェックボックスを追加した。
- `Resources/Strings.resx` — 英語UI文言キーをSystemSettings配下へ移した。
- `Resources/Strings.ja.resx` — 日本語UI文言キーをSystemSettings配下へ移した。

### Behavioral Impact
- 設定の保存先と実行挙動は変わらない。
- ユーザーが設定を変更する場所がAuto TranslateタブからSystemタブへ変わる。

### Risk & Mitigation
- Risk: 既存のAuto Translateタブ内に設定が見つからなくなる。
- Mitigation: 機能の副作用に合わせてSystem > Clipboardへ移動し、表示文言は同じ意味を維持した。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 1回目はWPF一時生成 `.g.cs` 欠落で失敗、同一コマンド再実行で成功。警告0、エラー0。

**2026-06-22 13:41 (Asia/Taipei) — OCR翻訳結果の自動clipboardコピー**

### Summary
- OCR原文と翻訳文をoverlay更新後にclipboardへ自動コピーするopt-in機能を追加した。

### Context / Goal
- OCR、翻訳完了、overlay表示のタイミングで、原文内容と翻訳文の内容をclipboardへ保存したい。
- overlay表示用textではなく、pipelineで確定した原文/翻訳の対応を出力元にする。

### Changes
- 自動clipboardコピー設定を追加し、既定OFFにした。
- pipelineの通常OCR/翻訳経路とprecomputed reading units経路で、overlay publish後に確定snapshotを通知するようにした。
- MainWindow側でUI Dispatcher経由のclipboard書き込みを行い、失敗時はログだけ残すようにした。
- paired text formatterとsnapshot modelを追加した。
- Auto Translate設定画面に自動コピー用チェックボックスを追加した。

### Files Touched
- `Models/AppSettings.cs` — `AutoCopyOcrTranslationToClipboard` 設定を追加し、clipboard上書きのopt-in理由をコメントした。
- `Models/TextExportItem.cs` — clipboard出力1単位の原文/翻訳pair modelを追加した。
- `Models/TextExportSnapshot.cs` — pipeline確定結果のclipboard出力snapshotを追加した。
- `Services/TextExportService.cs` — paired text formatterを追加した。
- `Services/PipelineOrchestrator.cs` — overlay publish後に自動clipboard snapshotイベントを発火する処理を追加した。
- `MainWindow.xaml.cs` — snapshotイベント購読とUIスレッドでのclipboard書き込みを追加した。
- `ViewModels/SettingsViewModel.cs` — 新設定の読み込み、保存、変更時保存を追加した。
- `UI/OverlayBehaviorControl.xaml` — 自動clipboardコピーのチェックボックスを追加した。
- `Resources/Strings.resx` — 英語UI文言を追加した。
- `Resources/Strings.ja.resx` — 日本語UI文言を追加した。
- `Doc/AutoClipboard_OCR_Translation_Implementation_Plan.md` — 実装方針ドキュメントを追加した。

### Behavioral Impact
- 設定ON時、通常OCR/翻訳pipelineまたはprecomputed payloadのoverlay更新後に、clipboardへ `[n] Original / Translation` 形式のtextが入る。
- 設定OFF時は既存どおりclipboardを変更しない。
- clipboard書き込みに失敗してもOCR/翻訳pipelineは失敗扱いにしない。

### Risk & Mitigation
- Risk: 自動コピーによりユーザーのclipboard内容を上書きする。
- Mitigation: 既定OFFの明示opt-in設定にし、設定コメントにも理由を残した。
- Risk: WPF clipboard APIをpipelineスレッドから呼ぶとSTA制約で失敗する。
- Mitigation: MainWindow Dispatcher経由でUIスレッドから `Clipboard.SetText` を呼ぶ。
- Risk: overlay用整形textを使うと原文/翻訳の対応が崩れる。
- Mitigation: `ReadingUnit` と `translations` から作る確定snapshotのみを出力元にした。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 成功。警告0、エラー0。

**2026-06-28 18:00 (Asia/Taipei) — OCR訂正実装案ドキュメント追加**

### Summary
- SymSpell候補生成と自前OcrWeightedEditDistanceによるOCR訂正導入案をDocへ追加した。

### Context / Goal
- このリポの共通OCR pipelineへOCR訂正仕組みを導入する方向性を、実装前に設計として整理する。
- SymSpellと自前weighted edit distanceの責務、差し込み位置、安全な自動採用条件を明確にする。

### Changes
- OCR訂正を各OCRエンジンではなくC#本体の共通 `OcrLine` 後段へ入れる実装案を作成した。
- SymSpell候補生成、辞書ロード、tokenizer、weighted edit distance、自動採用条件、ログ、UI、テスト方針を整理した。

### Files Touched
- `Doc/OcrCorrection_SymSpell_WeightedEditDistance_Implementation_Plan.md` — OCR訂正導入の実装案を新規追加した。

### Behavioral Impact
- ドキュメント追加のみ。アプリの実行時挙動は変わらない。

### Risk & Mitigation
- Risk: 実装案と実装時の既存pipelineがずれる。
- Mitigation: 現行の `OcrAndGroupStage` / `ReadingUnit` / `TranslateStage` の流れに合わせて差し込み位置と影響範囲を明記した。

### Tests / Verification
- ドキュメント追加のみのためビルドは未実施。

**2026-08-01 10:38 (Asia/Taipei) — ROI選択のDPI二重変換修正**

### Summary
- ROI選択結果とHookプレビュー矩形で、`PointToScreen()` 後にDPI変換を二重適用していた処理を削除した。

### Context / Goal
- Windowsの表示倍率が100%以外のとき、ROI選択位置と実際のキャプチャ/オーバーレイ位置がズレる。
- ROI選択ウィンドウから返す矩形を、下流のキャプチャ/オーバーレイが期待する絶対スクリーン座標に統一する。

### Changes
- ROI確定時の `DpiHelper.DipRectToDevice()` 呼び出しを削除し、`PointToScreen()` で得たスクリーン矩形をそのまま保存対象にした。
- ROIドラッグ中のHookプレビュー通知も同じスクリーン矩形をそのまま渡すようにした。
- コメントを現在の座標契約に合わせて更新した。

### Files Touched
- `UI/RoiSelectorWindow.xaml.cs` — ROI選択結果とプレビュー通知のDPI二重変換を削除した。

### Behavioral Impact
- Windows表示倍率が100%以外でも、ROI選択位置がキャプチャ範囲とオーバーレイ表示に一致しやすくなる。
- 既存のキャプチャ/オーバーレイ側のスクリーン座標契約は変更しない。

### Risk & Mitigation
- Risk: `PointToScreen()` の戻り値を期待する座標系が環境依存の場合、別DPI環境で差が出る可能性がある。
- Mitigation: 下流経路の `ScreenRectToWindowDip()` / `TryBuildHookCanvasRect()` / `CopyFromScreen()` がスクリーン座標前提であることを確認し、二重変換のみを削除した。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 成功。警告0、エラー0。

**2026-08-13 10:04 (Asia/Taipei) — ROI選択後の即時翻訳ホットキー追加**

### Summary
- ROIを選択・保存した直後にOCRと翻訳を実行する、設定可能な追加ホットキーを実装した。

### Context / Goal
- ROI選択後に別の実行ホットキーを押す手間をなくす。
- 既存のF6によるROI選択のみの挙動は維持する。

### Changes
- 既定無効の「ROIを選択して翻訳」ホットキー設定と競合検出を追加した。
- ROI確定後にpHashとOCR差分判定をスキップし、翻訳キャッシュを利用して即時翻訳する経路を追加した。
- ホットキー設定画面と日本語・英語の表示文言を追加した。

### Files Touched
- `Models/AppSettings.cs` — 新ホットキーの永続化項目を追加した。
- `Models/HotkeyDefaults.cs` — 既定無効の初期値を追加した。
- `ViewModels/SettingsViewModel.cs` — 設定同期、保存、競合検出を追加した。
- `UI/HotkeysControl.xaml` — 新ホットキーの設定行を追加した。
- `UI/HotkeysControl.xaml.cs` — 設定候補と競合表示の接続を追加した。
- `Services/Application/HotkeyCommandController.cs` — ROI選択・翻訳コマンドを追加した。
- `MainWindow.xaml.cs` — ホットキー登録とROI確定後の翻訳実行を追加した。
- `Resources/Strings.resx` — 英語表示名を追加した。
- `Resources/Strings.ja.resx` — 日本語表示名を追加した。

### Behavioral Impact
- 新ホットキーを割り当てると、ROI確定後に範囲を保存して一度だけ翻訳表示する。キャンセル時は翻訳しない。
- 既存のROI選択ホットキーと既定割り当ては変更しない。

### Risk & Mitigation
- Risk: ROI変更直後でも過去の画像・OCR差分により翻訳が抑止される可能性がある。
- Mitigation: 新ホットキー経路だけpHashとOCR差分をスキップし、翻訳キャッシュは維持して応答時間を抑える。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 成功。警告0、エラー0。
- `git diff --check` — エラーなし。

**2026-08-17 00:02 (Asia/Taipei) — 重複文字列への翻訳結果配布を修正**

### Summary
- キャッシュを無視する強制翻訳でも、完全一致する重複文字列の全表示箇所へ翻訳結果を反映するよう修正した。

### Context / Goal
- 同じOCR文字列が複数箇所にある場合、強制翻訳では最初の1箇所だけが翻訳され、残りが原文表示になる問題があった。
- 翻訳APIへの重複送信を増やさず、今回成功した結果を安全に全該当箇所へ配布する。

### Changes
- 用語集保護後の翻訳入力が完全一致する単位ごとに、対応する Unit ID を収集するようにした。
- 今回取得・復元した翻訳結果を、同じ翻訳入力を持つすべての Unit ID に設定するようにした。
- 正規化だけが一致する異なる文字列や、過去の翻訳結果は配布対象にしない。

### Files Touched
- `Services/Orchestration/Stages/TranslateStage.cs` — 現在の翻訳結果を完全一致する重複単位へ配布する処理を追加した。

### Behavioral Impact
- 通常実行と強制実行のどちらでも、同じ翻訳入力が複数箇所にあれば全箇所に同じ翻訳文が表示される。
- キャッシュ判定、翻訳APIへの送信件数、正規化だけが一致する文字列の扱いは変更しない。

### Risk & Mitigation
- Risk: 正規化キーで結果を配布すると、句読点や記号だけが異なる文字列へ誤って同じ翻訳を適用する可能性がある。
- Mitigation: 配布キーを用語集保護後の翻訳入力の完全一致に限定し、今回成功した結果だけを使用する。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 成功。警告0、エラー0。
- `git diff --check` — エラーなし（改行コード変換に関する Git 警告のみ）。

**2026-08-17 00:21 (Asia/Taipei) — ウィンドウタイトルへバージョン表示を追加**

### Summary
- メインウィンドウのアプリ名の後ろに、プロジェクト設定を参照した `v1.0.10` を表示するようにした。

### Context / Goal
- 実行中のアプリバージョンをタイトルバーから確認できるようにする。
- 表示用の固定値を別管理せず、ビルド成果物のバージョンと同期させる。

### Changes
- プロジェクトの `Version` を `1.0.10` に設定した。
- 実行アセンブリの3桁バージョンをローカライズ済みアプリ名へ付加するタイトル更新処理を追加した。
- UI言語変更後もバージョン付きタイトルを再構成するようにした。

### Files Touched
- `Hotkey-Translator.csproj` — アプリケーションバージョン `1.0.10` を定義した。
- `MainWindow.xaml.cs` — 起動時およびUI言語変更時にバージョン付きタイトルを設定するようにした。

### Behavioral Impact
- メインウィンドウのタイトルが `Hotkey Translator v1.0.10` と表示される。
- アセンブリバージョンは `1.0.10.0` となり、タイトルでは3桁の `1.0.10` を使用する。

### Risk & Mitigation
- Risk: コードからタイトルを設定すると、既存のローカライズバインディングが直接更新されなくなる。
- Mitigation: UI言語変更イベントでもローカライズ済み名称からタイトルを再構成する。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 成功。警告0、エラー0。
- 生成DLLを確認し、AssemblyVersion=`1.0.10.0`、タイトル用表示=`1.0.10` となることを確認した。
- `git diff --check` — エラーなし（改行コード変換に関する Git 警告のみ）。

**2026-08-17 16:02 (Asia/Taipei) — OneOCRの最小画像サイズを余白補正**

### Summary
- 50px未満の辺を持つ画像へ右・下の白色余白を追加し、OneOCRで認識できるようにした。

### Context / Goal
- OneOCR本体は幅・高さの各辺に50pxの最小サイズ制限がある。
- 小さいキャプチャを拡大せず、元画像基準のOCR座標を維持して処理したい。

### Changes
- OneOCR helperの画像デコード時に、50px未満の辺を50pxまで白色でパディングするようにした。
- パディングを右端・下端だけに追加し、OCR結果のline/word polygonを元画像範囲へクリップした。
- 応答の画像サイズは元サイズを維持し、補正時は元サイズと補正後サイズを標準エラーへ記録するようにした。
- 10000pxを超える入力は従来どおり明示的に失敗させる。

### Files Touched
- `Native/OneOcrHelper/main.cpp` — 最小サイズ補正、元サイズ保持、座標クリップ、補正ログを追加した。

### Behavioral Impact
- 幅または高さが50px未満の画像でも、OneOCR helperが再起動エラーにならずOCRを実行できる。
- OCR応答の画像サイズと座標原点は元画像基準のまま変わらない。

### Risk & Mitigation
- Risk: 追加した余白をOneOCRが文字領域として検出し、元画像外の座標を返す可能性がある。
- Mitigation: 余白を白色にし、返却する全polygonを元画像範囲へクリップする。

### Tests / Verification
- `cmake --build . --config Release --target OneOcrHelper` — 成功。
- Named Pipe経由の実DLLテストで `49x100`、`100x49`、`50x50` が成功し、応答サイズが各入力の元サイズと一致することを確認した。
- `49x100 -> 50x100` と `100x49 -> 100x50` の補正ログが出力されることを確認した。
- `git diff --check` — エラーなし（改行コード変換に関するGit警告のみ）。

**2026-08-25 10:19 (Asia/Taipei) — 翻訳言語変更時のキャッシュ分離と再翻訳**

### Summary
- 翻訳先言語などの翻訳スコープ変更時に旧言語の直近訳を再利用せず、同一画面でも再翻訳するようにした。

### Context / Goal
- 実行中の直近訳キャッシュが翻訳言語をキーに含まず、同じ原文へ前回言語の訳を返していた。
- 同一画面ではpHashとOCR差分により、翻訳設定変更後の再翻訳が抑止されていた。

### Changes
- 永続キャッシュと直近訳キャッシュで、翻訳元・翻訳先言語、スタイル、用語集スコープを含む共通キーを使用するようにした。
- 翻訳スコープ変更後の通常翻訳では、1回だけpHashとOCR差分を無効化して全ReadingUnitを処理するようにした。
- 全ReadingUnitの翻訳が揃った場合だけスコープを確定し、部分成功時は次回再試行できるようにした。
- OCR専用実行とForce Gemini strict実行では通常翻訳スコープを確定しないようにした。

### Files Touched
- `Services/CacheKeyBuilder.cs` — 翻訳スコープ生成を分離し、キャッシュキー生成と共用した。
- `Services/Orchestration/Stages/TranslateStage.cs` — 直近訳のキーを永続キャッシュと同じ設定スコープへ変更した。
- `Services/PipelineOrchestrator.cs` — 翻訳スコープ変更検知、pHash/OCR差分の一時無効化、成功時のスコープ確定を追加した。

### Behavioral Impact
- 翻訳言語、スタイル、または用語集スコープを変更すると、画面の原文が同じでも新しい設定の訳が表示される。
- 翻訳設定が同一の場合は従来どおりキャッシュを再利用する。

### Risk & Mitigation
- Risk: 設定変更直後は同一画面でもOCRとキャッシュ照会または翻訳API呼び出しが1回増える。
- Mitigation: 翻訳結果に影響するスコープが変わった場合だけ再処理し、全件成功後は通常の差分判定へ戻す。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 成功。警告0、エラー0。
- `git diff --check` — エラーなし（改行コード変換に関するGit警告のみ）。

**2026-08-26 10:21 (Asia/Taipei) — Custom翻訳先入力欄の位置調整**

### Summary
- Custom選択時の翻訳先入力欄を翻訳元入力欄と同じ横位置に揃えた。

### Context / Goal
- 翻訳元の行には言語入替ボタン用の列がある一方、翻訳先の行にはなく、Custom入力欄の開始位置がずれていた。
- Source/TargetのCustom入力欄を縦に整列させる。

### Changes
- 翻訳先グリッドへ入替ボタン相当の空き列を追加した。
- 翻訳先のCustom入力欄を3列目へ移動した。

### Files Touched
- `UI/OverviewControl.xaml` — 翻訳先言語行の列構成とCustom入力欄の配置を調整した。

### Behavioral Impact
- Source/Targetの両方でCustomを選択した際、各入力欄の左端が同じ位置に揃う。
- 言語選択、入力値、翻訳処理の挙動は変わらない。

### Risk & Mitigation
- Risk: 言語設定領域の横幅が狭い場合に、翻訳先入力欄が収まりにくくなる可能性がある。
- Mitigation: 翻訳元と同一の既存列幅を使用し、セクションの既存最小幅内に収める。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 成功。警告0、エラー0。
- `git diff --check` — エラーなし（改行コード変換に関するGit警告のみ）。

**2026-08-26 10:25 (Asia/Taipei) — Custom入力文字の垂直中央揃え**

### Summary
- Source/TargetのCustom入力文字とカーソルを入力欄内で垂直中央に揃えた。

### Context / Goal
- Custom入力欄の文字が上寄りに表示され、言語選択欄との視覚的な整列が崩れていた。
- テーマ既定のサイズと余白を維持しながら、入力内容の縦位置を調整する。

### Changes
- Source/TargetのCustom用TextBoxへ`VerticalContentAlignment="Center"`を追加した。

### Files Touched
- `UI/OverviewControl.xaml` — Custom入力欄の垂直コンテンツ配置を中央へ変更した。

### Behavioral Impact
- Custom入力時の文字とカーソルが入力欄内の垂直中央に表示される。
- 入力値、保存、翻訳処理の挙動は変わらない。

### Risk & Mitigation
- Risk: 適用中のTextBoxテーマによって中央位置の見え方にわずかな差が生じる可能性がある。
- Mitigation: 固定Paddingを使わず、WPFのコンテンツ配置プロパティだけを指定する。

### Tests / Verification
- `dotnet build .\Hotkey-Translator.csproj -p:OutputPath="artifacts\agent-build\"` — 成功。警告0、エラー0。
- `git diff --check` — エラーなし（改行コード変換に関するGit警告のみ）。

**2026-08-26 11:00 (Asia/Taipei) — ユーザー操作ガイド作成**

### Summary
- 現在の操作UIに基づく文字のみのユーザー向け操作ガイドを作成した。

### Context / Goal
- 画面内の各操作と設定の用途を、既存ドキュメントに依存せず利用者が確認できるようにする。
- 初回設定、日常操作、詳細設定、トラブル時の確認手順を1つのMarkdownへまとめる。

### Changes
- 基本的なOCR／翻訳手順と既定ホットキーを記載した。
- 全サイドバーメニュー、下部プレビュー／ログ、ROIスロット、Hook、各エンジン設定の操作を記載した。
- よくある問題の確認手順を追加した。

### Files Touched
- `Doc/UserGuide.md` — 現行UIに対応するユーザー操作ガイドを新規作成した。
- `.agent/changes.md` — 本タスクの変更内容を追記した。

### Behavioral Impact
- アプリの実行挙動に変更はない。利用者向けドキュメントのみ追加される。

### Risk & Mitigation
- Risk: UI変更後にドキュメントの記載が古くなる可能性がある。
- Mitigation: 現在のXAML、ローカライズ文字列、操作処理、既定ホットキーを照合して記載した。

### Tests / Verification
- 既存の `Doc/` 内ファイルを参照せず、現行UIソースとの項目照合を実施した。
- アプリコードは変更していないためビルドは未実施。

**2026-09-09 13:34 (Asia/Taipei) — Vulkan readbackの完了回収・寿命管理・計測を改善**

### Summary
- Vulkan改善計画のStep 1～3に対応するnative変更と回帰テストを実装し、x64ビルド・実GPUのstaging同期検証を実施した。

### Context / Goal
- publish idle通知とenqueueの競合、capture FPS gateによる完了回収遅延、GPU未完了resourceの破棄リスクを解消する。
- workerとPresentの負荷を分けて測定し、後続のoverlay非同期化を判断できるようにする。

### Changes
- queue lock内でidle eventのReset/Setを行い、drainの述語再確認・worker終了/待機失敗検出を追加した。
- pending GPU回収をcapture発行判定より前へ移し、低capture FPSでも完了済みslotを回収するようにした。
- GPU submitの未完了状態と失敗状態を追跡し、再作成・破棄・reset/detach前のGPU retirementとworker drainを分離した。
- worker/Presentの期間集計、capture age、queue/発行/publish/defer/busy診断を追加した。
- staging payload bytesを画像サイズに修正し、allocation paddingの読み出しと不要な再作成を防止した。copy後のHOST_READ memory dependencyも追加した。
- 実コードのworker、Windows event、V2 writerを使う回帰テストと、実GPU/同期検証を使う任意実行モードを追加した。
- ユーザーの追加指示に従い、x86ビルド・テストを以後スキップした。

### Files Touched
- `Native/HookAgentVulkan/VulkanPresentHook.cpp` — 完了回収、idle通知、GPU/worker寿命、失敗時の再利用防止、診断とpayloadサイズを修正。
- `Native/HookAgentVulkan/tests/VulkanReadbackTests.cpp` — FPS gate、所有権、画素、世代、失敗、実GPU同期の回帰テストを追加。
- `Native/HookAgentVulkan/CMakeLists.txt` — 任意のHookAgentVulkanTests targetとCTest登録を追加。
- `Native/CMakeLists.txt` — HT_VULKAN_BUILD_TESTSオプションを追加（既定OFF）。
- `Doc/GraphicsHook_Vulkan_Performance_Next_Steps.md` — 調査時点を保持して実装状況、未実装の後続Step、x64検証手順とログの意味を追記。
- `.agent/changes.md` — 本タスクの結果を追記。

### Behavioral Impact
- capture 1/5/15 FPSでもGPU完了回収が次のcapture発行周期を待たない。captureの発行間隔は維持する。
- FramePipe V2/BGRA8契約は維持する。画像外のallocator paddingはpublishしない。
- fence/submit失敗時はqueueのresource再作成まで新規capture/hook overlay処理を停止する。GPU未完了のcommand poolを再利用しない。
- overlay-only/immediateのCPU wait、present semaphore chain、BGRA中間copy、consumer鮮度制御は今回変更していない。

### Risk & Mitigation
- Risk: teardownでのGPU完了待ちや診断有効時の集計が停止時間に影響し得る。
- Mitigation: 通常のGPU完了回収は非ブロッキングとし、blocking waitはresource再作成/破棄時に限定。追加計測は既存診断フラグで制御し、サンプル保存数を固定した。
- Risk: swapchain/ImGuiを含む実ゲーム構成はstaging単体試験で保証できない。
- Mitigation: 計画書でstaging検証とWSI/ゲーム性能を区別し、未検証のsemaphore再利用・破棄を前提にoverlay waitを削除しない。

### Tests / Verification
- SDK: G:/Development-Cache/VulkanSdk（Vulkan headers 1.4.341）。
- x64 Release: HookAgentVulkan、HookAgentVulkanTestsのビルド成功。
- `ctest --test-dir Native/build -C Release --output-on-failure` — 成功（VulkanReadback）。低FPS、2,000回のidle/enqueue遷移、active writer保持、画素/世代/失敗、GPU retirement、worker終了、計測時刻の順序を検証。
- `HookAgentVulkanTests.exe --gpu` — GTX 1080で12回の実GPU staging→fence→worker publishと破棄に成功。Validation Layers/synchronization validationのerrorは0。試験プロセスのみimplicit layerを無効化。
- x86は指示前に公式Khronos Vulkan-Loader v1.4.341のimport libraryでコンパイル・リンク成功。ただし実行はアンチウイルスに阻まれ、ユーザー指示後はビルド/テストをスキップ。セキュリティ設定は未変更。
- `git diff --check` — 成功（GitのLF/CRLF変換に関する注意のみ）。
- 実ゲームの1% low、overlay/WSI、Alt+Tab/detachの実動作検証は未実施。性能改善率は未確定。

### Open Questions
- Step 4のpresent semaphore再利用・破棄とmulti-swapchain/queue familyの検証が残る。
- Step 5のcopy/backlog改善はworker計測後、Step 6のconsumer鮮度制御は許容ageとtimestamp契約の確定後に実施する。

**2026-09-10 12:09 (Asia/Taipei) — Vulkan overlayの非同期化とBGRA中間コピー削減**

### Summary
- Vulkan改善計画のStep 4とStep 5のコピー削減を実装し、x64実GPU/WSIの同期検証に合格した。

### Context / Goal
- 前回のStep 1～3についてユーザーの実機確認で大きな問題がないとの報告を受け、実装を継続した。
- 通常overlayのCPU fence待機を外し、ゲーム描画からPresentまでのGPU依存とworkerのmapped memory寿命を維持する。

### Changes
- 元のPresent wait semaphore全件をhook submitでwaitし、コマンド完了後に同じ全件を再signalする同期を追加した。元PresentのpNext、複数swapchain、pResultsは保持する。
- overlay単独submitを非同期化し、ImGui描画の同時実行を1件に制限。未完了時はCPUを待たせずoverlayをスキップする。overlayBusyTotalを追加した。
- 描画先/device/image count変更では旧GPU使用を退役させてImGui backendを再初期化する。
- swapchain作成時に対応surfaceへ必要usageを追加し、queue family/usage/protected/shared-present等の対応範囲を明示した。submit失敗はVkResultへ伝播する。
- BGRAはPublishing slotのmapped pointerを同期WriteFrameへ直接渡して中間memcpyを削除。RGBA変換とV2契約は維持した。
- 実Win32 surfaceとswapchainを使う任意の--present試験、対応範囲・overlay所有権・submit失敗の回帰試験を追加した。

### Files Touched
- `Native/HookAgentVulkan/VulkanPresentHook.cpp` — Present同期、非同期overlay、ImGui再初期化、usage/ownershipガード、BGRA直接publishと診断。
- `Native/HookAgentVulkan/tests/VulkanReadbackTests.cpp` — 同期ガード、overlay fence、実GPU semaphore chain、V2画素、submit失敗注入。
- `Native/HookAgentVulkan/tests/VulkanPresentSmoke.h` — 実GPU/WSIの複数swapchain、描画先切替、サイズ変更を伴う再初期化、終了試験。
- `Doc/GraphicsHook_Vulkan_Performance_Next_Steps.md` — 第14節に方式、互換性上の制約、検証結果、残るStepを追記。
- `.agent/changes.md` — 本タスクの結果を追記。

### Behavioral Impact
- 通常delayed/overlayはhookが追加するCPU fence待機を行わない。初期化、resource切替・破棄、明示immediate設定の待機は残る。
- ImGuiがGPU使用中の場合はその回のoverlay描画をスキップする。
- 途中attachでswapchain作成情報がない場合、exclusive sharingでdevice作成情報がない場合、複数queue familyのexclusive等はcapture/overlayをスキップする。以前動作していた構成にも影響するため計画書に明記した。
- hook submit失敗時はoriginal Presentを呼ばず、戻り値と全pResultsへ失敗を返す。送信前skipは元Presentを継続する。
- BGRAの共有メモリへのコピーは1回になり、RGBA変換、stride、frameId、timestamp、IPC形式は維持する。

### Risk & Mitigation
- Risk: binary semaphoreの順序・再利用やGPU使用中のImGui buffer再利用が不正になる可能性。
- Mitigation: 元wait全件をwait/re-signalし、元Presentを保持。GPU fenceによる再利用判定、失敗時のqueue停止、実WSIの同期検証で確認した。
- Risk: queue ownershipやimage usage不明の途中attachではcaptureが停止する。
- Mitigation: 推測によるGPU accessを追加せず診断を出す。作成時からhookが有効な構成が必要であることを文書に明記した。
- Risk: mapped pointer直接publish中のunmapやslot再利用。
- Mitigation: GPU完了後にworkerへ渡し、同期WriteFrame完了までPublishing所有権を保持。writerを遅らせる既存の所有権テストにも合格した。

### Tests / Verification
- SDK: G:/Development-Cache/VulkanSdk。x64 ReleaseのHookAgentVulkan/HookAgentVulkanTestsビルド成功。
- CTest VulkanReadback: 合格。低FPS、色変換、idle/enqueue 2,000回、worker所有権、retirement、同期ガード、overlay fenceを検証。
- `HookAgentVulkanTests.exe --present`: GTX 1080で実stagingの12 publish、2 swapchainの72 Present、overlay描画先切替、サイズ変更を伴うdetach/再初期化、V2画素と範囲、submit失敗注入を確認。Validation Layers/synchronization validation error 0。
- 通常production hook経路のCPU fence待機呼出し数が増えないことを確認。テストゲーム自身のcommand buffer再利用待機と描画先切替は別扱い。
- x86ビルド・テストはユーザー指示どおりスキップ。セキュリティ設定は変更していない。
- `git diff --check`で空白エラーなし。今回変更後の実ゲーム性能、DLL injection、Alt+Tab、device lost、OUT_OF_DATEは未検証。

### Open Questions
- 実ゲームの1% low、hook p99、capture ageとoverlayBusyTotalの変更前後比較は残る。
- Step 5のbacklog制御はqueue depth/ageの測定後、Step 6のconsumer鮮度制御はtimestamp契約と許容ageを合わせる別変更として残る。

**2026-09-11 11:17 (Asia/Taipei) — x86 Vulkan回帰の原因を変更前後比較で絞り込み**

### Summary
- 旧版では通過しStep 4追加後だけcapture/overlayの準備前に停止する4条件を、実装コードのx64比較プローブで再現した。

### Context / Goal
- ユーザー報告: 現行x64は正常、Step 4/5以前のx86も正常、現行x86ではOCR画像取得とoverlayが失敗。対象端末ログの取得は困難。
- x86をビルド・実行せず、変更差分と既存の情報補完経路の整合性を検証する。

### Changes
- 無視対象のNative/build/regression_analysis配下に調査用CMake/probeと旧コードスナップショットを作成した。製品コード・製品DLLは変更していない。
- 旧9bb7a95と現行af28a14の実際のHook_vkCreateDevice、Hook_vkGetDeviceQueue、SubmitPresentWorkLockedを使って比較した。
- Vulkan外部呼出しをstubにし、GPUコマンド記録/overlay render pass作成へ到達する境界で停止。ダミーハンドルをGPUへ渡さない。

### Files Touched
- `Native/build/regression_analysis/probe.cpp` — device作成/後補完からcapture・overlay入口までを比較する一時調査コード（Git無視対象）。
- `Native/build/regression_analysis/CMakeLists.txt` — x64専用の旧版/現行比較用ビルド（Git無視対象）。
- `Native/build/regression_analysis/old.cpp` — 9bb7a95のVulkanPresentHook.cppスナップショット（Git無視対象）。
- `.agent/changes.md` — 調査結果を追記。

### Behavioral Impact
- 製品動作の変更なし。既存のx86向けdevice後補完はsingleQueueFamilyを設定せず、Step 4で追加したexclusive sharingチェックに拒否されることを確認。
- 全device queue familyが単一という条件、単一physical deviceを含むdevice-group情報の一律拒否、wait semaphore数0の拒否も旧版との差分として再現した。

### Risk & Mitigation
- Risk: 再現した条件が対象ゲームで実際に発生したと誤認すること。
- Mitigation: x64の制御された比較試験であり、x86実機での原因確定や同期の安全性検証ではないと明示。安全チェックを単純削除する修正は行っていない。

### Tests / Verification
- x64プローブold/currentをReleaseビルドして実行、全比較条件で期待どおりの結果。
- 単一family・作成情報あり・waitあり: 旧版/現行ともcapture記録入口とoverlay準備入口へ到達。
- device後補完 / 別transfer family追加 / physical device 1個のdevice-group情報 / waitなし: 旧版は両入口へ到達、現行は両入口の前で停止。
- Step 5のworkerコピー処理に到達する前の停止であることを確認。
- x86ビルド・実行なし。製品DLL再ビルドなし。アンチウイルス設定変更なし。

### Open Questions
- 対象ゲームが後補完・複数family・device-group・waitなしのどの条件に該当するかは未確定。
- 情報補完経路と新しい必須条件の不整合が最有力。旧版で既に正常だったhook入口の未捕捉や一般的なビルド不備は優先度を下げる。

**2026-09-11 11:32 (Asia/Taipei) — Vulkanのdevice後補完とx86関数取得経路の回帰を修正**

### Summary
- Step 4の過剰なfamily制約と情報補完の不整合を修正し、instance経由のdevice関数取得をhookで捕捉するようにした。

### Context / Goal
- 旧x86は正常でStep 4/5後のx86だけ画像取得・overlayが失敗する報告に対し、再現した停止条件を修正する。
- 正常なPresentのVulkan所有権契約に基づいて対応範囲を戻し、未確認のGPU情報は推測しない。

### Changes
- device全体のsingleQueueFamily条件を廃止。Presentのqueue familyは既に画像の所有権を持つという仕様と、同じqueue/全wait semaphoreによる同期に基づく判定へ修正した。
- device作成情報の捕捉有無とphysical device数を分離。補完はinstanceにphysical deviceが1個の場合に限定し、swapchain作成時の補完も追加した。
- 単一GPUのdevice-groupとLOCAL/mask=1のPresentを許可。実際の複数GPU groupは拒否を維持した。
- GIPA/GDPA両経路でdevice関数を共通のhookへ振り分け、resolver自身の取得を捕捉。未対応関数のnullを保持し、登録をmutexで保護した。
- skip診断を詳細化。回帰試験と実GPUでのproc-address/WSI試験を拡張した。

### Files Touched
- `Native/HookAgentVulkan/VulkanPresentHook.cpp` — device情報、所有権条件、単一GPU group、instance/device lookup、補完と診断を修正。
- `Native/HookAgentVulkan/tests/VulkanDispatchTests.h` — lookup、作成情報、補完からcapture準備までの回帰試験を追加。
- `Native/HookAgentVulkan/tests/VulkanReadbackTests.cpp` — 判定テストを更新し、実GPUのinstance/device/queueをhook経由で作成。複数family・単一GPU groupを追加。
- `Native/HookAgentVulkan/tests/VulkanPresentSmoke.h` — 実hook経由のswapchain/Present、usage追加、device後補完、LOCAL group Presentを検証。
- `Doc/GraphicsHook_Vulkan_Performance_Next_Steps.md` — 第15節に修正根拠、対応範囲、検証を追記。
- `.agent/changes.md` — 本タスクの記録を追記。既存の調査エントリは保持。

### Behavioral Impact
- 単一GPUの後補完・複数queue family・単一GPU groupで不要にcapture/overlayを拒否しなくなる。
- 作成情報未捕捉かつ複数adapterがある場合は、先頭adapterを推測して利用せずスキップする。
- waitなし、複数GPU group、protected、swapchain作成情報不明は引き続き対象外。BGRA直接publishとV2契約は維持。

### Risk & Mitigation
- Risk: 所有権制約の変更でGPU同期が崩れる可能性。
- Mitigation: Vulkanの有効なPresentの所有権契約を根拠としてコメント/Docへ明記。同じqueue、全元wait、再signalの同期は維持し、複数familyの実GPU同期検証に合格した。
- Risk: 関数取得経由のhookが未対応APIを有効と見せたり、x86で未捕捉となる可能性。
- Mitigation: 両resolverで共通の振り分けとnull保持を試験。x86で無効化している直接export hookを増やしていない。

### Tests / Verification
- SDK G:/Development-Cache/VulkanSdk。x64 ReleaseのHookAgentVulkan/HookAgentVulkanTestsビルド成功。
- CTest VulkanReadback合格。既存のworker/色/寿命試験に加え、GIPA/GDPA両経路、後補完、複数family、単一GPU group、曖昧なadapterの拒否を確認。
- `HookAgentVulkanTests --present`合格。GTX 1080、device queue family数2、単一GPU group、72回の複数swapchain Present、実proc-address hook、device後補完、画素/usage/終了を確認。Validation Layers/synchronization validation error 0。
- x86ビルド・実行なし。アンチウイルス設定変更なし。対象端末のx86ゲームでの修正確認は未実施。
- `git diff --check`で空白エラーなし。

### Open Questions
- 対象x86ゲームで今回修正した条件が原因だったかは、別端末で再ビルドしたDLLによる実機確認が必要。

**2026-09-11 13:06 (Asia/Taipei) — Vulkan診断ログ設定のUIバインディングを修正**

### Summary
- 性能診断ログと診断ファイル出力のチェック状態が保存設定へ反映されない問題を修正した。

### Context / Goal
- UIで有効化しても再起動時にチェックが外れるとの報告に対応する。
- UIの参照名を既存ViewModelの保存・読み込み・自動保存処理へ正しく接続する。

### Changes
- Settings.GraphicsHookPerfDiagLogをSettings.EnableGraphicsHookPerfDiagLogへ修正。
- Settings.GraphicsHookDiagFileSinkをSettings.EnableGraphicsHookDiagFileSinkへ修正。

### Files Touched
- `UI/HookFullscreenControl.xaml` — 診断チェックボックス2か所のBinding Pathを修正。
- `.agent/changes.md` — 本タスクの記録を追記。

### Behavioral Impact
- UIの診断設定変更が既存の自動保存へ届き、保存された値が再起動後のチェック状態に反映される。
- 設定ファイルの項目名や既定値は変更していない。これまで保存されなかったチェックは修正版で設定し直す必要がある。

### Risk & Mitigation
- Risk: 保存プロパティ名とUIの参照名の不一致。
- Mitigation: ViewModelのプロパティ、保存・読み込み代入、自動保存通知、AppSettingsの名前が一致することを確認。

### Tests / Verification
- XAMLのXML構文確認に成功。
- `dotnet build Hotkey-Translator.csproj --no-restore -c Debug -p:BuildProjectReferences=false -v:q` — 成功、警告0・エラー0。
- `git diff --check` — 空白エラーなし。
- アプリを操作して設定変更・再起動する実機確認は未実施。native/x86のビルドは行っていない。

**2026-09-11 14:03 (Asia/Taipei) — Hook診断ファイル出力をUI設定へ統一**

### Summary
- Vulkan初期化ログとHookHostログを診断ファイル出力設定に接続し、OFF時のファイル作成・追記を停止した。

### Context / Goal
- 性能診断とファイル出力の両方をOFFにしても、Vulkan初期化ログとHostログが生成されていた。
- 起動直後、実行中の切り替え、終了処理まで設定を適用し、既存ログは削除せず保持する。

### Changes
- Host起動引数で最初のログより前にファイル出力方針を指定。引数なしではOFFとする。
- diagnosticsコマンドを追加し、ゲーム未接続・pipeline無効時も既存Hostへ設定を通知する。
- 設定適用時はdetachや接続条件判定より先に、接続中ゲームの共有設定とHost診断設定を更新する。
- Hostは共有設定の書き込み成功を確認してから注入する。Vulkanはinstall-threadの最初のログと再接続・終了時に設定を再読込する。
- Vulkanの設定未取得時は初期化を明示的に失敗させる。初期化後も設定済みFPS・overlayを保持する。
- ファイル出力の最終段で設定を検査し、OFFへの切り替えと書き込みを同じmutexで排他する。
- 性能診断の有効化とファイル出力の有効化は独立。性能ログのファイル保存には両方が必要。

### Files Touched
- `Services/Hook/GraphicsHookClientService.cs` — 起動引数、共有設定の先行更新、接続状態に依存しないHost診断通知。
- `Services/Hook/Contracts/GraphicsHookMessages.cs` — diagnosticsコマンドのpayload。
- `Native/HookHost/main.cpp` — 起動時・実行中のファイル出力制御、共有設定の注入前公開。
- `Native/HookAgentVulkan/VulkanPresentHook.cpp` — 全ファイル出力の設定検査、初期化前読込、OFF時の排他付きclose。
- `Native/HookAgentVulkan/VulkanPresentHook.h` — 初期化前に共有設定が必要である契約を明記。
- `Native/HookAgentVulkan/tests/VulkanReadbackTests.cpp` — 設定4通り、初期化ログ、OFF時の保持・並行書き込みの回帰試験。
- `Native/HookHost/tests/HookHostDiagnosticTests.cpp` — Host設定4通り、実行中切り替え、終了時の回帰試験。
- `Native/HookCommon/tests/DiagnosticTestFiles.h` — ゲームのログと混在しない試験出力先。
- `Native/HookHost/CMakeLists.txt` — Host診断試験を既存のテストオプションとCTestへ登録。
- `.agent/changes.md` — 本タスクの記録。

### Behavioral Impact
- ファイル出力OFFではHost/Vulkanの初期化ログも新規ファイルを作らず、既存ファイルにも追記しない。
- ONへの変更後から追記し、OFFではファイルを閉じる。既存ログを削除しない。
- OutputDebugStringのデバッガ向け出力は引き続き利用できる。
- アプリ、Host、Vulkan DLLを対応する修正版で使用する。設定ファイルの項目・共有設定の形式は変更なし。

### Risk & Mitigation
- Risk: 初期ログが共有設定の公開より先に出る、またはOFFと並行する書き込みがファイルを再度開く可能性。
- Mitigation: 注入前の設定公開、初期化入口での強制読込、最終出力段のmutex内検査で防止する。
- Risk: 診断通知が接続状態の表示を変更する可能性。
- Mitigation: diagnosticsは一方向通知とし、接続状態の応答を発行しない。

### Tests / Verification
- x64 ReleaseのHookHost、HookAgentVulkan、両テストターゲットをビルド成功。SDKはG:/Development-Cache/VulkanSdk。
- CTest: HookHostDiagnostics / VulkanReadbackの2件に合格。設定4通り、初期化前設定、実行中OFF、既存ファイル保持、並行書き込みを確認。
- 実Hostプロセスで起動時ON/OFF、pipe経由ON→OFF、OFFでのshutdown後に追記がないことを確認。
- ビルド済みVulkan DLLをx64の隔離した検証プロセスへロードし、実exportのInstallVulkanHookThreadでflags 0/1/2/3とOFF再適用を確認。OFFでのUninstall後も追記なし。
- 実GPU --present試験成功。72回の複数swapchain Present、capture/overlay、resize/detach、12回publish、同期validationエラーなし。
- WPF Debugビルド成功、警告0・エラー0。初回は既存の生成ファイル欠落で失敗したが、Rebuildで解消した。
- アプリ実行フォルダーのNativeはリポジトリNativeへのjunction。x64 Host/DLLのSHA-256一致により修正版反映を確認した。
- git diff --checkで空白エラーなし。x86ビルド・実行、アンチウイルス設定変更はしていない。
- ユーザーのゲームでUI操作を伴う最終確認は未実施。

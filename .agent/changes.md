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
- `git diff --check`（LF/CRLF warning のみ）

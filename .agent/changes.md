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

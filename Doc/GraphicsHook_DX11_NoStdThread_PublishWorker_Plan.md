# DX11 Graphics Hook `std::thread` 非依存 Publish Worker 実装案

1. **概要（1–3行）**
- 本計画は、DX11 Hook の off-Present publish を維持しつつ、DLL 内 runtime が `std::thread` を直接保持しない設計へ置き換える実装案である。
- 目的は、対象アプリ終了時の `DLL_PROCESS_DETACH(reserved != nullptr)` でも `std::thread` destructor 起因の `abort()` を起こさないことにある。
- 結論として、publish worker は `std::thread` ではなく Win32 thread handle または thread pool work item で管理するのが妥当である。

2. **ゴール / 非ゴール**
### ゴール
- DX11 off-Present publish の性能改善を維持したまま、process exit 時の `abort()` を解消する。
- `DllMain` の制約下でも destructor-safe な worker lifecycle にする。
- resize / detach / process exit の 3 終了パスを分けて扱えるようにする。

### 非ゴール
- DX11 publish worker 自体を撤廃して同期 publish に戻すこと。
- Vulkan / DX9 の同時修正。
- C# 側 consumer 契約の変更。

3. **現状問題**
- 現行 DX11 Hook runtime は `std::thread publishThread` を保持している。
- `UninstallPresentHook()` を通る通常 detach では `join()` できるが、対象アプリ終了時は `DllMain(DLL_PROCESS_DETACH, reserved != nullptr)` で `UninstallPresentHook()` が呼ばれない。
- このとき OS が worker thread を止めても `std::thread` オブジェクトは joinable のまま残る。
- DLL 静的オブジェクト破棄時に `std::thread` destructor が `std::terminate()` を呼ぶため、`abort()` が発生する。

4. **なぜ `detach` を常用しないか**
- `detach` は destructor の abort は避けられるが、worker が DLL 内コードや DLL 所有メモリを終了直前まで触る危険が残る。
- 特に inject DLL では unload / process exit 時のコード寿命が短く、`detach` は「見えなくなるだけ」で安全性の根本解決にならない。
- したがって、推奨は `std::thread` を持たない管理方式に寄せること。

5. **推奨アーキテクチャ**
### 5.1 第一候補: Win32 thread handle ベース
- runtime は `std::thread` ではなく以下を持つ:
  - `HANDLE publishThreadHandle`
  - `DWORD publishThreadId`
  - `HANDLE publishWakeEvent`
  - `HANDLE publishStopEvent`
- worker は `CreateThread` で起動し、entry point は static / free function にする。
- 通常 detach / resize では `SetEvent(stop)` + `WaitForSingleObject(thread)` で停止する。
- process exit では wait せず handle を閉じるだけでも `std::thread` destructor 問題は起きない。

### 5.2 第二候補: Win32 thread pool work item
- publish queue を `TP_WORK` で処理する。
- runtime は work item handle と event だけを保持する。
- thread 自体の所有を持たず、OS thread pool に publish を委譲する。
- ただし queue drain、再入、stop 制御がやや複雑になる。

### 5.3 推奨結論
- 初手は **Win32 thread handle ベース** を推奨する。
- WHY: 現行の queue / condition_variable モデルに最も近く、差分が小さく、終了時制御が明確だから。

6. **提案データ構造**
### 6.1 Runtime フィールド案
- `HANDLE publishThreadHandle = nullptr`
- `DWORD publishThreadId = 0`
- `HANDLE publishWakeEvent = nullptr`
- `HANDLE publishStopEvent = nullptr`
- `CRITICAL_SECTION publishQueueLock`
- `std::deque<Dx11PublishRequest> publishQueue`
- `std::vector<CaptureSlot*> publishCompleted`
- `LONG publishActiveCount = 0`
- `bool publishThreadStarted = false`

### 6.2 request / slot は現行流用
- `Dx11PublishRequest`
- `CaptureSlot`
- `CaptureSlotState::ReadyToPublish`
- `publishCompleted`

7. **スレッドモデル**
### 7.1 起動
1. `InstallPresentHook()` 内で `CreateEvent` / `InitializeCriticalSection`
2. `CreateThread` で publish worker 起動
3. 起動成功後に hook install 続行

### 7.2 通常処理
1. `Present` 側は `Map(DO_NOT_WAIT)` 成功後に request を queue へ積む
2. `SetEvent(publishWakeEvent)` で worker を起こす
3. worker は queue を処理して `publishCompleted` に完了 slot を返す
4. `Present` / reset 側 cleanup で `Unmap` して slot を `Free` に戻す

### 7.3 通常 detach
1. `SetEvent(publishStopEvent)`
2. `SetEvent(publishWakeEvent)`
3. `WaitForSingleObject(publishThreadHandle, timeout)`
4. handle close

### 7.4 process exit
- `DllMain(DLL_PROCESS_DETACH, reserved != nullptr)` では wait / join しない。
- 代わりに:
  - event / handle を閉じるだけ
  - queue / vector は OS process teardown に委ねる
- WHY: process exit で必要なのは graceful stop ではなく、abort しないことだから。

8. **実装詳細**
### 8.1 `std::condition_variable` の置き換え
- `publishCv` は不要
- wakeup は `publishWakeEvent`
- stop は `publishStopEvent`
- queue condition は `WaitForMultipleObjects([wake, stop])`

### 8.2 queue lock
- `std::mutex publishMutex` は `CRITICAL_SECTION` に置換推奨
- WHY: runtime が C++ RAII destructor に依存しすぎるのを避けるため

### 8.3 active count
- `std::uint32_t publishActiveCount` は `InterlockedIncrement/Decrement` で管理
- drain 判定は:
  - queue empty
  - `publishActiveCount == 0`

### 8.4 cleanup
- `CleanupCompletedCaptureSlotsLocked()` は維持
- `Unmap` は引き続き render-thread 側で行う
- WHY: immediate context を worker で触らない保守的設計を維持するため

9. **終了パス設計**
### 9.1 `UninstallPresentHook()`
- graceful stop を行う
- queue drain 後に thread wait
- 完了 slot cleanup 後に ring/device reset

### 9.2 `ResetDeviceStateLocked()`
- publish queue drain は行うが thread 自体は維持可能
- ただし pending publish が ring/resource を跨がないよう queue 完了待ちは必要

### 9.3 `DllMain(DLL_PROCESS_DETACH)`
- `reserved == nullptr`
  - 従来どおり `UninstallDx11Hook()` を呼ぶ
- `reserved != nullptr`
  - graceful stop はしない
  - `publishThreadHandle` があれば wait せず close
  - event handle も close
- NOTE: process exit 中は CRT destructor-safe を優先し、完全停止より abort 回避を優先する

10. **実装手順**
- Step 1: `Dx11Runtime` の `std::thread / mutex / condition_variable` を Win32 handle + lock へ置換
- Step 2: publish worker entry を `DWORD WINAPI PublishWorkerThread(void*)` に変更
- Step 3: enqueue / wakeup / drain / stop helper を Win32 API ベースへ置換
- Step 4: `DllMain(DLL_PROCESS_DETACH)` の process-exit 専用 cleanup を追加
- Step 5: install failure rollback と uninstall path を全件更新

11. **リスクと緩和策**
- Risk: process exit 時に wait しないため publish 中 frame が捨てられる
- Mitigation: process exit 中はどうせ target process 全体が終了するため、frame 完了保証は不要と割り切る

- Risk: `CRITICAL_SECTION` / handle 管理の漏れ
- Mitigation: helper 関数を `EnsurePublishThreadStarted`, `RequestPublishThreadStop`, `ClosePublishThreadHandles` に分割する

- Risk: thread handle ベースにしても stale slot cleanup が漏れる
- Mitigation: completed slot cleanup は `Present` / reset / uninstall の 3 箇所で必ず通す

12. **影響範囲**
- `Native/HookAgentDx11/Dx11PresentHook.cpp`
- `Native/HookAgentDx11/dllmain.cpp`
- `Doc/`

13. **Definition of Done**
- [ ] DX11 off-Present publish が維持されている
- [ ] `Dx11Runtime` が `std::thread` を保持していない
- [ ] process exit で `abort()` が出ない
- [ ] 通常 detach / resize / Alt+Tab で queue drain と cleanup が成立する
- [ ] x64/x86 の `HookAgentDx11` build が通る

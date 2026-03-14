# GraphicsHook PID / HWND 調査ガイド

`Doc\Get-TopLevelWindowMap.ps1` は、PowerShell だけで top-level window の `HWND / PID / ProcessName / Title / ClassName / Rect / MainWindowHandle` を確認するための補助スクリプトです。

`Get-Process` の `MainWindowHandle` だけでは拾えないケースがあるため、このスクリプトは `EnumWindows` で top-level window を直接列挙します。

## 使い方

リポジトリルートで実行します。

```powershell
powershell -ExecutionPolicy Bypass -File .\Doc\Get-TopLevelWindowMap.ps1
```

表示中の top-level window を一覧表示します。

## よく使う例

特定 PID の window だけ見る:

```powershell
powershell -ExecutionPolicy Bypass -File .\Doc\Get-TopLevelWindowMap.ps1 -ProcessId 48168
```

プロセス名で絞る:

```powershell
powershell -ExecutionPolicy Bypass -File .\Doc\Get-TopLevelWindowMap.ps1 -ProcessName SKShinoviVersus
```

タイトルの一部で絞る:

```powershell
powershell -ExecutionPolicy Bypass -File .\Doc\Get-TopLevelWindowMap.ps1 -TitleContains vkcube
```

非表示 window も含めて見る:

```powershell
powershell -ExecutionPolicy Bypass -File .\Doc\Get-TopLevelWindowMap.ps1 -ProcessName SKShinoviVersus -IncludeInvisible
```

## 見方

`HWND`
- 実ウィンドウのハンドルです。
- アプリログの `Fixed capture target resolved: hwnd=...` と比較します。

`PID`
- その window を所有するプロセス ID です。
- `launcher_bound pid=...` や `hook_state pid=...` と比較します。

`ProcessName`
- `Get-Process` で引いたプロセス名です。
- launcher が想定した exe 名と一致するか確認します。

`Title` / `ClassName`
- `WindowBindingService` の再解決条件を調べる材料です。
- ログで `class=(empty) title=(empty)` になっている場合、この列が重要です。

`MainWindowHandle`
- `Get-Process` が main window と見なしている HWND です。
- `HWND` と違う場合、main window 判定と top-level window 列挙の結果がズレています。

## 調査手順

1. launcher でゲームを起動する。
2. ゲーム画面が出たら、対象プロセス名または PID でこのスクリプトを実行する。
3. 出力された `PID` と `HWND` を、アプリログの `launcher_bound pid=...` と `hook_state pid=...` と比較する。

## 判定の目安

`launcher_bound pid` と実 window の `PID` が違う:
- launcher が bootstrap process を掴んでいて、最終 render process が別です。
- この場合は launcher の PID 決定ロジックを調べるべきです。

`PID` は同じだが `HWND` が取れているのにアプリログで `hwnd=0x0`:
- `WindowBindingService.TryResolveWindowHandle(...)` の条件で弾かれています。
- class/title/visibility/rect の条件を見直すべきです。

`PID` も `HWND` も一致しているのに GraphicsHook capture だけ失敗する:
- Window 解決ではなく hook runtime 側の問題です。
- `hook_vulkan_<pid>.log` や `hook_host_<pid>.log` を優先して確認します。

## 補足

このスクリプトは top-level window の調査用です。child window や swapchain の実体を直接追うものではありません。

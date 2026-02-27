# MagpieCore External Mirror Fullscreen + OCR/Overlay Translation Implementation Plan (Rollback Branch Baseline)

## 1. 概要（1-3行）
- 本ブランチには自前ミラーフルスクリーン実装が無いため、`Magpie.Core.exe` 外部連携を新規導入する。
- 目的は「Windowゲームをミラーフルスクリーン化」しつつ、既存 OCR/翻訳パイプラインを維持してオーバーレイ翻訳を表示すること。
- 連携方式は `Code-Reference/lunatranslator` の `Magpie_Core_CLI_Message_*` メッセージ制御を参考にする。

## 2. ゴール / 非ゴール
### ゴール
- `Ctrl+F7`（新規）でミラーフルスクリーン開始/停止をトグルできる。
- `F7` / `Shift+F7` の既存ロック/解除挙動は維持する。
- OCR入力は既存 `CaptureManager` を利用し、翻訳オーバーレイはミラー表示面へ座標変換して重畳する。
- DX11 Hook とミラーモードを排他にし、ミラーモード優先で正規化する。

### 非ゴール
- Magpie 本体の改造。
- 独占フルスクリーンすべてへの保証。
- 1stリリースでの高度なプロファイルUI（最初は固定プロファイルで可）。

## 3. 前提・仮定
- 現ブランチにはミラーモード設定・ミラーホットキー・ミラー表示サービスは存在しない（新規追加が必要）。
- `Tools/Magpie/` に `Magpie.Core.exe` と依存資産（DLL/effects/config）を配置して運用する。
- `Magpie.Core.exe` は下記I/Fを提供する前提。
  - Window class: `WNDCLS_Magpie_Core_CLI_Message`
  - Messages: `Magpie_Core_CLI_Message_Start`, `Magpie_Core_CLI_Message_Start_WindowedMode`, `Magpie_Core_CLI_Message_Stop`, `Magpie_Core_CLI_Message_Exit`
- UI排他は次の即時反映とする（確認ダイアログなし）。
  - `EnableMirrorFullscreenMode = true` にした時点で `EnableDx11HookPipeline = false` にする。
  - `EnableDx11HookPipeline = true` にした時点で `EnableMirrorFullscreenMode = false` にする。

## 4. 現状整理（現ブランチ）
- 既存ホットキー:
  - `F7`: 固定キャプチャ対象のロック
  - `Shift+F7`: 固定キャプチャ対象の解除
- 既存設定には `EnableDx11HookPipeline` はあるが、`EnableMirrorFullscreenMode` は無い。
- 既存オーバーレイは「元ゲームのスクリーン座標」を前提に描画されるため、Magpie拡大面にはそのまま一致しない。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `MagpieProcessService`（新規）
  - `Magpie.Core.exe` の起動・生存監視・終了。
- `MagpieIpcClient`（新規）
  - `FindWindow + RegisterWindowMessage + SendMessage` で Start/Stop/Exit を送信。
- `MagpieSessionController`（新規）
  - ホットキーと設定、固定対象ウィンドウ、Magpie制御のオーケストレーション。
- `MirrorOverlayMapper`（新規）
  - OCR/Overlay座標を「元Window座標 -> ミラー表示面座標」へ変換。

### データフロー / シーケンス
1. `Ctrl+F7` 押下。
2. 既存 `WindowBindingService` で対象 HWND を確定（既ロックがあれば再利用、未ロックならForegroundをロック）。
3. `MagpieProcessService` が `Magpie.Core.exe <config.json>` を起動。
4. `MagpieIpcClient` が Start メッセージ送信（`wParam=profileIndex`, `lParam=targetHwnd`）。
5. OCRは既存 `CaptureManager -> PipelineOrchestrator` を継続利用。
6. `MirrorOverlayMapper` が変換した矩形で翻訳オーバーレイを描画。
7. `Ctrl+F7` 再押下で Stop、アプリ終了時に Exit。

## 6. インターフェース設計
### 新規設定（追加）
- `EnableMirrorFullscreenMode: bool`（既定 `false`）
- `HotkeyToggleMirrorFullscreenKey: string`（既定 `F7`）
- `HotkeyToggleMirrorFullscreenModifiers: string`（既定 `Control`）
- （任意）`MagpieProfileIndex: int`（既定 `0`）
- （任意）`MagpieCorePath: string`（既定 `Tools\\Magpie\\Magpie.Core.exe`）

### 新規サービスI/F案
- `IMagpieProcessService`
  - `bool EnsureStarted(string configPath, out string? reason)`
  - `bool StopProcess(out string? reason)`
  - `bool IsRunning { get; }`
- `IMagpieIpcClient`
  - `bool TryStartScaling(long targetHwnd, int profileIndex, bool windowedMode, out string? reason)`
  - `bool TryStopScaling(out string? reason)`
  - `bool TryExit(out string? reason)`
  - `bool TryWaitCoreWindow(TimeSpan timeout, out nint coreHwnd)`
- `IMirrorOverlayMapper`
  - `bool TryMap(Rect sourceRect, out Rect mappedRect)`

### エラー方針
- 起動失敗: 実行ファイル不在 / 依存不足 / コアウィンドウ待機タイムアウト
- 送信失敗: メッセージID未取得 / `SendMessage` 異常
- 停止失敗: Stop失敗時はプロセスKillで回収

## 7. 実装手順（ステップ分割）
### Step 1: 実行資産検証
- `Tools/Magpie/` 配下の必須ファイルチェック（`Magpie.Core.exe` + 依存資産）。
- 単体起動テストで最低限起動することを確認。

### Step 2: Settings/VM/UI 追加（ミラー土台を新規導入）
- `AppSettings` / `SettingsViewModel` / `MainWindow.xaml` にミラー設定項目追加。
- `HotkeyDefaultsRule` / `SettingsValidator` にミラーホットキー既定値を追加。
- Hook排他ルール（Mirror優先）を新規実装。
- UI操作時も即時排他を適用する。
  - MirrorをONにしたらHookをOFFへ切り替える。
  - HookをONにしたらMirrorをOFFへ切り替える。

### Step 3: Hotkey追加（Ctrl+F7）
- `MainWindow` のホットキー登録/表示文言にミラートグル追加。
- `F7` / `Shift+F7` は既存どおり維持。

### Step 4: Magpieプロセス制御
- `MagpieProcessService` 実装。
- 起動時設定JSON生成・終了時クリーンアップ実装。

### Step 5: IPC連携
- `MagpieIpcClient` 実装。
- `Start/Stop/Exit` を送信し、ログ `stage=magpie_ipc` を追加。

### Step 6: オーバーレイ座標変換
- `OverlayPresenter` に矩形マッパー注入ポイント追加。
- ミラー有効中のみ mapper を有効化し、停止時に解除。

### Step 7: 安定化
- 対象Window消失時の自動停止。
- 起動失敗・送信失敗のユーザー向けログ整備。

## 8. 非機能要件チェック
- 性能: WPF自前ミラー方式より低遅延であること。
- 可観測性: `stage=magpie_process|magpie_ipc|magpie_session|overlay_map` ログ追加。
- 互換性: 既存ホットキー・OCR実行フローに回帰がないこと。
- 運用: 依存ファイル不足時に明確なエラーを出すこと。

## 9. リスクと緩和策
- Risk: Magpie配布物のバージョン差でメッセージI/F不一致。
- Mitigation: 起動直後に core window class / message 登録可否を検証し、失敗時は無効化ログを出す。

- Risk: 依存ファイル不足で Magpie が即終了。
- Mitigation: 起動前必須ファイルチェック + 起動後プロセス存活チェックを実装。

- Risk: 座標変換ずれ。
- Mitigation: デバッグ描画（元矩形/変換後矩形）トグルを追加。

## 10. 影響範囲
### 変更ファイル候補
- `Models/AppSettings.cs`
- `ViewModels/SettingsViewModel.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Services/Settings/Rules/HotkeyDefaultsRule.cs`
- `Services/Settings/AppSettingsValidator.cs`
- `Services/Settings/Rules/MirrorModeSettingsRule.cs`（新規）
- `Services/Application/MagpieProcessService.cs`（新規）
- `Services/Application/MagpieIpcClient.cs`（新規）
- `Services/Application/MagpieSessionController.cs`（新規）
- `Services/OverlayPresenter.cs`

### 実行資産
- `Tools/Magpie/Magpie.Core.exe`
- `Tools/Magpie/*`（依存DLL/effects/config）

### ドキュメント
- 本計画書
- 実装後に運用手順書（配置/起動確認/障害切り分け）を追記

## 11. Definition of Done
- [ ] 設定UIにミラーモード有効化とミラートグルホットキー設定が追加される。
- [ ] `Ctrl+F7` でミラー開始/停止ができる。
- [ ] `F7` / `Shift+F7` の既存ロック/解除が回帰しない。
- [ ] UIで Mirror/Hook の一方を有効化した時、もう一方が即時無効化される。
- [ ] ミラー中も OCR・翻訳処理が継続動作する。
- [ ] 翻訳オーバーレイがミラー表示面で座標一致する。
- [ ] アプリ終了時に Magpie プロセス残留がない。
- [ ] 起動不可/IPC不可/依存不足時に理由ログが出る。

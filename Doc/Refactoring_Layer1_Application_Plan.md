# Refactoring_Layer1_Application_Plan

1. **概要（1–3行）**
- 本計画は、`MainWindow.xaml.cs` に集中している Application 層責務を分離し、UI は「入力と表示」に専念させるための実装案である。
- 対象は層1（Application）に限定し、OCR/翻訳アルゴリズムやCapture実装の仕様変更は行わない。
- 後続の層2～5リファクタで再利用できる土台（Controller境界・状態コンテキスト）を先に確立する。

2. **ゴール / 非ゴール**
### ゴール
- `MainWindow` から以下の責務を分離する。
1. Run実行制御（排他・busy表示・payload再利用）
2. Scene-change watcher制御（A/B判定、pending-drain、streak）
3. Hotkey登録・更新制御
4. Settings正規化とUI反映の調停
5. ログバッファ制御
- 既存のユーザー体験（ホットキー、設定保存、Auto-hide/Auto-translate挙動）を維持する。
- フェーズ単位で安全に戻せる状態を維持する。

### 非ゴール
- `PipelineOrchestrator` の段階分解（層2で実施）。
- Capture provider ポリシー抽象化（層3で実施）。
- gRPC Host共通化（層4で実施）。
- `AppSettings` の大規模再構造化（層5で実施）。

3. **前提・仮定**
- `MainWindow.xaml.cs` は 3000 行超で、UI・制御・状態が混在している。
- 現在の実装には既に Scene semantic gate と payload 再利用が入り、状態管理がさらに複雑化している。
- 互換優先のため、初期段階では「ロジック移動（extract）」を中心にし、アルゴリズム変更は避ける。

4. **現状整理（層1対象）**
- UIイベント群
- 例: `OnRunOnce`, `OnSettingChanged`, `OnToggleOverlayHotkeyPressed`, 各Slider変更イベント
- Run制御群
- 例: `RunOnceAsync(...)`, `SetBusyOverlay`, `ShowLoadingSpinnerForRun`, `HideLoadingSpinnerForRun`
- Scene watcher群
- 例: `InitializeAutoHideWatcher`, `OnAutoHideTick`, `QueueSceneChangeAutoTranslate`, pending関連
- Settings調停群
- 例: `ApplySettingsToUi`, `SaveSettingsAsync`, `Normalize*Settings`, `Update*Controls`
- Hotkey群
- 例: `InitializeHotkeys`, `TryRegisterHotkeys`, `TryApplyHotkeyBinding`, `BuildHotkeyConfig`
- Logging群
- 例: `InitializeLogBuffer`, `OnLogFlushTick`, `AppendLog`, `UpdateLoggingState`

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成（層1）
- `MainWindow`（薄いView/Composition Root）
- 役割: DI配線、UIイベント受信、View更新API提供。

- `MainWindowRunCoordinator`（新規, Services/Application）
- 役割: Run排他、Busy表示制御、`PipelineOrchestrator` 呼び出し、scene payload再利用判定。

- `SceneChangeController`（新規, Services/Application）
- 役割: watcher lifecycle、Stage A/B判定、streak、pending-drain、Auto-hide/Auto-translateトリガ。

- `HotkeyController`（新規, Services/Application）
- 役割: hotkey 構築/再登録/衝突判定/解除。

- `SettingsUiController`（新規, Services/Application）
- 役割: settings の normalize・UI反映・UI入力からsettingsへの写像。

- `UiLogController`（新規, Services/Application）
- 役割: ログキュー、flushタイマ、最大行数制限。

### 5.2 データフロー（文章）
1. `MainWindow` がUIイベントを受ける。
2. 該当Controllerへ委譲する（`RunCoordinator` / `SceneChangeController` など）。
3. Controllerは必要に応じて `SettingsService` / `PipelineOrchestrator` / `OverlayPresenter` を利用。
4. View更新は `MainWindow` の最小API（例: `SetBusyOverlay`, `UpdateControls`）を経由する。

### 5.3 既存パターンとの整合
- 既存の `Services` 配下に置き、`AppLogger`・`SettingsService`・`PipelineOrchestrator` を再利用する。
- 既存の `Dispatcher` 利用方針を踏襲し、UIスレッド境界を明示化する。

6. **インターフェース設計**
### 6.1 最小インターフェース案
- `IMainWindowViewBridge`
- 目的: Controller からUI更新を行うための最小橋渡し。
- 例: `SetBusyOverlay(...)`, `AppendLog(...)`, `SetOverlayEnabled(...)`, `UpdateSceneChangeControls(...)`。

- `IRunCoordinator`
- `Task RunOnceAsync(ForceRunOptions options, SceneTextSnapshot? payload)`
- `bool IsRunning { get; }`

- `ISceneChangeController`
- `void Initialize(AppSettings settings)`
- `void UpdateWatcher(AppSettings settings)`
- `void OnOverlayShown()` / `OnOverlayHidden()` / `OnOverlayUpdated()`
- `void ResetState()`

- `ISettingsUiController`
- `bool Normalize(AppSettings settings)`
- `void ApplyToUi(AppSettings settings)`
- `Task SaveFromUiAsync()`

### 6.2 状態保持ポリシー
- UI固有状態（チェック状態、選択タブ）は `MainWindow` 側。
- 実行制御状態（run中、pending payload、semantic streak）は Controller 側。
- settings 正規化ルールは `SettingsUiController` に集約。

7. **実装手順（ステップ分割）**
- Step 1: Bridge導入（最小）
- `IMainWindowViewBridge` を追加し、`MainWindow` 自身が実装。
- この段階ではロジック移動しない。

- Step 2: Run制御を抽出
- `RunOnceAsync(...)` 系を `MainWindowRunCoordinator` へ移動。
- 既存メソッド名を `MainWindow` 側に薄い委譲として残し、互換維持。

- Step 3: Scene watcher制御を抽出
- `InitializeAutoHideWatcher`～`TryDrainPendingSceneChangeAutoTranslate` を `SceneChangeController` へ移動。
- watcher timer と semantic state を `SceneChangeController` の内部状態へ移管。

- Step 4: Hotkey制御を抽出
- `InitializeHotkeys` / `TryUpdateHotkeys` / `TryRegisterHotkeys` を `HotkeyController` へ移動。
- 既存HotkeyConfig生成は当初 `MainWindow` から供給し、後続でController側へ寄せる。

- Step 5: Settings調停を抽出
- `Normalize*Settings` と `ApplySettingsToUi` / `SaveSettingsAsync` の分解。
- 値計算とclampは `SettingsUiController`、WPF要素書き換えは `MainWindow` Bridge で実施。

- Step 6: Logging制御を抽出
- ログキュー/flush/最大行管理を `UiLogController` へ移動。

- Step 7: 旧フィールド削除と命名整理
- `MainWindow` から不要 private field を段階削除。
- 依存注入順・初期化順を固定し、コメント（WHY/COMPAT）を補完。

8. **非機能要件チェック**
- 性能
- `OnAutoHideTick` の実行時間を現行比で悪化させない。
- run開始～overlay更新の遅延を増やさない。

- 可観測性
- 既存ログ文言を可能な限り維持。
- Controller境界で `context` ログ（例: trigger/source）を追加。

- 互換性
- 既存ホットキー既定値・settings読み込み互換を維持。
- 既存 UI イベントハンドラ名を当面維持（XAML差分最小化）。

- 運用
- 各Stepで `dotnet build` 通過を必須とする。
- Step単位で revert 可能なコミット粒度を前提にする。

9. **リスクと緩和策**
- Risk: UIスレッド境界が崩れ、Dispatcher例外が発生。
- Mitigation: Bridge API で「UIスレッド必須」メソッドを明示し、Controller側は直接UI要素へ触れない。

- Risk: state 移管漏れで scene watcher が不安定化。
- Mitigation: watcher関連フィールドの移管チェックリストを作成し、段階移管ごとにログ検証する。

- Risk: 大規模差分でレビュー困難。
- Mitigation: StepごとにPR相当の小分割（責務1つずつ）で進める。

10. **影響範囲**
- 新規候補
- `Services/Application/MainWindowRunCoordinator.cs`
- `Services/Application/SceneChangeController.cs`
- `Services/Application/HotkeyController.cs`
- `Services/Application/SettingsUiController.cs`
- `Services/Application/UiLogController.cs`
- `Services/Application/IMainWindowViewBridge.cs`

- 既存更新
- `MainWindow.xaml.cs`（委譲中心へ縮小）
- `MainWindow.xaml`（必要最小限。基本は変更しない）

11. **Definition of Done**
- [ ] `MainWindow` が UIイベント受信 + Bridge 実装を主責務に限定されている。
- [ ] Run/Scene/Hotkey/Settings/Logging の各責務が専用Controllerへ分離されている。
- [ ] 既存シナリオ（F5/F8/F9/F10/F11/F6/F7、設定保存、auto-translate/auto-hide）が互換動作する。
- [ ] 既存設定ファイルで起動でき、互換性エラーがない。
- [ ] フェーズ完了時点で `MainWindow.xaml.cs` の行数とフィールド数が有意に減少している。
- [ ] 後続層（2～5）の実装案から、層1コンポーネント境界を再利用できる。

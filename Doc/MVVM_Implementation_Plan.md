# MVVM_Implementation_Plan

1. **概要（1–3行）**
- 本計画は、現在の Controller 分離済み構成（Run/Scene/Settings/Hotkey/Log）を土台に、WPF の MVVM へ段階移行する実装案である。
- 目的は `MainWindow.xaml.cs` を「View + Composition Root」に縮退させ、UI同期コードとイベント駆動ロジックを ViewModel/Service に再配置すること。
- 互換性を優先し、Big Bang 置換は行わず、機能単位の並行稼働で移行する。

2. **ゴール / 非ゴール**
### ゴール
- `Settings` UI の手動同期処理（Apply/Save/OnChanged）を Binding + ViewModel へ移行する。
- `ICommand`（`AsyncRelayCommand`）で UI イベントハンドラの大半を置換する。
- 設定保存を即時連打保存から `debounce` 制御へ移し、I/O負荷と不整合を抑える。
- `MainWindow.xaml.cs` を View 固有責務（Window ハンドル、Global Hotkey 入口、起動配線）へ限定する。

### 非ゴール
- OCR/翻訳アルゴリズムの仕様変更。
- gRPC host 基盤そのものの再設計（必要なら後続フェーズで別計画）。
- すべてのイベントを1回で MVVM 完全移行すること。

3. **前提・仮定**
- 既存で `MainWindowRunCoordinator` / `SceneChangeController` / `SettingsUiController` / `HotkeyController` / `UiLogController` は導入済み。
- 既存ユーザー体験（F5/F8/F9/F10/F11/F6/F7、settings.json 互換）を維持する必要がある。
- Global Hotkey は Window ハンドル依存のため、完全純粋 MVVM にはしない（View境界として明示維持）。

4. **現状整理**
- `MainWindow.xaml.cs` にまだ以下が残る。
- Settings UI 入出力マッピング（コントロール値⇔`AppSettings`）。
- 多数の `On...Changed` / `On...Click` イベントハンドラ。
- コントロール表示更新（`Update...Value`, `Update...Controls`）。
- これらは MVVM 化で削減可能だが、初期化順と UI スレッド境界を誤ると起動時例外が起こる。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `MainWindow`（View/Composition Root）
- `MainWindowViewModel`（画面全体の公開状態・コマンド）
- `SettingsViewModel`（設定編集状態、検証、保存トリガ）
- `RuntimeStatusViewModel`（稼働状態表示、ログ表示用状態）
- `ISettingsPersistence`（`SettingsService` ラッパ）
- `IResourceHostFacade`（既存 host 起動/停止 API の ViewModel 向け窓口）

### 5.2 データフロー
1. XAML は `MainWindowViewModel` / `SettingsViewModel` のプロパティへ TwoWay Binding。
2. UI操作は `ICommand` 実行。
3. `SettingsViewModel` は `debounce` 後に `ISettingsPersistence.SaveAsync()` を呼ぶ。
4. 保存成功後、必要な反映（host再評価、hotkey更新、watcher更新）を既存 Controller に委譲。

### 5.3 既存パターンとの整合
- 既存 Controller は維持し、ViewModel はそれらを呼ぶ調停層として追加する。
- View 依存処理（Global Hotkey の Window ハンドル関連）は `MainWindow` 側に残し、ViewModel は抽象化されたイベント通知のみ受ける。

6. **インターフェース設計**
### 6.1 主要インターフェース
- `IMainWindowViewModel`
  - `SettingsViewModel Settings { get; }`
  - `IAsyncRelayCommand RunOnceCommand { get; }`
  - `IAsyncRelayCommand SaveSettingsCommand { get; }`
- `ISettingsChangeScheduler`
  - `void RequestSave()`（debounce）
  - `Task FlushAsync()`
- `IResourceHostFacade`
  - `Task EnsureAsync(AppSettings settings)`
  - `Task RestartLlamaAsync()`

### 6.2 保存戦略
- `PropertyChanged` 直保存は禁止。
- `debounce`（例: 300–500ms）でまとめて保存。
- 明示操作（Run/Restart 直前）は `FlushAsync()` で強制反映。

7. **実装手順（ステップ分割）**
- Step 1: MVVM 基盤導入
- `CommunityToolkit.Mvvm` 導入、`MainWindowViewModel`/`SettingsViewModel` 雛形追加。

- Step 2: Settings の片方向移行
- まず読み込み反映を Binding 化（`ApplySettingsToUi` 削減）。

- Step 3: Settings の双方向移行
- `ApplyUiInputToSettings` を解体し、各プロパティを TwoWay Binding 化。
- 保存は `ISettingsChangeScheduler` 経由へ変更。

- Step 4: UIイベントを ICommand 化
- `OnRunOnce`, `OnSwapLanguages`, `OnTranslationPriorityUp/Down` などを `AsyncRelayCommand` へ置換。

- Step 5: 表示補助ロジックの XAML 化
- `Update...Value` 系を `StringFormat` / `Converter` へ移行。

- Step 6: Window 固有責務の明示固定
- `MainWindow` に残す責務を文書化（Global Hotkey登録、Windowライフサイクル、DataContext 配線のみ）。

- Step 7: 後片付け
- 旧イベントハンドラ/不要フィールドを削除し、`MainWindow.xaml.cs` を最小化。

8. **非機能要件チェック**
- 性能: 保存 debounce により設定連打時の I/O を削減。
- 互換性: 既存 `settings.json` キー互換を維持（既存 Normalizer を温存）。
- 可観測性: 保存キュー投入/flush/失敗をログで追跡可能にする。
- 運用: MVVM 未移行機能と共存可能な段階移行を維持。

9. **リスクと緩和策**
- Risk: ViewModel が巨大化し、`MainWindow` の肥大化を再現する。
- Mitigation: `SettingsViewModel` / `RuntimeStatusViewModel` へ分割し、責務境界を固定。

- Risk: `async void` 相当のコマンド例外が UI スレッドで未処理になる。
- Mitigation: `AsyncRelayCommand` + 集中例外ハンドラを採用。

- Risk: Hotkey の完全 MVVM 化を目指して Window 依存境界が壊れる。
- Mitigation: Hotkey は View 境界として残し、ViewModel へは抽象イベントだけ渡す。

- Risk: 設定保存のタイミング差で既存挙動が変わる。
- Mitigation: `FlushAsync()` を Run/Host操作前に明示呼び出し、互換シナリオを回帰確認する。

10. **影響範囲**
- 新規候補
- `ViewModels/MainWindowViewModel.cs`
- `ViewModels/SettingsViewModel.cs`
- `ViewModels/RuntimeStatusViewModel.cs`
- `Services/Application/SettingsChangeScheduler.cs`
- `Converters/*`（必要分のみ）

- 既存更新
- `MainWindow.xaml`
- `MainWindow.xaml.cs`
- `Services/Application/SettingsUiController.cs`（ViewModel 連携口へ調整）

11. **Definition of Done**
- [ ] Settings 主要項目が TwoWay Binding で同期され、`ApplyUiInputToSettings` 依存が解消されている。
- [ ] 主要操作が `ICommand` 化され、`MainWindow` のイベントハンドラ数が有意に減っている。
- [ ] 保存は debounce + flush 戦略で統一されている。
- [ ] Global Hotkey の責務境界（View 残置）が文書化されている。
- [ ] `dotnet build` / `dotnet run` が成功し、主要シナリオが互換動作する。
- [ ] `MainWindow.xaml.cs` は中間目標 500–900 行レンジへの縮小計画が確認できる。

### Migration（SHOULD）
- 旧 `On...Changed` ハンドラは、Binding 化完了単位で段階削除する。
- 旧 `ApplySettingsToUi` / `ApplyUiInputToSettings` は「並行稼働期間を経て」最終削除する。

### Open Questions（SHOULD）
- 保存 debounce 時間を 300ms / 500ms のどちらにするか。
- Translation priority 編集UIを ObservableCollection のまま維持するか、専用 ItemViewModel 化するか。

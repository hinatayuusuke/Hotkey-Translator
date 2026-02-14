# MainWindow View Boundary

## Purpose
- `MainWindow` は MVVM 移行後も、WPF の `Window` 実体に依存する処理の境界として維持する。

## Responsibilities Kept In View
- Global Hotkey の登録/解除（HWND が必要なため）
- `Window` のライフサイクル（`Loaded` / `Closed`）に伴う初期化と破棄
- Overlay 表示や MessageBox など、UI スレッド上の View 依存操作

## Responsibilities Moved Or Moving Out
- 設定変更時の保存トリガ（`debounce` は `SettingsChangeScheduler` に移管）
- 主要 UI 操作（ROI 選択、言語入れ替え、翻訳優先度変更、Runtime 操作）の `ICommand` 化
- スライダー値表示更新のコードビハインド依存を廃止し、XAML `Binding` + `StringFormat` へ移行
- Drawer のウィンドウ高さ自動調整は `DrawerLayoutController` へ移管
- OCR プレビューの拡大ウィンドウ管理は `PreviewZoomCoordinator` へ移管
- OCR プレビューの latest-only UI 反映は `PreviewFrameDispatcher` へ移管

## Boundary Rule
- `MainWindow` には「画面実体に依存する処理」だけを残し、ドメインロジックと設定永続化ロジックは Controller / Service / ViewModel 側へ寄せる。

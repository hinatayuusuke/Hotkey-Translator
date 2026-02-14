# MainWindow.xaml.cs Refactoring Round2 Plan

1. **概要（1–3行）**
- 本計画は、`MainWindow.xaml.cs` の再肥大化を抑えるため、UI境界を維持しつつ「画面固有ロジック」を段階抽出する実装案である。
- 目的は行数削減ではなく、変更衝突（Drawer/Preview/Hotkey/Overlay）を局所化して回帰リスクを下げること。
- 既存機能（Run, Hotkey, Drawer, OCR Preview, Overlay）は挙動互換を前提とする。

2. **ゴール / 非ゴール**
### ゴール
- `MainWindow` の責務を「Composition Root + Window Lifecycle + WPF境界」に再収束させる。
- 近年追加された `Drawerレイアウト制御` と `Preview拡大ウィンドウ制御` を専用コンポーネントへ分離する。
- 将来のUI追加時に、`MainWindow.xaml.cs` への直接追記を最小化する導線を作る。

### 非ゴール
- OCR/翻訳アルゴリズム変更。
- Host起動ポリシー変更。
- 完全な pure MVVM 化（HWND/Window依存の撤去）。

3. **前提・仮定**
- 既存の層1分離で `ResourceHost*` / `Hotkey*` / `SettingsChangeScheduler` は導入済み。
- ただし Drawer と Preview Zoom は `MainWindow.xaml.cs` に再集約され、画面ロジックの凝集度が落ちている。
- `MainWindow_View_Boundary` 方針（Window依存はView境界に残す）は維持する。

4. **現状整理**
- `MainWindow.xaml.cs` に以下が混在している。
- 画面ライフサイクル・DI組立・Hotkey入口
- Drawer開閉/自動リサイズ/行高連動
- OCRプレビュー最新保持キュー + 表示反映
- Preview拡大ウィンドウの生成/再利用/同期
- 問題: 機能追加のたびに同一ファイルで衝突し、局所修正が全体影響へ波及しやすい。

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- `MainWindow`（維持）
  - Composition Root
  - Window lifecycle
  - WPFイベント入口（最小）
- `DrawerLayoutController`（新規, `Services/Application`）
  - `IsBottomPanelOpen` 監視
  - Window高さ自動拡張/復元
  - Drawer行高の強制反映（Splitter競合対策）
- `PreviewZoomCoordinator`（新規, `Services/Application`）
  - 拡大Window単一インスタンス管理
  - 画像同期（メインプレビュー -> 拡大ウィンドウ）
- `PreviewFrameDispatcher`（新規 or MainWindow内抽出）
  - 最新フレーム保持・UI反映スケジューリング

### 5.2 データフロー / シーケンス
1. `Pipeline` が preview bitmap を発火。
2. `PreviewFrameDispatcher` が最新のみ保持してUIへ反映。
3. 反映後、`PreviewZoomCoordinator` へ同一 `ImageSource` を通知。
4. `BottomPreviewPaneVisible/IsBottomPanelOpen` 変化を `DrawerLayoutController` が処理。
5. `MainWindow` はイベント購読と橋渡しのみ担当。

### 5.3 既存パターンへの整合
- 既存 `*Controller` 抽出方針を踏襲し、UI固有の状態制御だけを新Controller化する。
- `MainWindow` には HWND/Dispatcher 依存の最終境界のみ残す。

6. **インターフェース設計**
### 6.1 Drawer制御
- `IDrawerLayoutController`
  - `void Attach(MainWindowViewModel viewModel)`
  - `void Detach()`
  - `void OnLoaded()`
- 入力
  - `IsBottomPanelOpen`, `WindowState`, 現在Windowサイズ, Drawer表示実測高さ
- 出力
  - `Height/Top` 更新、Drawer行 `GridLength` 更新

### 6.2 Preview Zoom制御
- `IPreviewZoomCoordinator`
  - `void ShowOrActivate(ImageSource? source)`
  - `void UpdateImage(ImageSource? source)`
  - `void Dispose()`
- ルール
  - 単一インスタンス運用
  - Close時ハンドラ解除を必須化

### 6.3 MainWindow境界（最終形）
- `MainWindow` に残す公開/内部責務
  - `OnLoaded/OnClosed`
  - `OnOcrPreviewClicked`
  - UI bridge 実装
- 残さない責務
  - Drawerサイズ計算の詳細
  - Zoom Window管理の詳細

7. **実装手順（ステップ分割）**
- Step 1: 挙動変更なしの partial 分割
  - `MainWindow.xaml.cs` を以下へ分離。
  - `MainWindow.Lifecycle.cs`
  - `MainWindow.Preview.cs`
  - `MainWindow.Drawer.cs`
  - `MainWindow.HotkeyBridge.cs`

- Step 2: Drawer制御抽出
  - `SyncWindowSizeForBottomDrawer` 系メソッドを `DrawerLayoutController` へ移送。
  - `RowDefinition` 名参照で開閉時に明示的 `GridLength` を設定（バインディング競合回避）。

- Step 3: Preview Zoom抽出
  - `_ocrPreviewZoomWindow` 管理と `Show/Update/Close` を `PreviewZoomCoordinator` へ移送。

- Step 4: Preview frame dispatch整理
  - `latest-only` キュー処理を `PreviewFrameDispatcher` へ抽出し、MainWindowは通知のみ。

- Step 5: 境界ドキュメント更新
  - `Doc/MainWindow_View_Boundary.md` を更新し、残置責務/禁止事項を再定義。

- Step 6: 回帰確認
  - `dotnet build` / `dotnet run`
  - Previewクリック拡大、ホイールズーム、Drawer開閉、Run/Hotkey一連導線。

8. **非機能要件チェック**
- 保守性
  - 変更点の局所化（1機能1ファイル）
- 互換性
  - 既存 settings キーと動作を維持
- 可観測性
  - Drawer open/close と auto-resize 判定ログを debug レベルで追加可能にする
- 安全性
  - Window close 時のイベント解除を標準手順化

9. **リスクと緩和策**
- Risk: 分割時のイベント解除漏れでメモリリークや二重反応が起きる。
- Mitigation: `Attach/Detach/Dispose` 契約を統一し、`OnClosed` で一括解放を固定。

- Risk: Drawer制御抽出で既存の手動リサイズ尊重ロジックが壊れる。
- Mitigation: 既存条件（自動加算分のみ復元、最大化時スキップ）をテスト観点として固定。

- Risk: partial分割だけで実質改善が出ない。
- Mitigation: Step 2/3 までを1セット完了条件にし、分割のみで終わらせない。

10. **影響範囲**
- `MainWindow.xaml.cs`（分割・委譲）
- `MainWindow.xaml`（必要最小限: 名前付与/イベント接続の整理）
- `Services/Application/DrawerLayoutController.cs`（新規）
- `Services/Application/PreviewZoomCoordinator.cs`（新規）
- `Services/Application/PreviewFrameDispatcher.cs`（新規候補）
- `Doc/MainWindow_View_Boundary.md`（更新）

11. **Definition of Done**
- [ ] `MainWindow.xaml.cs` 本体が「境界責務」に限定されている。
- [ ] Drawer制御詳細が `DrawerLayoutController` に移管されている。
- [ ] Preview拡大Window制御が `PreviewZoomCoordinator` に移管されている。
- [ ] Preview latest-only 反映が単一責務として分離されている。
- [ ] `dotnet build Hotkey-Translator.csproj -p:UseAppHost=false` が成功する。
- [ ] 主要手動導線（Run/Hotkey/Drawer/PreviewZoom）に回帰がない。

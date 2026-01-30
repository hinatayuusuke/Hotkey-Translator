# Global Loading Indicator Window Plan

1. **概要（1–3行）**
- 画面右下に小さなTopmostウィンドウを新設し、OCR/翻訳のローディング表示を行う。
- 表示位置は「アクティブな表示領域（ROI/キャプチャ対象）」に合わせる。

2. **ゴール / 非ゴール**
- ゴール: MainWindowに依存せず、画面右下に小型表示を出す。
- ゴール: OCR/翻訳中のみ表示し、短時間処理では表示しない。
- 非ゴール: 既存のOverlayWindowのデザイン刷新。
- 非ゴール: マルチモニタ全体の統一通知UI。

3. **前提・仮定**
- 既存のCaptureManager/ROI情報で対象領域を取得できる。
- 右下座標は「ROI/キャプチャ対象のRect」から算出する。

4. **現状整理**
- BusyOverlayはMainWindow内にあり、アプリ中央に表示される。
- OCR/翻訳の開始・終了イベントはMainWindowで制御している。

5. **提案アーキテクチャ**

   * コンポーネント構成
   - 新規UI: `UI/LoadingIndicatorWindow.xaml(.cs)`
   - 表示制御: MainWindow から表示/非表示を操作

   * データフロー / シーケンス（文章で可）
   - OCR開始 → 200ms遅延 → 表示
   - OCR終了 → 非表示
   - 翻訳開始 → 200ms遅延 → 表示
   - 翻訳終了 → 非表示 or OCR表示へ戻す
   - 表示位置は現在のROI/キャプチャ対象の右下に再配置

   * 既存パターンへの整合
   - 既存のBusyOverlayロジックを差し替え/再利用

6. **インターフェース設計**

   * API / 関数 / イベント
   - `LoadingIndicatorWindow.Show(string message)`
   - `LoadingIndicatorWindow.Hide()`
   - `LoadingIndicatorWindow.UpdatePosition(Rect targetBounds)`

   * 入出力、エラー、バリデーション
   - targetBounds が空の場合はスクリーン右下（仮）にフォールバック

7. **実装手順（ステップ分割）**

   * Step 1…
   - `LoadingIndicatorWindow` を新規作成
   - Topmost/透過/クリック透過を設定

   * Step 2…
   - MainWindowのBusyOverlayを削除または無効化
   - OCR/翻訳イベントで新しいウィンドウを制御

   * Step 3…
   - ROI/キャプチャ対象のRectから右下位置を算出
   - DPI補正を考慮し、画面内にクランプ

8. **非機能要件チェック**

   * 性能: 軽量ウィンドウで負荷は低い
   * 可観測性: ログ不要
   * 互換性: UI変更のみ

9. **リスクと緩和策**
- Risk: ROIが極端に小さい場合、表示位置がずれる。
- Mitigation: 最小マージンを確保して右下に固定。

10. **影響範囲**
- 変更ファイル候補
  - UI/LoadingIndicatorWindow.xaml
  - UI/LoadingIndicatorWindow.xaml.cs
  - MainWindow.xaml / MainWindow.xaml.cs
- ドキュメント更新: 本ファイル

11. **Definition of Done**
- ローディング表示がMainWindow中央ではなく、ROI右下に表示される
- OCR/翻訳の短時間処理では表示されない
- OCR/翻訳完了後に確実に非表示になる

# OCR未検出トースト表示（フォーカスウィンドウ右下）実装案

1. **概要（1–3行）**
OCRで文字が1行も検出されなかった場合、フォーカスウィンドウ右下に短時間の通知を表示する。
既存オーバーレイは消し、ユーザーに「検出なし」を明示する。

2. **ゴール / 非ゴール**
- ゴール: OCR結果0行時に小さな英語通知（"No text detected"）が右下に出る。
- ゴール: OCR結果0行時は前回オーバーレイを表示しない。
- ゴール: 通知は短時間で自動消滅する。
- 非ゴール: 通知内容や表示時間の設定UI追加。
- 非ゴール: OCRロジック自体の改善。

3. **前提・仮定**
- 画面の基準矩形は `CaptureManager.GetCaptureBounds` か `frame.Bounds` を利用可能。
- F9でオーバーレイを非表示にしても、通知は表示する。

4. **現状整理**
- OCR結果0行のケースでは `ShowLast()` で前回オーバーレイを維持して終了している。
- ユーザーへのフィードバックが無い。

5. **提案アーキテクチャ**
- `OverlayWindow` に「通知専用Canvas」または「通知用Border」を追加。
- `OverlayPresenter` に `ShowToast(string text, Rect anchor)` を追加し、位置と表示時間を制御。
- OCR結果0行時はオーバーレイをクリアし、通知のみ表示。

6. **インターフェース設計**
- `OverlayWindow.ShowToast(string text, Rect anchor)` を追加。
  - `anchor` はフォーカスウィンドウ矩形（スクリーン座標）。
  - 右下基準で配置する。
  - 表示時間は定数（例: 1200ms）。
- `OverlayPresenter` から UI スレッドで呼ぶ。
- `PipelineOrchestrator` の「OCR結果0行」分岐で呼ぶ。
- `OverlayPresenter.ClearOverlay()` などでオーバーレイを消す。

7. **実装手順（ステップ分割）**
1. `OverlayWindow.xaml` に通知用の `Canvas` か `Border` を追加（IsHitTestVisible=false）。
2. `OverlayWindow` に `ShowToast` を実装し、一定時間後に非表示化。
3. `OverlayPresenter` に `ShowToast` と `ClearOverlay` ラッパーを追加。
4. `PipelineOrchestrator` の「OCR結果0行」ケースで `ClearOverlay` + `ShowToast` を呼ぶ。

8. **非機能要件チェック**
- 性能: 軽量なUI更新のみ。
- セキュリティ: 変更なし。
- 可観測性: 追加ログは不要（必要なら既存ログに1行追加）。
- 互換性: 既存オーバーレイ表示には影響しない。

9. **リスクと緩和策**
- リスク: OCR連続失敗時に通知が連打される。
- Mitigation: 連続表示を抑制する簡易レート制限（最短間隔）。

10. **影響範囲**
- `UI/OverlayWindow.xaml` / `UI/OverlayWindow.xaml.cs` — 通知UI追加と表示制御。
- `Services/OverlayPresenter.cs` — トースト表示API追加とオーバーレイクリア。
- `Services/PipelineOrchestrator.cs` — OCR結果0行時に通知＋オーバーレイクリア。

11. **Definition of Done**
- [ ] OCR結果0行時に「No text detected」が右下に表示される。
- [ ] 通知は自動消滅する。
- [ ] OCR結果0行時は前回オーバーレイが表示されない。

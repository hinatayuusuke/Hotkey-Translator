# F9 を非表示切替（透明）にする実装案

1. **概要（1–3行）**
ウィンドウの Hide/Show を使わず、オーバーレイを常時表示したまま F9 で透明切替する。
F8/F10 実行時は非表示状態でも必ず再表示（透明解除）して OCR 結果を見せる。

2. **ゴール / 非ゴール**
- ゴール: F9非表示→OCR実行時の旧フレームフラッシュを無くす。
- ゴール: F8/F10 実行時は非表示でも必ず表示状態に戻す。
- 非ゴール: 既存のホットキー割当変更。
- 非ゴール: キャプチャ方式やOCRパイプラインの変更。

3. **前提・仮定**
- `OverlayWindow` は常時表示でもクリック透過かつキャプチャ除外で問題ない。
- `OverlayPresenter.Update()` は現在ロールバック済み（既存のまま）。

4. **現状整理**
- F9 は `OverlayPresenter.SetEnabled(false)` で `Hide()` を呼び、Window を非表示にしている。
- `Hide/Show` による DWM 旧フレームの表示がフラッシュの原因。

5. **提案アーキテクチャ**
- `OverlayPresenter` に「表示状態（可視/不可視）」を Window ではなく Canvas で制御させる。
- Window 自体は起動時に一度だけ `Show()` し、その後は常時表示。

6. **インターフェース設計**
- `OverlayPresenter.SetEnabled(bool enabled)` は Window を Hide/Show せず、
  `OverlayWindow` に対して「Canvas表示切替」を指示する API に変更。
- `OverlayWindow` に `SetOverlayVisibility(bool visible)` を追加し、
  `OverlayCanvas.Visibility` または `Opacity` を切り替える。
- F8/F10 の `EnableOverlay()` は `SetEnabled(true)` を呼び、必ず再表示。

7. **実装手順（ステップ分割）**
1. `OverlayWindow` に `SetOverlayVisibility(bool visible)` を追加し、Canvas表示を制御。
2. `OverlayPresenter.SetEnabled` の `Hide/Show` 呼び出しを削除し、
   `OverlayWindow.SetOverlayVisibility` を呼ぶように変更。
3. `OverlayPresenter.Show()` は初期表示時のみ利用（起動時に一度呼ぶ）。
4. F9 トグルは `SetEnabled` に委譲したまま運用。
5. F8/F10 の `EnableOverlay()` はそのまま `SetEnabled(true)` で再表示されることを確認。

8. **非機能要件チェック**
- 性能: Window 常時表示だが Canvas を隠すだけなので負荷は軽微。
- セキュリティ: 変更なし。
- 可観測性: 追加ログ不要。
- 互換性: キー割当・設定は維持。

9. **リスクと緩和策**
- リスク: 透明化しても合成負荷がわずかに増える可能性。
- Mitigation: 問題が出た場合は Canvas を `Collapsed` にして描画コストを下げる。

10. **影響範囲**
- `UI/OverlayWindow.xaml.cs` — Canvas表示切替 API 追加。
- `Services/OverlayPresenter.cs` — Hide/Show から Canvas切替へ変更。

11. **Definition of Done**
- [ ] F9 で非表示にしても Window は消えず、オーバーレイだけが消える。
- [ ] F9 非表示後に F8/F10 実行すると必ず表示状態に戻る。
- [ ] OCR 実行時のフラッシュが発生しない。

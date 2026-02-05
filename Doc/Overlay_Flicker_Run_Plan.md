# OCR 実行時のオーバーレイちらつき修正案

1. **概要（1–3行）**
OCR 実行時に発生する「オーバーレイが一瞬消えて戻る」ちらつきを解消する。
RunOnce 中の Hide→Show を撤廃し、Update で内容を差し替える方針に統一する。

2. **ゴール / 非ゴール**
- ゴール: B画像 OCR 実行時に Aオーバーレイが消えて戻る“瞬断”を無くす。
- 非ゴール: F9 トグル挙動の変更（現状ちらつき無し）。
- 非ゴール: 画面キャプチャ経路の変更（WGC/DXGI/GDI自体の入替）。
- 非ゴール: 新たなUI設定項目の追加。

3. **前提・仮定**
- オーバーレイは `OverlayWindow` 側で `SetWindowDisplayAffinity` によりキャプチャ除外されている。
- Run 中の Hide/Show を撤廃すると「Aが一瞬消える」から「AがB更新まで残る」にUXが変化する。

4. **現状整理**
- `PipelineOrchestrator.RunOnceAsync` の冒頭で `_overlayPresenter.Hide()`、`finally` で `_overlayPresenter.Show()` を実行している。
  これが A→(消える)→A→B の“瞬断”を作っている。

5. **提案アーキテクチャ**
- **Run 中の Hide/Show を撤廃**: オーバーレイは表示を維持し、`Update` のみで内容を差し替える。

6. **インターフェース設計**
- 変更は `PipelineOrchestrator` 内に限定し、`OverlayPresenter` / F9 の挙動は変更しない。

7. **実装手順（ステップ分割）**
1. `PipelineOrchestrator.RunOnceAsync` の `_overlayPresenter.Hide()` / `Show()` を削除。
2. 通常の `Update` フローで新しい結果が描画されることを確認。

8. **非機能要件チェック**
- 性能: Hide/Show を除去するためむしろ安定。
- セキュリティ: 変更なし。
- 可観測性: 追加ログ不要。
- 互換性: F9/F8/F10/F11 のキー割当は維持。

9. **リスクと緩和策**


10. **影響範囲**
- `Services/PipelineOrchestrator.cs` — Run 中の Hide/Show を撤廃。

11. **Definition of Done**
- [ ] B画像のOCR実行中にオーバーレイが消えて戻る現象が発生しない。
- [ ] 通常の F8/F10 実行でオーバーレイ更新が行われる。

# PaddleOCR Preprocessor Padding + Safe Coordinate Clamp Plan

1. **概要（1–3行）**
- 画像端の文字欠け対策として、推論前にPaddingを追加し、検出座標を補正・クランプする。
- Padding色は背景に近い色を自動推定し、白背景/黒文字のケースでも安定させる。

2. **ゴール / 非ゴール**
- ゴール: 左端欠けなどの検出漏れを改善する。
- ゴール: 座標補正後に負値が残らないよう安全にクランプする。
- 非ゴール: OCRモデル自体の変更や学習。

3. **前提・仮定**
- PaddleOCR推論は `ocr_engine.py` の `recognize()` 内で行われる。
- 検出結果は `[x, y, w, h]` 形式で返却される。

4. **現状整理**
- 画像端の文字で `x=0` 付近のボックスが多く、先頭文字が欠けやすい。
- Paddingは未実装。

5. **提案アーキテクチャ**

   * コンポーネント構成
   - `OcrService/ocr_engine.py` にPadding処理と座標補正を追加。

   * データフロー / シーケンス
   - 画像読み込み → 背景色推定 → Padding追加 → OCR推論
   - 推論結果の box 座標を padding 分だけ戻す
   - 負値を 0 にクランプ

   * 既存パターンへの整合
   - 既存の `recognize()` 処理に最小限の追加。

6. **インターフェース設計**

   * パラメータ
   - `padding_px`（例: 20）
   - `padding_color`（画像の四辺平均色）

7. **実装手順（ステップ分割）**

   * Step 1…
   - 画像端の平均色を計算して padding 色に利用

   * Step 2…
   - `ImageOps.expand` で padding を追加

   * Step 3…
   - 検出結果の `box` を `[x-padding, y-padding, w, h]` に補正
   - `x` / `y` が 0 未満の場合は 0 にクランプ

8. **非機能要件チェック**

   * 性能: 小さな画像拡張のみ、影響は軽微
   * 可観測性: 必要ならログで padding 適用を確認

9. **リスクと緩和策**
- Risk: Padding色の推定が不適切だと誤検出が増える
- Mitigation: 四辺の平均色を使用し、極端な色を避ける

10. **影響範囲**
- 変更ファイル候補
  - `OcrService/ocr_engine.py`

11. **Definition of Done**
- 左端欠けが軽減される
- 座標補正後に負値が発生しない
- OCR結果のボックスが元画像座標に一致する

# PaddleOCR-VL Layout Detection Parser 実装案

1. **概要（1–3行）**
- `--use-layout-detection` 有効時に、OCR結果へ `"<div ...><img ...>"` のようなレイアウト由来文字列が混入し、オーバーレイ表示を汚染する問題を修正する。
- 対策は「抽出対象の厳格化（text系ブロック優先）」と「HTML/画像参照断片の除外」の2段で行う。
- 既存の座標復元ロジックは維持し、今回の主眼は“テキスト選別パーサー”の改善に限定する。

2. **ゴール / 非ゴール**
### ゴール
- `OcrServiceVL/ocr_vl_engine.py` のパーサーで、layout検出結果から自然言語テキストのみを高優先で抽出できるようにする。
- `img_in_image_box_*` や `<img ...>` のような非テキスト断片をOCRラインとして返さない。
- 既存JSON契約（`lines[].text/box/confidence`）を維持する。

### 非ゴール
- モデル推論設定（`max_pixels`, `precision`, `use_tensorrt` 等）の最適化。
- Overlay側レンダラー仕様の変更。
- OCR後段（翻訳・差分・マージ）のアルゴリズム変更。

3. **前提・仮定**
- 問題は主に `use_layout_detection=True` 時の出力形式差分に起因する。
- レイアウト出力には text 以外（image/table/formula 相当）が混在し、現行パーサーは十分に弁別していない。
- 返却座標は最終的に `[x,y,w,h]` の矩形へ正規化可能である。

4. **現状整理**
- `OcrServiceVL/ocr_vl_engine.py` は複数の出力形式を吸収しているが、`parsing_res_list` 由来の `block_content` を広く採用している。
- そのため、テキスト以外のブロック内容（HTML断片や画像参照）が `lines[].text` に混入するケースがある。
- 混入時は overlay 上で「添付メタ文字列」が前面に出て、実用上の可読性を下げる。

5. **提案アーキテクチャ**
### コンポーネント構成
- `ocr_vl_engine.py` 内に「ブロック選別フィルター層」を追加。
- 既存の座標正規化関数（bbox/polygon→ltrbwh）は流用。

### データフロー / シーケンス
1. 推論結果から `parsing_res_list` を走査。
2. 各ブロックで `block_label` / `block_content` / `block_bbox` を取得。
3. `block_label` が text系で、かつ `block_content` がHTML/画像参照パターン非該当のものだけ採用。
4. 採用ブロックを `lines` へ変換し返却。
5. text系が0件のときのみ、既存fallback（markdown/plain）を限定的に使用。

### 既存パターンへの整合
- 出力JSONのスキーマは現行維持。
- ログは既存の `[PaddleVlGrpc]` 系へ追加し、解析しやすさを優先。

6. **インターフェース設計**
- 追加関数（案）
  - `_is_text_like_block(label: str | None) -> bool`
  - `_looks_like_markup_or_asset_ref(text: str) -> bool`
  - `_sanitize_block_text(text: str) -> str`
- 判定規則（初期案）
  - allow: `text`, `title`, `paragraph`（実際のlabel集合をログ確認して調整）
  - reject: `<img`, `<div`, `img_in_image_box_`, `![`, `</` を含む短文/断片
- バリデーション
  - 空文字、極端な短文（例: 記号のみ）は除外。
  - bbox欠損時は採用しない（0,0,1,1 フォールバック乱用を避ける）。

7. **実装手順（ステップ分割）**
- Step 1: ラベル観測ログ追加
  - `parsing_res_list` から `block_label` の分布を debugログに出す。
- Step 2: text系 allow-list 導入
  - text以外ブロックの採用を停止。
- Step 3: マークアップ/画像参照除外
  - `block_content` に対して reject パターンを適用。
- Step 4: fallback条件の厳格化
  - text系抽出が0件の場合のみ markdown/plain fallback を許可。
- Step 5: テスト画像検証
  - `OcrServiceVL/.testpic/test4.png` で添付文字列が返らないことを確認。

8. **非機能要件チェック**
- 性能
  - 文字列判定の追加のみで、推論時間への影響は軽微。
- 可観測性
  - `accepted_blocks`, `rejected_blocks`, `reject_reason` を debugログ化。
- 互換性
  - gRPC契約は不変。WPF側改修なしで適用可能。
- 運用
  - rejectパターンは定数化し、将来の拡張を容易にする。

9. **リスクと緩和策**
- Risk: allow-list が厳しすぎて正当テキストを落とす。
- Mitigation: Step1で実ラベル分布を取得し、段階的に調整する。
- Risk: HTML除外ルールが過剰で通常英文を誤除外する。
- Mitigation: rejectは複合条件（タグ+資産参照語）で判定し、単純な `<` だけでは弾かない。
- Risk: fallback縮退で0件応答が増える。
- Mitigation: text系0件時のみに限定fallbackを残し、完全無出力を避ける。

10. **影響範囲**
- `OcrServiceVL/ocr_vl_engine.py` — パーサー選別ロジック本体。
- `OcrServiceVL/test_ocr_vl_engine.py`（必要時）— 判定切替用オプション/デバッグ確認。
- `Doc/` — 必要なら運用ガイドに reject規則を追記。

11. **Definition of Done**
- [ ] `--use-layout-detection` + `test4.png` で `<img...>` 系文字列が `lines[].text` に含まれない。
- [ ] 主要テキストは従来どおり抽出される（過度な欠落がない）。
- [ ] `dotnet build Hotkey-Translator.csproj` に回帰がない（契約互換確認）。
- [ ] 追加ログで採用/除外の理由が追跡できる。

# Paddle OCR Recognition Model UI Plan

1. **概要（1–3行）**
- Paddle OCR設定UIに「認識モデル（text_recognition_model_name）」の選択項目を追加する。
- 既存のWinRT/翻訳/キャッシュには影響させず、Paddle OCRのみで完結させる。

2. **ゴール / 非ゴール**
- ゴール: Paddle OCRの認識モデルをUIで選択できる。
- ゴール: 影響範囲をPaddle OCR関連に限定する。
- 非ゴール: 自動言語判定やWinRT側の変更。
- 非ゴール: 翻訳言語やキャッシュキーの仕様変更。

3. **前提・仮定**
- 現状のコード（修正前）を前提とする。
- Paddle OCR側で `text_recognition_model_name` を反映できる（`ocr_engine.py` 側）。

4. **現状整理**
- Paddle OCR設定UIには検出モデルの選択はあるが、認識モデルは固定。
- `ocr_engine.py` で `text_recognition_model_name` は固定値になっている。

5. **提案アーキテクチャ**

   * コンポーネント構成
   - UI: Paddle OCR設定に「Recognition model」選択を追加
   - Settings: Paddle OCR認識モデル用の設定値を追加
   - Python: `ocr_engine.py` に設定反映

   * データフロー / シーケンス
   - UIで認識モデルを選択
   - Settingsに保存
   - Paddle OCR起動時（gRPC）に引数として渡す or `ocr_engine.py` 内で反映

   * 既存パターンへの整合
   - 既存のPaddle OCR設定保存/反映パターンに合わせる

6. **インターフェース設計**

   * UI
   - ComboBox: Recognition model
   - 選択肢例:
     - en_PP-OCRv5_mobile_rec
     - latin_PP-OCRv5_mobile_rec
     - eslav_PP-OCRv5_mobile_rec
     - PP-OCRv5_server_rec
     - （必要なら他の言語モデルも追加）

   * Settings
   - `PaddleTextRecognitionModelName` などの設定キーを追加

7. **実装手順（ステップ分割）**

   * Step 1…
   - AppSettings に認識モデルの設定値を追加

   * Step 2…
   - Paddle OCR設定UIに認識モデルのドロップダウンを追加
   - Save/Applyの配線を追加

   * Step 3…
   - `ocr_engine.py` で `text_recognition_model_name` を設定値から受け取る
   - gRPC起動時に必要なら引数で渡す

8. **非機能要件チェック**

   * 性能: 影響なし
   * 互換性: 既存の設定はデフォルト値で維持
   * 可観測性: 必要ならログ出力

9. **リスクと緩和策**
- Risk: モデル名の誤入力で起動失敗
- Mitigation: UIを選択式に限定し、フリーテキストにしない

10. **影響範囲**
- 変更ファイル候補
  - MainWindow.xaml / MainWindow.xaml.cs
  - Models/AppSettings.cs
  - OcrService/ocr_engine.py
  - （必要なら）Services/PaddleGrpcHost.cs

11. **Definition of Done**
- Paddle OCR設定で認識モデルを選択できる
- 選択値が保存・復元される
- Paddle OCR実行時に認識モデルが反映される

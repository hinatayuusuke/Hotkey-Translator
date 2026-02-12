# PaddleOCR v5 パラメーター運用ガイド

## 1. このガイドの対象

このリポジトリの現行実装では、`OcrEngine` が Paddle 選択時に `PaddleGrpcOcrProvider` を使います。  
つまり OCR 実行経路は基本的に以下です。

1. `Services/OcrEngine.cs`（Paddle分岐）
2. `Services/PaddleGrpcOcrProvider.cs`（gRPCクライアント）
3. `Services/PaddleGrpcHost.cs`（ローカル gRPC サーバー起動）
4. `OcrService/server.py` -> `OcrService/ocr_engine.py`（PaddleOCR v5 実行）

## 2. まず押さえる設定の責務

### 2.1 Settings.json で運用調整できる項目

`OcrEngine`
- `Paddle` で PaddleOCR 経路を使用。

`EnablePaddleGrpcHost`
- `true`: アプリ起動時にローカル `OcrService` を自動起動。
- `false`: 既存の外部 gRPC サーバー接続を想定。

`PaddleGrpcEndpoint` / `PaddleGrpcHost` / `PaddleGrpcPort`
- 接続先指定。`PaddleGrpcEndpoint` が優先。

`PaddleGrpcProjectDir` / `PaddleGrpcUvPath` / `PaddleGrpcServerScript`
- ローカル起動時の実行パス。

`PaddleDevice`
- サーバー起動引数に渡るが、`cpu` 指定でも現状は `gpu:0` に強制補正される。

`PaddleModelDir`
- gRPC サーバー起動時の `--model` に渡る（`ocr_engine.py` 側では `det_model_dir` に適用）。

`PaddleTextDetectionModelName`
- 検出モデル名（例: `PP-OCRv5_mobile_det`）。

`PaddleTextRecognitionModelName`
- 認識モデル名（`auto` も可）。
- `auto` の場合、`SourceLanguage` に応じて内部でモデル名を解決。

`EnablePaddleConfidenceFilter` / `PaddleConfidenceThreshold`
- OCR結果をアプリ側で後段フィルタする（描画/翻訳に渡す前）。

`PaddleGrpcReadyTimeoutMs` / `PaddleGrpcRestartMax` / `PaddleGrpcRestartWindowSeconds`
- サーバー起動待ち・再起動制御。

### 2.2 `ocr_engine.py` 側で固定されている項目（Settings.json 非連動）

`OcrService/ocr_engine.py` で現在ハードコードされている値:

- `text_det_thresh = 0.5`
- `text_det_box_thresh = 0.68`
- `text_det_unclip_ratio = 1.3`
- `text_rec_score_thresh = 0.58`
- `padding_px = 20`（入力画像外周の拡張）

NOTE: これらは `Settings.json` から直接変更できません。調整は `ocr_engine.py` 修正が必要です。

### 2.3 PaddleOCR 本体が提供している主要パラメーター

以下は PaddleOCR 3.x（General OCR Pipeline）で公開されている主要パラメーターです。  
このプロジェクトで未使用のものも含みます（運用上「使えるが、現状は渡していない」候補）。

初期化（`PaddleOCR(...)`）で代表的によく使う項目:
- 実行/モデル: `lang`, `ocr_version`, `device`
- 検出モデル: `text_detection_model_name`, `text_detection_model_dir`
- 認識モデル: `text_recognition_model_name`, `text_recognition_model_dir`
- 前処理モジュール: `use_doc_orientation_classify`, `use_doc_unwarping`, `use_textline_orientation`
- 検出しきい値: `text_det_limit_side_len`, `text_det_limit_type`, `text_det_thresh`, `text_det_box_thresh`, `text_det_unclip_ratio`
- 認識しきい値: `text_rec_score_thresh`, `text_rec_input_shape`
- 性能系: `enable_hpi`, `use_tensorrt`, `precision`, `enable_mkldnn`, `mkldnn_cache_capacity`, `cpu_threads`

推論時（`predict(...)`）の上書き:
- 公式ドキュメントでは、`predict` 呼び出し時に指定した値は `None` でない限り初期化値より優先されます。
- つまり、運用設計としては「初期化で基準値」「ケース別に predict で局所上書き」という使い分けが可能です。

NOTE: 現行の `OcrService/ocr_engine.py` は主に初期化時パラメーターで運用しており、`predict` 側の上書きは使っていません。

### 2.4 旧パラメーター名との対応（非推奨 -> 推奨）

PaddleOCR 3.x では旧名が deprecated 扱いのものがあります。  
現行コードでもコメントにある通り、新名称を使う方針が安全です。

- `det_db_thresh` -> `text_det_thresh`
- `det_db_box_thresh` -> `text_det_box_thresh`
- `det_db_unclip_ratio` -> `text_det_unclip_ratio`
- `use_angle_cls` -> `use_textline_orientation`

## 3. 重要な挙動差（ハマりやすい点）

### 3.1 `PaddleLanguage` は gRPC 経路では使われない

現行の gRPC 経路では、言語は `SourceLanguage` から解決されます。  
`PaddleGrpcOcrProvider` / `PaddleGrpcHost` ともに、`ja* -> "japan"`、それ以外は概ね `"en"` の扱いです。

つまり:
- `PaddleLanguage` を変更しても、gRPC 経路では期待どおり効かないケースがあります。
- 実運用では `SourceLanguage` と認識モデル設定の整合を優先してください。

### 3.2 CPU指定は実質無効

`PaddleDevice=cpu` でも、`PaddleGrpcHost` が `gpu:0` へ上書きします。  
GPU非対応環境での PaddleOCR v5 運用は想定外です。

### 3.3 `PaddleModelDir` は検出側のみ

現状 `ocr_engine.py` は `model_dir` を `det_model_dir` にしか割り当てていません。  
認識モデルをローカルディレクトリ固定で差し替えたい場合は、追加実装が必要です。

## 4. 症状別の調整ポイント

### 4.1 余計な枠を拾いすぎる（釣り検出）

優先順:
1. `ocr_engine.py` の `text_det_thresh` を上げる
2. `text_det_box_thresh` を上げる
3. `text_det_unclip_ratio` を下げる（枠膨張を抑える）

### 4.2 枠が細切れ / 文字欠けが増える

優先順:
1. `text_det_unclip_ratio` を少し上げる
2. `text_det_thresh` / `text_det_box_thresh` を下げる
3. `padding_px` を増やして見切れを緩和

### 4.3 低信頼文字を表示したくない

`Settings.json` で調整:
- `EnablePaddleConfidenceFilter=true`
- `PaddleConfidenceThreshold` を上げる（例: `0.6 -> 0.7`）

NOTE: これは Paddle推論時の内部閾値ではなく、アプリ後段の結果フィルタです。

## 5. 運用手順（推奨）

1. まず `Settings.json` 側で接続・モデル・後段フィルタを調整する。
2. それでも検出品質が合わない場合にだけ `ocr_engine.py` の検出/認識しきい値（`text_det_*`, `text_rec_score_thresh`）を調整する。
3. `padding_px` は最後に触る（座標ズレ検証コストが高いため）。
4. 変更は1回につき1〜2項目に限定し、ログと表示結果を比較する。

## 6. 最小サンプル（Settings.json 抜粋）

```json
{
  "OcrEngine": 1,
  "EnablePaddleGrpcHost": true,
  "PaddleGrpcEndpoint": "http://127.0.0.1:50051",
  "PaddleDevice": "gpu:0",
  "PaddleModelDir": null,
  "PaddleTextDetectionModelName": "PP-OCRv5_mobile_det",
  "PaddleTextRecognitionModelName": "PP-OCRv5_server_rec",
  "EnablePaddleConfidenceFilter": false,
  "PaddleConfidenceThreshold": 0.6,
  "SourceLanguage": "ja"
}
```

## 7. 参考（公式）

- General OCR Pipeline (PaddleOCR 3.x):  
  https://www.paddleocr.ai/main/en/version3.x/pipeline_usage/OCR.html
- Text Detection Module (PaddleOCR 3.x):  
  https://www.paddleocr.ai/v3.0.3/en/version3.x/module_usage/text_detection.html

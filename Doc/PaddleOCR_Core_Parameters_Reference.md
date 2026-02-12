# PaddleOCR 3.x 主要パラメーター解説

## 1. 概要

このドキュメントは、PaddleOCR 3.x の General OCR Pipeline で利用される主要パラメーターを、運用時の判断に使える形で整理したものです。  
本プロジェクトで現時点で未使用の項目も含みます。

## 2. 初期化時 `PaddleOCR(...)` の主要パラメーター

### 2.1 実行/モデル

- `lang`
- OCRパイプラインが使う言語系の設定。
- 認識モデルの選定方針と合わせて決める。

- `ocr_version`
- 使用するOCRパイプライン世代の指定（例: `PP-OCRv5`）。
- モデル名・利用可能な機能の前提になる。

- `device`
- 実行デバイス設定（例: `gpu:0`, `cpu`）。
- 実運用では推論時間と安定性の両面で最重要。

### 2.2 検出モデル

- `text_detection_model_name`
- 検出モデルの名前を指定。

- `text_detection_model_dir`
- 検出モデルをローカルディレクトリから読む場合に指定。

### 2.3 認識モデル

- `text_recognition_model_name`
- 認識モデルの名前を指定。

- `text_recognition_model_dir`
- 認識モデルをローカルディレクトリから読む場合に指定。

### 2.4 前処理モジュール

- `use_doc_orientation_classify`
- 文書方向補正モジュールの利用可否。

- `use_doc_unwarping`
- 文書の歪み補正モジュールの利用可否。

- `use_textline_orientation`
- 行方向の補助処理利用可否。

### 2.5 検出しきい値

- `text_det_limit_side_len`
- 検出前リサイズの基準長。

- `text_det_limit_type`
- リサイズ基準の解釈（max/min など）を制御。

- `text_det_thresh`
- テキスト判定の基本しきい値。上げると厳しくなる。

- `text_det_box_thresh`
- 候補ボックス採用しきい値。上げると低信頼ボックスを捨てやすい。

- `text_det_unclip_ratio`
- ボックス膨張率。下げると座標膨張を抑えやすい。

### 2.6 認識しきい値

- `text_rec_score_thresh`
- 認識結果のスコアしきい値。

- `text_rec_input_shape`
- 認識入力形状の指定。

### 2.7 性能系

- `enable_hpi`
- 高性能推論オプション。

- `use_tensorrt`
- TensorRT 利用可否。

- `precision`
- 推論精度設定（fp32/fp16/int8 など）。

- `enable_mkldnn`
- CPU実行時の最適化。

- `mkldnn_cache_capacity`
- MKLDNN キャッシュ容量設定。

- `cpu_threads`
- CPUスレッド数。

## 3. 推論時 `predict(...)` の上書きルール

- `predict(...)` 呼び出し時に指定した値は、`None` でない限り初期化値より優先されます。
- 運用上は次の使い分けが実践的です:
- 常用設定: `PaddleOCR(...)` 初期化時に固定。
- ケース別最適化: `predict(...)` で局所上書き（例: 入力種類別のしきい値調整）。

## 4. 運用の基本方針

1. まずは初期化パラメーターで安定基準を作る。  
2. 入力特性が明確に分かれる場合のみ `predict(...)` 上書きを導入する。  
3. しきい値調整は 1 回につき 1〜2項目に限定し、ログで比較する。  

## 5. 参考

- https://www.paddleocr.ai/main/en/version3.x/pipeline_usage/OCR.html

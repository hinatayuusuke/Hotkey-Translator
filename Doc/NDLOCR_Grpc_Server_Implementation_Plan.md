# NDLOCR gRPCサーバ実装案（PaddleOCR実装準拠）

## 1. 概要（1–3行）
`OcrService/server.py` と `OcrServiceVL/server.py` の実装パターンを踏襲して、NDLOCR向けのローカルgRPCサーバを追加する。  
API契約は既存 `ocr.proto`（`Health` / `Recognize`）を維持し、C# 側の呼び出し互換を壊さない。  
初期は CPU 既定・単一エンジン再利用で安定性優先、後段で性能最適化を行う。

## 2. ゴール / 非ゴール
### ゴール
- 既存と同じ gRPC 契約で NDLOCR 推論を提供する `OcrServiceNDL/server.py` を設計する。
- `EnginePool`（LRU/TTL）と `Health` / `Recognize` の失敗時挙動を Paddle 実装と同等にそろえる。
- ログ形式を `stage=ocr_grpc host=ndl ...` に統一し、運用時の切り分けを容易にする。

### 非ゴール
- C# 側 `ResourceHostFacade` / `OcrEngine` の全面実装（本書ではサーバ中心）。
- NDLOCRモデルアルゴリズム自体の改変。
- proto 契約の破壊的変更。

## 3. 前提・仮定
- 参考実装:
  - `OcrService/server.py`
  - `OcrServiceVL/server.py`
- 契約:
  - `OcrService/ocr.proto` の `OcrService` / `Health` / `Recognize` を利用する。
- NDLOCR推論本体は `NDLOCR/ndl_ocr_engine.py` を利用可能。
- 既存の呼び出し側は `OcrResponse.json`（`lines[]`）を受け取る前提で動作する。

## 4. 現状整理
- Paddle系サーバの共通点:
  - 起動時に `ensure_proto()` で `ocr_pb2.py` / `ocr_pb2_grpc.py` を自動生成。
  - `EnginePool` でエンジン再利用（TTL + LRU、`Lock` で排他）。
  - `Recognize` で空画像を `INVALID_ARGUMENT`、推論例外を `INTERNAL` で返却。
  - `grpc.max_receive_message_length` / `grpc.max_send_message_length` を設定。
  - 受信/送信byte数ログを出力。
- 差分:
  - PaddleOCRは言語・det/recモデル切替キー。
  - PaddleOCR-VLはパイプライン引数が多く、`close()` を呼ぶ明示開放あり。
- NDLOCRでは言語切替を持たず、`device` と閾値系設定が主な可変軸。

## 5. 提案アーキテクチャ
### コンポーネント構成
- `OcrServiceNDL/server.py`（新規）
  - `ensure_proto()`
  - `EnginePool`
  - `OcrService` 実装
  - `main()`（argparse + gRPC server）
- `OcrServiceNDL/ocr_ndl_engine.py`（新規）
  - `NdlOcrLiteEngine` をラップし、`recognize(image_bytes) -> json string` を提供。
- `OcrServiceNDL/ocr.proto`（初期は既存 `OcrService/ocr.proto` と同一内容で運用）

### データフロー / シーケンス
1. クライアントが `Recognize(OcrRequest)` で画像bytes送信。
2. サーバが `request.image` を検証（空なら `INVALID_ARGUMENT`）。
3. `EnginePool.get()` でエンジンを取得（言語キー分岐なし）。
4. `engine.recognize(request.image)` を実行。
5. JSON文字列を `OcrResponse.json` で返却。
6. 例外時は `INTERNAL` + detail を返し、呼び出し側でフォールバック判断。

### 既存パターンへの整合
- `EnginePool` のTTL/LRUロジックは Paddle と同形にする。
- ログキー命名を統一（`stage=ocr_grpc host=ndl event=request_bytes/response_bytes`）。
- gRPCオプションと `ThreadPoolExecutor(max_workers=4)` は初期値を合わせる。

## 6. インターフェース設計
### API / 関数
- `Health(HealthRequest) -> HealthResponse`
  - 常時 `ready=true` ではなく、初回エンジン生成失敗後は `ready=false` へ遷移可能にする案。
- `Recognize(OcrRequest) -> OcrResponse`
  - `OcrRequest.language` は NDLOCR では使用しない（常に無視）。
  - `text_detection_model_name` / `text_recognition_model_name` も当面未使用（互換維持のみ）。

### 起動引数（案）
- `--host`（既定 `127.0.0.1`）
- `--port`（既定 `50053`）
- `--device`（`cpu|cuda`、既定 `cpu`）
- `--model-dir`（既定 `../NDLOCR/model`）
- `--config-dir`（既定 `../NDLOCR/config`）
- `--det-conf-threshold` / `--det-iou-threshold`
- `--max-message-bytes`（既定 32MB）
- `--pool-max-engines`（既定 1）
- `--pool-ttl-seconds`（既定 1800）

### 入出力 / エラー / バリデーション
- 入力: `request.image` 必須。
- 出力JSON: `{"lines":[{"text","box":[x,y,w,h],"confidence","detection_confidence","recognition_confidence"}]}`
- NOTE: `confidence` 系は観測用途のメタデータとして返却し、初期段階では挙動制御に使わない。
- バリデーション:
  - `image` が空なら `INVALID_ARGUMENT`。
  - JSONシリアライズ不能等は `INTERNAL`。
- エラー詳細は `context.set_details(str(exc))` を使い、呼び出し側ログに残る形を維持。

## 7. 実装手順（ステップ分割）
### Step 1: スケルトン作成
- `OcrServiceNDL` ディレクトリを追加。
- `ocr.proto` / `ensure_proto()` / `Health` / ダミー `Recognize` を実装。
- gRPC起動確認（`Health` ready 応答）。

### Step 2: エンジン接続
- `ocr_ndl_engine.py` で `NDLOCR/ndl_ocr_engine.py` をラップ。
- `Recognize` で実推論実行・JSON返却。
- `EnginePool` にTTL/LRUを追加。

### Step 3: 運用ログと安全化
- request/response bytes ログを追加。
- 推論時間ログ（`stage=ocr_grpc host=ndl event=timing ...`）を追加。
- 例外分類（入力不正 vs 推論失敗）を明確化。

### Step 4: 呼び出し側接続（別PR推奨）
- C# `NdlGrpcHost` / Provider と接続。
- 失敗時フォールバックと再起動制御を追加。

## 8. 非機能要件チェック
- 性能:
  - 単一エンジン再利用を基本。
  - 起動時間と推論時間を分離ログ化。
- セキュリティ:
  - ローカルバインド（`127.0.0.1`）固定。
  - 画像bytes以外の外部入力を扱わない。
- 可観測性:
  - `stage=ocr_grpc host=ndl` 系ログで追跡可能。
- 互換性:
  - 既存 proto 契約を維持。
- 運用:
  - `max_message_bytes` と pool設定を引数で上書き可能。

## 9. リスクと緩和策
- Risk: NDLOCR初回ロードが長く、Healthはreadyでも初回Recognizeが遅い。  
  Mitigation: 起動時に軽いウォームアップを任意実行するフラグを用意。

- Risk: CUDA指定でも実行環境が未整備で失敗。  
  Mitigation: サーバ側で `device=cuda` 失敗時の明示エラーを返し、呼び出し側でCPU再試行/フォールバック。

- Risk: pool保持でメモリ圧迫。  
  Mitigation: 既定 `max_engines=1` + TTLで制限。

## 10. 影響範囲（変更ファイル候補・移行・ドキュメント更新）
- 新規（サーバ実装）
  - `OcrServiceNDL/server.py`
  - `OcrServiceNDL/ocr_ndl_engine.py`
  - `OcrServiceNDL/ocr.proto`
  - `OcrServiceNDL/pyproject.toml`（必要なら）
- 既存（将来の接続側）
  - `Services/Application/ResourceHostFacade.cs`
  - `Services/OcrEngine.cs`
  - `Models/AppSettings.cs`
- ドキュメント
  - `Doc/NDLOCR_Lite_Integration_Plan.md`（本計画への参照追記）

## 11. Definition of Done
- [ ] `OcrServiceNDL/server.py` が `Health` / `Recognize` を提供し起動できる。
- [ ] `Recognize` が画像bytes入力でJSONを返す。
- [ ] レスポンスJSONに言語依存項目を含めない。
- [ ] レスポンスJSONに `confidence` / `detection_confidence` / `recognition_confidence` を含める。
- [ ] `confidence` 系は初期段階で表示・翻訳抑制の判定に使わない。
- [ ] 空入力で `INVALID_ARGUMENT`、推論例外で `INTERNAL` を返す。
- [ ] `stage=ocr_grpc host=ndl` の request/response ログが出る。
- [ ] poolのTTL/LRUが機能し、長時間運用でエンジン数が上限を超えない。
- [ ] 既存クライアントが proto 変更なしで呼び出せる。

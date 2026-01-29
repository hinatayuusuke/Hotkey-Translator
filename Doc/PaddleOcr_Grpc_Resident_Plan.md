# Paddle OCR gRPC 常駐サーバー構成案（再起動・監視・フォールバック込み）

1. **概要（1–3行）**
   WPF 起動時に Python gRPC サーバーを起動し常駐させ、OCR時は gRPC で JSON を取得する。
   サーバーのヘルスチェック・監視・自動再起動を組み込み、失敗時は WinRT OCR へフォールバックする。

2. **ゴール / 非ゴール**
   **ゴール**

   * PaddleOCR 初期化を常駐化し、OCRレイテンシを安定化
   * gRPC サーバーの監視/再起動により安定運用
   * gRPC障害時は自動で WinRT OCR にフォールバック

   **非ゴール**

   * gRPC サーバーの冗長構成（複数プロセス）
   * OCR 精度のアルゴリズム改善
   * Python 側の推論最適化（別スコープ）

3. **前提・仮定**（不確実性の扱いを明確化）

   * Python 側は `OcrService/server.py` で gRPC サーバーを提供する。
   * WPF 側はアプリ起動時に Python を起動し、終了時に停止する。
   * gRPC の応答形式は JSON（既存 `OcrResultModel` に対応）。

4. **現状整理**（現行挙動、関連モジュール、既存制約）

   * `PaddleOcrProvider` は `uv run python paddle_ocr_bridge.py` を都度実行する。
   * 1回のOCRごとにプロセス起動・モデルロードが発生する。
   * OCR失敗時は `OcrEngine` が WinRT にフォールバック可能。

5. **提案アーキテクチャ**

   ### コンポーネント構成

   * **WPF側**
     * `PaddleGrpcHost`（新規）
       * Python gRPC サーバー起動/停止/監視
       * ヘルスチェック
       * 再起動制御
     * `PaddleGrpcOcrProvider`（新規）
       * gRPC クライアントで OCR JSON を取得
       * タイムアウト/エラー処理
   * **Python側**
     * `server.py`（gRPC サーバー）
     * `ocr_engine.py`（PaddleOCR 初期化/推論）

   ### データフロー / シーケンス

   1. WPF 起動時に `PaddleGrpcHost.Start()` を呼び出す。
   2. `PaddleGrpcHost` が Python サーバーを起動し、`HealthCheck` で ready を確認。
   3. OCR 時は `PaddleGrpcOcrProvider` が gRPC 経由で JSON を取得。
   4. gRPC 失敗時は再起動を試み、失敗が続く場合は WinRT OCR にフォールバック。
   5. WPF 終了時に `PaddleGrpcHost.Stop()` を呼び出してプロセスを終了。

   ### 既存パターンへの整合

   * `OcrEngine` の `Paddle` 分岐を `PaddleGrpcOcrProvider` に切り替える。
   * 失敗時の WinRT フォールバックは既存の `OcrEngine` の仕組みを活用。

6. **インターフェース設計**

   ### WPF側 API

   * `PaddleGrpcHost`
     * `StartAsync()` / `Stop()`
     * `Task<bool> WaitForReadyAsync(timeout)`
     * `OnCrashed` イベント（プロセス終了検知）
   * `PaddleGrpcOcrProvider`
     * `RecognizeAsync(Bitmap, AppSettings, CancellationToken)`

   ### gRPC API（例）

   * `OcrService.Ocr` (request: image bytes + lang + device + modelDir)
   * response: JSON string（既存 `PaddleOcrResponse` 相当）

7. **実装手順（ステップ分割）**

   ### Step 1: Python gRPC サーバー
   * `server.py` で gRPC サーバー起動
   * `ocr_engine.py` で PaddleOCR を初期化して保持
   * HealthCheck サービス追加

   ### Step 2: WPF gRPC クライアント導入
   * C# 側に gRPC クライアント追加
   * `PaddleGrpcOcrProvider` 実装

   ### Step 3: 常駐起動・監視
   * `PaddleGrpcHost` を追加
   * 起動時に `StartAsync` + `WaitForReady`
   * 異常終了時に再起動（最大 N 回）

   ### Step 4: フォールバック強化
   * gRPC 連続失敗時は WinRT OCR へ切替
   * ログに「フォールバック発動理由」を出力

8. **非機能要件チェック**

   * **性能**：OCRごとの初期化コスト削減
   * **セキュリティ**：画像データはローカルのみで処理
   * **可観測性**：起動/再起動/ヘルスチェック失敗をログ出力
   * **互換性**：gRPC使用不可なら WinRT fallback

9. **リスクと緩和策**

   * **Python プロセスが起動しない**
     * 緩和: ヘルスチェックタイムアウト後にフォールバック
   * **gRPC がタイムアウトする**
     * 緩和: 再起動 + フォールバック
   * **モデルパス不一致**
     * 緩和: AppSettings と Python 側を同一設定で起動

10. **影響範囲**（変更ファイル候補・移行・ドキュメント更新）

* 変更候補
  * `Services/OcrEngine.cs`（Paddle分岐をgRPCに）
  * `Services/PaddleGrpcHost.cs`（新規）
  * `Services/PaddleGrpcOcrProvider.cs`（新規）
  * `OcrService/server.py`, `OcrService/ocr_engine.py`（Python側）
* ドキュメント更新
  * 本ドキュメント

11. **Definition of Done**（完了条件のチェックリスト）

* [ ] WPF 起動時に Python gRPC サーバーが常駐起動する
* [ ] OCR要求時に gRPC で JSON が取得できる
* [ ] サーバー停止/クラッシュ時に再起動が走る
* [ ] gRPC が使用不可の場合 WinRT OCR にフォールバックできる
* [ ] 終了時に Python プロセスが停止する

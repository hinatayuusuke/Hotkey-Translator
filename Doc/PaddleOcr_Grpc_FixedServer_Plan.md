# Paddle OCR gRPC 固定サーバー仕様 実装案（GPU強制 + 言語追従）

1. **概要（1–3行）**
   gRPCホスト起動時に `cpu` 指定なら `gpu`（line 0）へ強制上書きし、
   requestは `language` のみを送信して WPF UI の言語選択に追従させる。

2. **ゴール / 非ゴール**
   **ゴール**

   * PaddleOCR 3.x / GPU必須前提で常駐サーバーを安定起動
   * WPF側とPython側のパラメータ不整合を排除
   * 言語設定だけはUIに追従し、それ以外は固定化する

   **非ゴール**

   * ランタイムでモデル/デバイスを切り替える
   * CPUフォールバックでPaddleOCRを動かす

3. **前提・仮定**（不確実性の扱いを明確化）

   * サーバー起動時にモデル/デバイスを固定する運用
   * 言語は request の値を使用し、必要なら内部でOCRエンジンを切り替える
   * GPUが必須で、`cpu` 指定は受け付けない
   * WPF側のOCRは gRPC 不可時に WinRTへフォールバック

4. **現状整理**（現行挙動、関連モジュール、既存制約）

   * WPF側 `PaddleGrpcOcrProvider` は request に device/lang/model_dir を含めて送信
   * Python側 `server.py` は起動時に固定パラメータで OCR エンジンを初期化
   * `ocr_engine.py` は GPU必須仕様に更新済み

5. **提案アーキテクチャ**

   ### コンポーネント構成

   * WPF: `PaddleGrpcHost`（起動時にGPU強制）
   * WPF: `PaddleGrpcOcrProvider`（requestは `language` のみ送信）
   * Python: `server.py` / `ocr_engine.py`（起動時固定パラメータ＋言語はrequest反映）

   ### データフロー / シーケンス

   1. WPF起動時、`PaddleGrpcHost.StartAsync` で `PaddleDevice` が `cpu` の場合は `gpu` に強制上書き
   2. Pythonサーバーは固定パラメータで `PaddleOcrEngine` を初期化
   3. OCR request は画像bytes＋言語のみ送信し、サーバーが言語を反映して推論

   ### 既存パターンへの整合

   * 既存の「サーバー起動時に固定パラメータ」の運用を強化
   * requestは言語のみ反映し、他パラメータは固定

6. **インターフェース設計**

   ### gRPC API

   * `OcrRequest` から `device/model_dir` を削除
   * `OcrRequest` は `image` + `language`

   ### WPF側

   * `PaddleGrpcHost` で `PaddleDevice == "cpu"` の場合 `gpu` に上書き
   * `PaddleGrpcOcrProvider` は `image` + `language` のみ送信

7. **実装手順（ステップ分割）**

   ### Step 1: WPF側 GPU強制
   * `PaddleGrpcHost.StartProcess` で `PaddleDevice` が `cpu` なら `gpu` に上書き
   * ログに上書き理由を残す

   ### Step 2: gRPC request簡略化
   * `Protos/OcrGrpc.proto` / `OcrService/ocr.proto` から `device/model_dir` を削除
   * `PaddleGrpcOcrProvider` の request 送信を `image` + `language` に変更

   ### Step 3: Python側整合
   * `server.py` は引数で固定値を受け取り、requestは画像＋言語のみ利用
   * 受信側で `device/model_dir` に依存しない構造を明示

8. **非機能要件チェック**

   * **性能**：requestサイズ縮小・固定初期化で安定
   * **セキュリティ**：ローカル限定通信
   * **可観測性**：GPU強制上書き時にログ出力
   * **互換性**：CPU運用は非対応

9. **リスクと緩和策**

   * **GPUが無い環境で起動失敗**
     * 緩和: 起動失敗時は WinRT OCR にフォールバック
   * **プロトコル変更による互換性崩れ**
     * 緩和: WPFとPythonを同時更新し、旧クライアントは破棄

10. **影響範囲**（変更ファイル候補・移行・ドキュメント更新）

* 変更候補
  * `Services/PaddleGrpcHost.cs`
  * `Services/PaddleGrpcOcrProvider.cs`
  * `Protos/OcrGrpc.proto`
  * `OcrService/ocr.proto`
  * `OcrService/server.py`
* ドキュメント更新
  * 本ドキュメント

11. **Definition of Done**（完了条件のチェックリスト）

* [ ] gRPCホスト起動時に `cpu` 指定が `gpu` に強制上書きされる
* [ ] OCR request に `device/model_dir` が含まれない
* [ ] Python側は固定パラメータで推論し、requestは画像＋言語のみ受け取る
* [ ] 既存のWinRTフォールバックが維持される

# OCR gRPC Message Size Resilience 実装案

1. **概要（1–3行）**
- 本計画は、`PaddleOCR` / `PaddleOCR-VL` の両経路で発生しうる `ResourceExhausted (Received message larger than max)` を、gRPC共通対策として解消する実装案である。
- 方針は「サーバ側メッセージ上限引き上げ（根本対策）」+「クライアント側自動ダウンサイジング再送（安全弁）」の二段構えとする。
- 既存のOCR前処理で使っている縮小/座標復元ロジックを流用し、重複実装を避ける。

2. **ゴール / 非ゴール**
### ゴール
- `PaddleGrpcOcrProvider` / `PaddleVlGrpcOcrProvider` の両方で、4MB上限超過によるOCR失敗を抑止する。
- ダウンサイジング時も `OcrLine.Rect` が元ROI座標系へ正しく復元される。
- payloadサイズ、縮小倍率、再試行回数をログで追跡できる。

### 非ゴール
- OCRアルゴリズムやモデル自体の変更。
- WinRT/翻訳経路の挙動変更。
- 画像品質最適化（高圧縮コーデック導入等）の全面刷新。

3. **前提・仮定**
- 現在クライアント側（C#）は gRPC channel の send/receive 上限を32MBに設定済み。
- 一方で Python gRPC server はデフォルト上限（約4MB）で起動しており、ここが主な失敗点。
- `OcrPreprocessCoordinator` には縮小後OCR結果の逆スケール復元処理が存在する。

4. **現状整理**
- `Services/PaddleGrpcOcrProvider.cs` / `Services/PaddleVlGrpcOcrProvider.cs`
  - PNG bytes を `OcrRequest.Image` に直接格納して送信。
- `OcrService/server.py` / `OcrServiceVL/server.py`
  - `grpc.server(...)` に message size options 未指定。
- エラー例:
  - `StatusCode="ResourceExhausted", Detail="Received message larger than max (...)"`

5. **提案アーキテクチャ**
### 5.1 コンポーネント構成
- 共通ユーティリティ（新規）
  - 例: `Services/OcrGrpcPayloadResizer.cs`
  - 役割: ビットマップ縮小、PNGバイト化、逆スケール座標復元。
- 既存Provider更新
  - `PaddleGrpcOcrProvider` / `PaddleVlGrpcOcrProvider`
  - 役割: 通常送信 -> `ResourceExhausted` 時のみ段階縮小再送。
- Pythonサーバ更新
  - `OcrService/server.py` / `OcrServiceVL/server.py`
  - 役割: gRPC message上限を構成値で明示設定。

### 5.2 データフロー / シーケンス
1. Providerが元画像をPNG化しサイズ計測。
2. 通常送信。
3. `ResourceExhausted` 発生時、縮小倍率テーブル（例: `0.85 -> 0.7 -> 0.55`）で再送。
4. 受信成功時、OCR座標を `1/scale` で元ROI座標へ復元。
5. 最大再試行超過時は従来どおり上位へ例外（OcrEngineでフォールバック）。

### 5.3 既存パターンへの整合
- 縮小/逆スケールの数式は `OcrPreprocessCoordinator.ScaleOcrResult` の考え方を共通ユーティリティ化して再利用する。
- Providerごとの差分は「JSONパース部分」のみに限定する。

6. **インターフェース設計**
### 6.1 共通ユーティリティ（案）
- `OcrPayloadAttempt BuildInitial(Bitmap source)`
- `IEnumerable<OcrPayloadAttempt> BuildRetrySequence(Bitmap source, IReadOnlyList<double> scales)`
- `IReadOnlyList<OcrLine> RescaleLines(IReadOnlyList<OcrLine> lines, double scaleX, double scaleY)`

`OcrPayloadAttempt`:
- `Bitmap PayloadBitmap`
- `byte[] PngBytes`
- `double ScaleX`
- `double ScaleY`
- `long ByteSize`

### 6.2 サーバ設定（案）
- Python側で `grpc.server(..., options=[("grpc.max_receive_message_length", N), ("grpc.max_send_message_length", N)])`
- 初期推奨: `N = 32 * 1024 * 1024`

### 6.3 エラー・バリデーション
- `ResourceExhausted` のみ縮小再試行対象。
- `InvalidArgument` / `Internal` 等は即時失敗（既存挙動維持）。
- 縮小後もサイズ超過が続く場合は最終エラーをそのまま伝播。

7. **実装手順（ステップ分割）**
- Step 1: Python gRPC server 上限設定
  - `OcrService/server.py` と `OcrServiceVL/server.py` に message size options を追加。

- Step 2: 共通縮小ユーティリティ追加
  - `Services` 配下に payload生成と座標逆変換ロジックを新設。

- Step 3: Paddle gRPC Provider適用
  - `PaddleGrpcOcrProvider` を共通ユーティリティ利用へ置換し、`ResourceExhausted` 再試行を実装。

- Step 4: Paddle-VL gRPC Provider適用
  - `PaddleVlGrpcOcrProvider` に同じ再試行を適用。

- Step 5: ログ/観測強化
  - 初回payloadサイズ、再試行倍率、最終成功倍率、失敗時最終サイズをログ化。

- Step 6: 手動検証
  - 高解像度/大ROI画像で `ResourceExhausted` が解消されることを確認。

8. **非機能要件チェック**
- 性能
  - 通常ケースは追加コスト最小（初回送信のみ）。
  - 超過時のみ縮小再送が走るため、遅延増は限定的。

- 可観測性
  - `stage=ocr_grpc payload_bytes=... scale=... retry=...` を出力。

- 互換性
  - 失敗時フォールバック先（WinRT）など上位の挙動は維持。

- 運用
  - サーバ上限値は定数化し、将来設定化できる余地を残す。

9. **リスクと緩和策**
- Risk: 縮小しすぎでOCR精度低下。
- Mitigation: 最小倍率を制限（例: 0.55）し、まずサーバ上限引き上げで回避を優先。

- Risk: 座標逆変換ミスでOverlay位置ずれ。
- Mitigation: 共通ユーティリティで一元管理し、Provider個別実装を避ける。

- Risk: server/client 上限値の非整合。
- Mitigation: 両サーバ同値設定 + ログで実際の受信サイズを追跡。

10. **影響範囲**
- `OcrService/server.py` — gRPC message上限設定。
- `OcrServiceVL/server.py` — gRPC message上限設定。
- `Services/PaddleGrpcOcrProvider.cs` — 自動縮小再試行/座標復元。
- `Services/PaddleVlGrpcOcrProvider.cs` — 自動縮小再試行/座標復元。
- `Services/*`（新規）— 共通 payload/座標ユーティリティ。
- 必要なら `Doc/` — 運用パラメータ追記。

11. **Definition of Done**
- [ ] `PaddleOCR` / `PaddleOCR-VL` の両サーバに message size options が適用されている。
- [ ] `ResourceExhausted` 発生時のみ段階縮小再送が機能する。
- [ ] 成功時の `OcrLine.Rect` が元ROI座標へ復元される。
- [ ] 通常ケースで既存品質/速度への影響が許容範囲である。
- [ ] 高解像度入力で `ResourceExhausted` 再現ケースが解消される。
- [ ] `dotnet build Hotkey-Translator.csproj` が成功する。

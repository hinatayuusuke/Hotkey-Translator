# NDLOCR-Lite OCRエンジン統合 実装案

## 1. 概要（1-3行）
既存アプリの目的（画面OCR→翻訳→同座標オーバーレイ）を維持したまま、`NDLOCR-Lite` を新しいOCRエンジンとして追加する。  
既存の `Paddle/PaddleOCR-VL` と同じ「ローカルgRPCホスト + C# Provider」方式で統合し、失敗時は `WinRT` へフォールバックする。  
初期は安全重視で、最小差分で導入してから精度・速度調整を段階的に行う。

## 2. ゴール / 非ゴール
### ゴール
- `OcrEngine` で `NDLOCR-Lite` を選択可能にする。
- OCR結果を既存 `OcrResultModel` に載せ、翻訳・差分判定・オーバーレイ描画を既存経路で再利用する。
- OCR座標をROI画像基準で返し、WPF/Hook V2 の両オーバーレイで同位置表示を維持する。
- 起動失敗・推論失敗時は既存同様に `WinRT` フォールバックを保証する。

### 非ゴール
- NDLOCR本体（`OcrServiceNDL`）の大規模改変。
- 既存の翻訳ロジック、Overlayレイアウトアルゴリズムの作り直し。
- 初期段階でのNDLOCR独自読み順情報の全面採用（まずは既存 `OcrLineGrouper` 優先）。

## 3. 前提・仮定
- `OcrServiceNDL` には ONNX モデルと推論コード（`ndl_core_engine.py`）が揃っている。
- NDLOCR-Lite は CC BY 4.0。配布時にクレジット/ライセンス表記が必要。
- 現行アプリは OCR結果を `OcrResultModel(lines, pixelWidth, pixelHeight)` で受け、以後の翻訳・オーバーレイはこの契約に依存している。
- OCRホスト追加は既存パターン（`PaddleGrpcHost`, `PaddleVlGrpcHost`）を踏襲する。

## 4. 現状整理
- OCR選択は `Models/OcrEngineKind.cs` と `Services/OcrEngine.cs` の分岐で実施。
- Python OCRは `ResourceHostFacade` 管理の常駐gRPCホスト経由（Paddle系）。
- パイプラインは `PipelineOrchestrator` で `Capture -> OCR -> Group/Diff -> Translate -> Overlay` を直列実行。
- オーバーレイ座標は OCR座標（ROI内）を基準に既存変換で WPF/Hook V2 へ流しているため、新エンジン側は「ROIピクセル基準」を守る必要がある。

## 5. 提案アーキテクチャ
### コンポーネント構成
- C#:
  - `NdlGrpcHost`（新規）: NDLOCR gRPCサーバ起動/ヘルスチェック。
  - `NdlGrpcOcrProvider`（新規）: gRPC呼び出しと `OcrResultModel` 変換。
  - `OcrEngine`（更新）: `Ndl` 分岐とWinRTフォールバック追加。
  - `ResourceHostFacade`（更新）: `ndl_grpc` のロード/停止制御を追加。
- Python:
  - `OcrServiceNDL/server.py`（新規）: 画像bytes受信、NDLOCR推論、JSON返却。
  - `OcrServiceNDL/ndl_grpc_adapter.py`（新規）: `OcrServiceNDL/ndl_core_engine.py` を呼ぶラッパー。

### データフロー / シーケンス
1. `CaptureManager` がROI対象フレームを取得。  
2. `OcrEngine` が `OcrEngineKind.Ndl` の場合、`NdlGrpcOcrProvider` を実行。  
3. ProviderはROI BitmapをPNG bytes化して gRPC送信。  
4. Python側でNDLOCR推論し、`lines[{text, box[x,y,w,h], confidence}]` を返す。  
5. C#で `OcrLine` に変換し `OcrResultModel` を返却。  
6. 以降は既存の Group/Diff/Translate/Overlay 経路をそのまま使用。  

### 既存パターンへの整合
- 既存 `OcrGrpc.proto`（`OcrRequest/OcrResponse`）を使い回し、契約変更を最小化する。
- ログ命名は既存に揃えて `stage=ocr_grpc host=ndl ...` を使用する。
- 失敗時フォールバックは既存 `Paddle*` と同一方針（例外を握ってWinRTへ）。

## 6. インターフェース設計
### API / 設定追加（案）
- `Models/OcrEngineKind.cs`
  - `Ndl = 4` を追加（既存数値は固定維持）。
- `Models/AppSettings.cs`
  - `EnableNdlGrpcHost: bool = true`
  - `NdlGrpcProjectDir: string = "OcrServiceNDL"`
  - `NdlGrpcUvPath: string = "uv"`
  - `NdlGrpcServerScript: string = "server.py"`
  - `NdlGrpcEndpoint: string = "http://127.0.0.1:50053"`
  - `NdlGrpcHost: string = "127.0.0.1"`
  - `NdlGrpcPort: int = 50053`
  - `NdlGrpcReadyTimeoutMs: int = 120000`
  - `NdlGrpcRestartMax: int = 3`
  - `NdlGrpcRestartWindowSeconds: int = 30`
  - `NdlDevice: string = "cpu"`（`cpu|cuda`）
  - `NdlDetScoreThreshold: double = 0.2`
  - `NdlDetConfThreshold: double = 0.25`
  - `NdlDetIouThreshold: double = 0.2`
- `ViewModels/SettingsViewModel.cs` + `MainWindow.xaml`
  - OCRエンジン選択に `NDLOCR-Lite (gRPC)` を追加。
  - 初期段階は詳細パラメータをUIに出さず、`settings.json` 管理でも可。

### 入出力 / バリデーション / エラー
- PythonレスポンスJSON:
  - `{"lines":[{"text":"...", "box":[x,y,w,h], "confidence":0.0-1.0}]}`
- 座標は必ず ROI画像ピクセル基準。
- `box` は画像範囲にクランプ（負値・はみ出し防止）。
- 空結果は `lines=[]` とし、例外時は gRPC `INTERNAL` で返す。

## 7. 実装手順（ステップ分割）
### Step 1: 基盤追加（小さくマージ）
- `OcrServiceNDL` を追加し、`Health`/ダミー `Recognize` でgRPC疎通を先に成立させる。
- `NdlGrpcHost`/`NdlGrpcOcrProvider` を追加し、`OcrEngine` から呼べる状態にする。

### Step 2: NDLOCR推論接続
- `ndl_grpc_adapter.py` でNDLOCR推論を実装。
- まずは「1枚画像入力 -> line配列返却」に限定し、XML出力など不要処理は省く。
- `stage=ocr_grpc host=ndl event=request_bytes/response_bytes` ログを追加。

### Step 3: 設定・UI配線
- `AppSettings`/`SettingsViewModel`/`MainWindow.xaml` に `Ndl` 選択を追加。
- `ResourceHostFacade` に `ndl_grpc` を登録し、選択時のみ起動。

### Step 4: 安全化
- `PaddleOcrSettingsRule` にならって NDL用の設定正規化ルールを追加。
- 起動失敗時に `OcrEngine = WinRt` へ戻す互換動作を整備。
- 非ASCIIパス問題（NDLOCR README注意）を検知し、明示エラーログを出す。

### Step 5: 検証
- Window/Hook両経路で「OCRテキスト位置とオーバーレイ位置の一致」を目視検証。
- F8/F10/F9 など既存ホットキー運用で挙動回帰がないことを確認。

## 8. 非機能要件チェック
- 性能: CPU既定。初期化コストが高い場合はEnginePool + LRU/TTLで常駐再利用。
- セキュリティ: 受信データは画像bytesのみ。外部通信なし。ローカル127.0.0.1限定。
- 可観測性: `stage=ocr_grpc host=ndl`、推論時間、行数、フォールバック有無を記録。
- 互換性: 既存OCRエンジンの分岐を壊さず追加方式。既存設定値は維持。
- 運用: ホスト再起動コマンドと失敗時自動停止（設定OFF化）を既存と同様にする。

## 9. リスクと緩和策
- Risk: NDLOCRの座標がpadding由来で画像外にはみ出す。
- Mitigation: Python側で最終 `box` を画像範囲にクランプして返す。

- Risk: CPU推論で遅延が大きい。
- Mitigation: 常駐プロセス化、Engine再利用、必要ならROI縮小/閾値調整を後段で追加。

- Risk: 依存関係衝突（既存Paddle環境と混在）。
- Mitigation: `OcrServiceNDL` を独立プロジェクト/venvで分離。

- Risk: ライセンス表示漏れ。
- Mitigation: DocにCC BY 4.0のクレジット追記、配布時の同梱確認をDoDへ含める。

## 10. 影響範囲（変更ファイル候補）
- `Models/OcrEngineKind.cs`
- `Models/AppSettings.cs`
- `Services/OcrEngine.cs`
- `Services/Application/ResourceHostFacade.cs`
- `Services/Settings/FeatureSettings/FeatureSettingsProvider.cs`
- `ViewModels/SettingsViewModel.cs`
- `MainWindow.xaml`
- `Services/NdlGrpcHost.cs`（新規）
- `Services/NdlGrpcOcrProvider.cs`（新規）
- `OcrServiceNDL/server.py`（新規）
- `OcrServiceNDL/ndl_grpc_adapter.py`（新規）
- `Doc/NDLOCR_Lite_Integration_Plan.md`（本書）

## 11. Definition of Done
- [ ] `NDLOCR-Lite` をUIで選択できる。
- [ ] 選択時に `OcrServiceNDL` が起動し、`Health` が ready になる。
- [ ] OCR結果が翻訳され、WPF/Hook V2 で同座標に表示される。
- [ ] 失敗時にWinRTフォールバックし、アプリ継続動作する。
- [ ] 設定保存/再起動後も選択状態が維持される。
- [ ] 必要ログ（request/response bytes, line count, fallback）が記録される。
- [ ] ライセンス表記（CC BY 4.0）運用がドキュメントに反映される。


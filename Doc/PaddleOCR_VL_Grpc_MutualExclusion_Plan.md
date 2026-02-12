# PaddleOCR-VL gRPC 実装案（PaddleOCR と相互排他・リソース解放）

## 1. 概要
- `OcrServiceVL` を `PaddleOCR` と同じ gRPC サーバ方式で実装する。
- `Paddle` と `PaddleVllm` は同時起動しない相互排他とし、切替時は非選択側リソースを必ず解放する。
- 既存パイプライン（`OcrResultModel` / `OcrLine` / line merge / overlay）には互換フォーマットで接続する。

## 2. ゴール / 非ゴール
### ゴール
- `OcrEngineKind.PaddleVllm` でローカル gRPC 経由の OCR 実行を可能にする。
- `Paddle` と `PaddleVllm` のランタイムを相互排他にし、切替時に非選択側ホストを停止してVRAM/メモリを解放する。
- 既存 `PaddleGrpcHost` と同等の起動・ready待機・再起動上限管理を揃える。

### 非ゴール
- 既存 `Paddle` 経路の廃止。
- 翻訳サービス側（Llama/DeepL/Gemini）の仕様変更。
- OCR後段アルゴリズム（マージ/差分）の大幅変更。

## 3. 前提・仮定
- `OcrServiceVL` は Python 実行環境（`uv` + `.venv`）を持つ。
- PaddleOCR-VL 推論結果から `text + box + confidence` へ正規化できる。
- 主要なリソース圧迫要因は Python 側モデル常駐（GPU/VRAM）であり、ホスト停止で解放できる。

## 4. 現状整理
- `Paddle` gRPC は実装済み: `OcrService/server.py`, `Services/PaddleGrpcHost.cs`, `Services/PaddleGrpcOcrProvider.cs`。
- `OcrServiceVL` は CLI 実行のみ: `OcrServiceVL/main.py`。
- `OcrEngineKind.PaddleVllm` は enum にあるが実運用経路未接続。
- ホスト常駐開始は `MainWindow.EnsureResourceHostsAsync` が担っている。

## 5. 提案アーキテクチャ
### 5.1 Python 側（OcrServiceVL）
- 追加: `OcrServiceVL/ocr_vl_engine.py`
  - `recognize(image_bytes: bytes) -> str(json)` を提供。
  - 返却JSONは既存互換: `{"lines":[{"text":"...","box":[x,y,w,h],"confidence":0.98}]}`。
- 追加: `OcrServiceVL/server.py`
  - gRPC `Health` / `Recognize` を実装。
  - `OcrService/ocr.proto` と同等契約を使用（再生成先は `OcrServiceVL`）。
- 継続: `OcrServiceVL/main.py`
  - 手動確認用CLIとして維持（内部で `ocr_vl_engine.py` を利用）。

### 5.2 C# 側
- 追加: `Services/PaddleVlGrpcHost.cs`
  - `PaddleGrpcHost` と同構造（起動・ready待機・監視・再起動上限）。
- 追加: `Services/PaddleVlGrpcOcrProvider.cs`
  - `PaddleGrpcOcrProvider` と同構造（画像bytes送信、JSON復元）。
- 変更: `Services/OcrEngine.cs`
  - `OcrEngineKind.PaddleVllm` を分岐追加。
- 変更: `MainWindow.xaml`, `MainWindow.xaml.cs`
  - OCR Engine 選択肢に `PaddleOCR-VL (gRPC)` を追加。

## 6. 相互排他とリソース解放方針（本件の中心）
### 6.1 排他ルール
- `Paddle` が選択される場合:
  - `PaddleGrpcHost` のみ起動対象。
  - `PaddleVlGrpcHost` が動作中なら停止。
- `PaddleVllm` が選択される場合:
  - `PaddleVlGrpcHost` のみ起動対象。
  - `PaddleGrpcHost` が動作中なら停止。
- `WinRt`（または将来の他OCR）が選択される場合:
  - `PaddleGrpcHost` / `PaddleVlGrpcHost` は両方停止。

### 6.2 解放対象
- Python サーバプロセス停止（モデル解放、VRAM解放）。
- gRPC チャネル解放（`GrpcChannel.Dispose()`）。
- 監視用 `CancellationTokenSource` / Task 終了。

### 6.3 実装ポイント
- `MainWindow.EnsureResourceHostsAsync` を以下に拡張:
  - `ShouldLoadPaddle(settings)`
  - `ShouldLoadPaddleVl(settings)` を新設
  - 起動前に「非選択側停止」を必ず先行実行
- `OnClosed` で両ホスト停止を保証。
- `PaddleGrpcOcrProvider` / `PaddleVlGrpcOcrProvider` は `IDisposable` 実装を追加し、アプリ終了時または切替時に明示解放できる形にする。

## 7. インターフェース設計
### 7.1 gRPC 契約
- 既存 `Protos/OcrGrpc.proto` を再利用。
- `OcrRequest`:
  - `image` を必須使用。
  - `language`, `text_detection_model_name`, `text_recognition_model_name` は VL 側では拡張余地として受け取る（未使用可）。

### 7.2 AppSettings 追加候補
- `EnablePaddleVlGrpcHost` (bool)
- `PaddleVlGrpcProjectDir` (`OcrServiceVL`)
- `PaddleVlGrpcUvPath` (`uv`)
- `PaddleVlGrpcServerScript` (`server.py`)
- `PaddleVlGrpcEndpoint` (`http://127.0.0.1:50052`)
- `PaddleVlGrpcHost` (`127.0.0.1`)
- `PaddleVlGrpcPort` (`50052`)
- `PaddleVlGrpcReadyTimeoutMs`（VLの初期化を考慮して長め）
- `PaddleVlGrpcRestartMax` / `PaddleVlGrpcRestartWindowSeconds`
- `PaddleVlDevice`, `PaddleVlPipelineVersion`, `PaddleVlMaxPixels`（必要最小限で開始）

## 8. 実装手順（段階マージ）
1. Python gRPC サーバ実装
- `OcrServiceVL` に `server.py` と `ocr_vl_engine.py` を追加。
- `pyproject.toml` に `grpcio` / `grpcio-tools` を追加。

2. C# の VL gRPC Host/Provider 実装
- `PaddleVlGrpcHost.cs` / `PaddleVlGrpcOcrProvider.cs` を追加。

3. OCRエンジン配線
- `OcrEngine.cs` で `PaddleVllm` を有効化。
- `MainWindow.xaml(.cs)` で engine 選択、保存、起動ルートを反映。

4. 相互排他と解放の確定
- `EnsureResourceHostsAsync` で「非選択側停止→選択側起動」を順序固定。
- provider dispose 呼び出しポイントを追加。

5. スモークテスト
- `Paddle` → `PaddleVllm` 切替、逆切替、`WinRt` 退避でプロセス残骸がないことを確認。

## 9. 非機能要件チェック
- 性能: 初回ロード遅延は ready timeout で吸収。再リクエストではモデル再利用。
- セキュリティ: localhost のみバインド（`127.0.0.1`）。
- 可観測性: `[PaddleGrpc]` / `[PaddleVlGrpc]` ログ分離。
- 互換性: 既存JSONフォーマット維持で後段変更を最小化。

## 10. リスクと緩和策
- Risk: 切替時に非選択側ホスト停止漏れでVRAM残留。
- Mitigation: 起動前の停止を共通関数化し、ログで停止成功/失敗を明示。

- Risk: VL 出力の box 形式揺れで後段が破綻。
- Mitigation: server 側で座標正規化を集中実装し、不正boxは除外して件数ログ出力。

- Risk: 切替直後に認識要求が来て一時失敗。
- Mitigation: ready 待機完了後に provider 呼び出しを許可し、失敗時は既存フォールバックに委譲。

## 11. 影響範囲
- 追加
  - `OcrServiceVL/server.py`
  - `OcrServiceVL/ocr_vl_engine.py`
  - `Services/PaddleVlGrpcHost.cs`
  - `Services/PaddleVlGrpcOcrProvider.cs`
- 変更
  - `OcrServiceVL/pyproject.toml`
  - `Models/AppSettings.cs`
  - `Services/OcrEngine.cs`
  - `MainWindow.xaml`
  - `MainWindow.xaml.cs`

## 12. Definition of Done
- `PaddleVllm` 選択で VL gRPC が ready になり OCR が返る。
- `Paddle` と `PaddleVllm` の同時稼働が発生しない。
- エンジン切替時に非選択側プロセスが停止し、リソース（特にVRAM）が解放される。
- `WinRt` 選択時は両Paddle系ホストが停止する。
- 既存 `Paddle` 経路の回帰がない。

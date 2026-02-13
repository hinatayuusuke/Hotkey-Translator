# PaddleOCR-VL gRPC 実装案（PaddleOCR と相互排他・リソース解放）

## 1. 概要
- `OcrServiceVL` を `PaddleOCR` と同じ gRPC サーバ方式で実装する。
- `Paddle` と `PaddleVllm` の組み合わせのみ同時起動しない相互排他とし、両者の切替時は非選択側リソースを必ず解放する。
- 既存パイプライン（`OcrResultModel` / `OcrLine` / line merge / overlay）には互換フォーマットで接続する。

## 2. ゴール / 非ゴール
### ゴール
- `OcrEngineKind.PaddleVllm` でローカル gRPC 経由の OCR 実行を可能にする。
- `Paddle` と `PaddleVllm` のランタイムを相互排他にし、切替時に非選択側ホストを停止してVRAM/メモリを解放する。
- 既存 `PaddleGrpcHost` と同等の起動・ready待機・再起動上限管理を揃える。
- 既存 `Vllm*`（HTTP直叩き）設定を廃止し、`PaddleVllm` は gRPC 経路へ一本化する。

### 非ゴール
- 既存 `Paddle` 経路の廃止。
- 翻訳サービス側（Llama/DeepL/Gemini）の仕様変更。
- OCR後段アルゴリズム（マージ/差分）の大幅変更。
- `WinRt` 選択時に Paddle 系ホストを自動停止すること（本件では要件外）。

## 3. 前提・仮定
- `OcrServiceVL` は Python 実行環境（`uv` + `.venv`）を持つ。
- PaddleOCR-VL 推論結果から `text + box + confidence` へ正規化できる。
- 主要なリソース圧迫要因は Python 側モデル常駐（GPU/VRAM）であり、ホスト停止で解放できる。

## 4. 現状整理
- `Paddle` gRPC は実装済み: `OcrService/server.py`, `Services/PaddleGrpcHost.cs`, `Services/PaddleGrpcOcrProvider.cs`。
- `OcrServiceVL` は CLI 実行のみ: `OcrServiceVL/main.py`。
- `OcrEngineKind.PaddleVllm` は enum にあるが実運用経路未接続。
- `PaddleVllm` の HTTP 経路（`Services/PaddleVllmOcrProvider.cs` + `Vllm*` 設定）が残存している。
- ホスト常駐開始は `MainWindow.EnsureResourceHostsAsync` が担っている。

## 5. 提案アーキテクチャ
### 5.1 Python 側（OcrServiceVL）
- 追加: `OcrServiceVL/ocr_vl_engine.py`
  - `recognize(image_bytes: bytes) -> str(json)` を提供。
  - 返却JSONは既存互換: `{"lines":[{"text":"...","box":[x,y,w,h],"confidence":0.98}]}`。
- 追加: `OcrServiceVL/server.py`
  - gRPC `Health` / `Recognize` を実装。
  - `Protos/OcrGrpc.proto` を契約ソースとして使用（`OcrServiceVL` 側はそこから生成）。
- 継続: `OcrServiceVL/main.py`
  - 手動確認用CLIとして維持（内部で `ocr_vl_engine.py` を利用）。

### 5.2 C# 側
- 追加: `Services/PaddleVlGrpcHost.cs`
  - `PaddleGrpcHost` と同構造（起動・ready待機・監視・再起動上限）。
- 追加: `Services/PaddleVlGrpcOcrProvider.cs`
  - `PaddleGrpcOcrProvider` と同構造（画像bytes送信、JSON復元）。
- 変更: `Services/OcrEngine.cs`
  - `OcrEngineKind.PaddleVllm` を gRPC provider 分岐で有効化。
  - 既存 `PaddleVllmOcrProvider`（HTTP）参照を撤去。
- 変更: `MainWindow.xaml`, `MainWindow.xaml.cs`
  - OCR Engine 選択肢に `PaddleOCR-VL (gRPC)` を追加。
  - OCR注意文言を現仕様に合わせて更新（「次回起動で解放」固定文言を見直し）。
  - Settings に `PaddleOCR-VL` タブを新設し、OCR関連のUI露出項目を当該タブへ集約する。
  - ホスト制御UIを追加:
    - 主操作: `設定を反映して再起動`（保存→非選択側停止→選択側起動）
    - 副操作: `停止`（`PaddleOCR-VL` ホストが生存中のときのみ停止。それ以外は no-op）

## 6. 相互排他とリソース解放方針（本件の中心）
### 6.1 排他ルール
- `Paddle` が選択される場合:
  - `PaddleGrpcHost` のみ起動対象。
  - `PaddleVlGrpcHost` が動作中なら停止。
- `PaddleVllm` が選択される場合:
  - `PaddleVlGrpcHost` のみ起動対象。
  - `PaddleGrpcHost` が動作中なら停止。
- `WinRt`（または将来の他OCR）が選択される場合:
  - 本件では Paddle 系ホスト停止を強制しない（現状挙動維持）。
  - 排他保証は `Paddle` と `PaddleVllm` の相互切替時のみを対象とする。

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
- `PaddleGrpcOcrProvider` / `PaddleVlGrpcOcrProvider` は `IDisposable` 実装を追加する。
  - 解放責務は `OcrEngine : IDisposable` に集約し、`MainWindow.OnClosed` で `OcrEngine.Dispose()` を必ず呼ぶ。
  - これにより gRPC `GrpcChannel` 解放を終了時に確実化する。
- ホスト再起動導線を追加する。
  - `設定を反映して再起動` 押下時は、`SaveSettingsAsync` を先行し最新値を確定してからホスト再起動する。
  - 実行中（`_runInProgress == 1`）は再起動/停止ボタンを無効化する。
  - 再起動処理は `resourceLoadGate` 配下で直列化し、同時操作競合を防ぐ。

## 7. インターフェース設計
### 7.1 gRPC 契約
- `Protos/OcrGrpc.proto` を唯一の契約ソース（SoT）として再利用。
- `OcrService/ocr.proto` と `OcrServiceVL/ocr.proto` は `Protos/OcrGrpc.proto` から生成する運用に統一する。
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
- `PaddleVlMaxNewTokens`（UIは 512～4096 で入力制限、未指定は `AUTO(None)`）
- 廃止（互換移行対象）
  - `VllmBaseUrl`
  - `VllmModelName`
  - `VllmApiKey` / `VllmApiKeyProtected`
  - `Services/PaddleVllmOcrProvider.cs` の参照
  - 旧設定が残っていても起動時は無視し、保存時に削除/空化して収束させる。

### 7.3 UI露出パラメーター方針（PaddleOCR-VLタブ集約）
- UI露出項目はすべて `Settings > PaddleOCR-VL` タブに集約する。
- 公開UI（通常運用で触る項目）
  - Core(PaddleOCR): `text_det_thresh`, `text_det_box_thresh`, `text_det_unclip_ratio`, `text_rec_score_thresh`
  - VL(PaddleOCR-VL): `pipeline_version`, `max_pixels`, `layout_threshold`, `max_new_tokens`
  - `max_new_tokens` は 512～4096 のレンジ制限で露出する（UI入力時とPython引数生成時の二重クランプ）。
  - `max_new_tokens` 未指定時は `AUTO` 扱いとし、Python 側へは `None` を渡して内部既定を利用する。
- 非公開（Settings.json でのみ調整）
  - VL推論挙動: `merge_layout_blocks`, `use_ocr_for_image_block`, `use_layout_detection`
  - 性能系: `enable_hpi`, `use_tensorrt`, `precision`
- UI非対象（実行系/CLI用途）
  - `input_image`, `save_dir`（CLI専用）
  - `device` は直接露出せず「Auto」を既定とする（Core/VLで既定差が大きく、誤設定リスクが高い）。
- 競合回避ルール
  - 既存 `EnablePaddleConfidenceFilter` / `PaddleConfidenceThreshold` と `text_rec_score_thresh` は役割が近いため、二重調整を避ける。
  - 実装時は「どちらか一系統に統一」する（推奨: `text_rec_score_thresh` 側）。

## 8. 実装手順（段階マージ）
1. Python gRPC サーバ実装
- `OcrServiceVL` に `server.py` と `ocr_vl_engine.py` を追加。
- `pyproject.toml` に `grpcio` / `grpcio-tools` を追加。

2. C# の VL gRPC Host/Provider 実装
- `PaddleVlGrpcHost.cs` / `PaddleVlGrpcOcrProvider.cs` を追加。

3. OCRエンジン配線
- `OcrEngine.cs` で `PaddleVllm` を有効化。
- `MainWindow.xaml(.cs)` で engine 選択、保存、起動ルートを反映。
- `Vllm*` 設定読み書きと HTTP provider 参照を撤去（設定移行を実施）。

4. 相互排他と解放の確定
- `EnsureResourceHostsAsync` で「非選択側停止→選択側起動」を順序固定。
- `OcrEngine.Dispose()` を実装し、`MainWindow.OnClosed` から呼ぶ。

5. ホスト制御UI（停止/再起動）追加
- `設定を反映して再起動` ボタンを追加し、保存後リスタート経路を実装。
- `停止` ボタンを追加し、`PaddleOCR-VL` ホスト生存時のみ停止（それ以外は no-op）を実装。
- 実行中無効化・失敗時ログ表示を実装。
- `PaddleOCR-VL` タブを追加し、公開UI項目を同タブへ集約する。
- `max_new_tokens` 入力制限（512～4096）を実装する。

6. スモークテスト
- `Paddle` → `PaddleVllm` 切替で、`PaddleGrpcHost` 停止 / `PaddleVlGrpcHost` 起動を確認。
- `PaddleVllm` → `Paddle` 切替で、`PaddleVlGrpcHost` 停止 / `PaddleGrpcHost` 起動を確認。
- `WinRt` 切替時は Paddle 系ホスト停止を期待しない（非要件）ことを確認。
- 設定変更後に `設定を反映して再起動` で反映されることを確認。
- `停止` 実行で当該ホストのみ停止し、他系統へ不要な影響がないことを確認。
- `max_new_tokens` が 512 未満/4096 超で入力されてもクランプされることを確認。
- `max_new_tokens` 未指定時に `AUTO(None)` として動作し、内部既定が使われることを確認。
- `停止` は `PaddleOCR-VL` 非稼働時に no-op であることを確認。

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

- Risk: 設定保存前に再起動すると、ユーザー期待と異なる構成で起動する。
- Mitigation: `設定を反映して再起動` で保存を必須化し、未保存再起動経路を作らない。

## 11. 影響範囲
- 追加
  - `OcrServiceVL/server.py`
  - `OcrServiceVL/ocr_vl_engine.py`
  - `Services/PaddleVlGrpcHost.cs`
  - `Services/PaddleVlGrpcOcrProvider.cs`
- 変更
  - `Protos/OcrGrpc.proto`（必要に応じた契約追記時のみ）
  - `OcrService/ocr.proto`（生成/同期運用へ変更）
  - `OcrServiceVL/ocr.proto`（生成/同期運用へ変更）
  - `OcrServiceVL/pyproject.toml`
  - `Models/AppSettings.cs`
  - `Services/OcrEngine.cs`
  - `MainWindow.xaml`
  - `MainWindow.xaml.cs`

## 12. Definition of Done
- `PaddleVllm` 選択で VL gRPC が ready になり OCR が返る。
- `Paddle` と `PaddleVllm` の同時稼働が発生しない。
- エンジン切替時に非選択側プロセスが停止し、リソース（特にVRAM）が解放される。
- `Vllm*`（HTTP直叩き）設定と provider 参照が撤去され、gRPC 経路へ一本化される。
- `WinRt` 選択時に Paddle 系ホスト停止を強制しないことが仕様として明文化されている。
- 既存 `Paddle` 経路の回帰がない。
- `設定を反映して再起動` で最新設定がホストに反映される。
- `停止` で対象ホストが停止し、再起動操作まで常駐しない。
- UI露出項目が `PaddleOCR-VL` タブへ集約されている。
- `max_new_tokens` が 512～4096 の範囲制約で運用される。
- `max_new_tokens` 未指定時は `AUTO(None)` として運用される。
- `停止` は `PaddleOCR-VL` 非稼働時に no-op である。

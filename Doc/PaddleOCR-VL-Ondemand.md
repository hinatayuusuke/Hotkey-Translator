# PaddleOCR-VL オンデマンド導入案 (uv 前提)

## 目的
- `PaddleOCR-VL (vLLM)` を初回利用時に必要な環境だけ自動準備する
- 既存の WinRT / PaddleOCR には影響を与えず、失敗時は即フォールバックする
- vLLM と PaddlePaddle の依存衝突を避けるため、専用環境を分離する

## 前提
- uv が利用可能であること (パス指定できる前提)
- vLLM は GPU 環境推奨。CPU では性能が大きく低下する
- vLLM サーバーはローカルまたはリモートのどちらでも良い

## 方針
- vLLM サーバーは「オンデマンドでローカル起動」も「既存サーバー接続」も許容する
- ローカル起動時は `Tools/PaddleOcrVllm/` に専用 venv を作成して固定運用する
- 導入は「初回 OCR 実行時に必要な場合のみ」実行する

## 導入トリガー
- OCR エンジンが `PaddleOCR-VL (vLLM)` に設定されている
- `VllmBaseUrl` に応答がなく、かつ「ローカル導入を許可」されている

## オンデマンド導入フロー (案)
1) vLLM サーバー疎通確認
   - `GET {VllmBaseUrl}/models` で 200 応答が返れば導入不要
2) vLLM 導入判定
   - `Tools/PaddleOcrVllm/` 配下に venv が存在するか確認
   - 存在しなければ venv 作成 → 依存導入
3) vLLM インストール (uv)
   - `uv venv`
   - `uv pip install -U vllm --pre --extra-index-url https://wheels.vllm.ai/nightly --extra-index-url https://download.pytorch.org/whl/cu129 --index-strategy unsafe-best-match`
4) vLLM 起動
   - `uv run vllm serve PaddlePaddle/PaddleOCR-VL --trust-remote-code --max-num-batched-tokens 16384 --no-enable-prefix-caching --mm-processor-cache-gb 0`
   - 起動ログを別ウィンドウまたはアプリログへ転送
5) 起動完了待ち
   - `/v1/models` または `/v1/health` をポーリングし、準備完了を検知
6) OCR 実行
   - `chat.completions` で image_url + OCR プロンプトを送信

## 失敗時の扱い
- インストール/起動/疎通のいずれかが失敗した場合は WinRT に即フォールバック
- エラー理由はログに明示し、次回は再試行可能にする

## 推奨設定
- `VllmBaseUrl`: `http://localhost:8000/v1`
- `VllmModelName`: `PaddlePaddle/PaddleOCR-VL`
- API キーが必要な環境では `VllmApiKey` を使用 (DPAPI で保護)

## 運用メモ
- vLLM と PaddlePaddle の同居は避け、vLLM 専用環境を分離
- 依存更新は「手動更新」または「一定期間ごとの更新」に限定し、オンデマンドで常に更新しない
- ローカル導入を避けたい場合は、`VllmBaseUrl` をリモートに設定し導入処理を無効化する

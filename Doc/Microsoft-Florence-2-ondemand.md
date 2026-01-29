# Microsoft Florence-2 オンデマンド導入案 (Python/uv 前提)

## 目的
- Florence-2 (Large/Base) を初回利用時に必要な環境だけ自動準備する
- 既存の WinRT / Paddle / vLLM には影響を与えず、失敗時は即フォールバックする
- Python 依存は専用 venv に分離して運用する

## 前提
- uv が利用可能 (パス指定できる想定)
- モデル取得には Hugging Face へのアクセスが必要
- GPU が無い場合は大幅に遅くなるため、CPU 実行は最小限の用途に限定する

## 方針
- ローカル推論を 기본とし、必要なときだけ venv と依存を導入する
- モデルは `Large` / `Base` を設定で切替える
- オンデマンド導入が失敗したら WinRT にフォールバックする

## 導入トリガー
- OCR エンジンが `Florence-2 (Large/Base)` に設定されている
- ローカル環境が未準備、またはモデルが未取得

## オンデマンド導入フロー (案)
1) venv 確認
   - `Tools/Florence2/` に venv が存在するか確認
2) venv 作成 (未作成の場合)
   - `uv venv`
3) 依存導入
   - `uv pip install -U transformers accelerate safetensors pillow`
   - GPU 版 PyTorch を使う場合は環境に合わせて追加
     - 例: `uv pip install torch --index-url https://download.pytorch.org/whl/cu121`
4) モデル取得
   - `microsoft/Florence-2-large` または `microsoft/Florence-2-base` を取得
   - 事前に `HF_HOME` を設定し、キャッシュを固定するのが望ましい
5) OCR 実行
   - 画像を PNG に保存し、Python ブリッジを起動
   - JSON 形式で `lines` を返す (box は入力画像のピクセル座標)

## Python ブリッジ案
- `Tools/Florence2/florence_ocr_bridge.py` を追加
- 入力:
  - `--image` PNG パス
  - `--model` (large/base)
  - `--device` (cpu/cuda)
- 出力:
  ```json
  {
    "lines": [
      {"text": "...", "box": [x, y, w, h], "confidence": 0.98}
    ]
  }
  ```
- 失敗時は標準エラーに理由を出し、アプリ側でフォールバック

## 失敗時の扱い
- 依存導入失敗: WinRT へ即フォールバック
- モデル取得失敗: WinRT へ即フォールバック
- 推論失敗/タイムアウト: WinRT へ即フォールバック
- 失敗理由はログに明示し、次回は再試行可能にする

## 推奨設定
- `FlorenceBaseUrl` を使わずローカル推論専用にする場合
  - `FlorenceModelName`: `microsoft/Florence-2-large` or `microsoft/Florence-2-base`
  - `FlorenceDevice`: `cuda` (GPU) / `cpu` (フォールバック)

## 運用メモ
- venv とモデルキャッシュは別ディレクトリで固定する
- 依存更新は「手動更新」または「一定期間ごとの更新」に限定する
- 初回導入は時間がかかるため、UI に進捗表示を出すと良い

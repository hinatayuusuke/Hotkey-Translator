# PaddleOCR 導入手順 (uv)

このドキュメントは `Doc/Plan4.md` の想定に沿った PaddleOCR の導入手順です。

## 1) 前提
- Windows 10/11
- `.NET 8` アプリ (Hotkey-Translator)
- uv が利用可能であること

## 2) uv のインストール
以下のいずれかの方法で uv をインストールします。

### 2.1 winget
```powershell
winget install --id AstralSoftware.Uv -e
```

### 2.2 公式インストーラ
```powershell
irm https://astral.sh/uv/install.ps1 | iex
```

インストール後、`uv --version` が通ることを確認してください。

## 3) プロジェクト配置
`Tools/PaddleOcr` を作成し、以下の構成を置きます。

```
Tools/PaddleOcr/
  pyproject.toml
  paddle_ocr_bridge.py
```

## 4) pyproject.toml の例
`Tools/PaddleOcr/pyproject.toml` を作成します。

```toml
[project]
name = "paddle-ocr-bridge"
version = "0.1.0"
requires-python = ">=3.10"
dependencies = [
  "paddleocr",
  "paddlepaddle"
]
```

GPU 版を使う場合は `paddlepaddle-gpu` を使用してください。

## 5) ブリッジスクリプト
`Tools/PaddleOcr/paddle_ocr_bridge.py` を用意します。
- 入力: 画像パス / 言語 / device / model_dir
- 出力: JSON (lines 配列)

`Plan4` で定義した I/F に合わせてください。

## 6) 依存の取得
`Tools/PaddleOcr` で `uv sync` を実行します。

```powershell
cd Tools/PaddleOcr
uv sync
```

## 7) アプリ側設定
アプリ設定で以下を指定します。

- `PaddleProjectDir`: `Tools\PaddleOcr`
- `PaddleUvPath`: `uv` (PATH が通っていない場合はフルパス)
- `PaddleLanguage`: `japan` / `en` など
- `PaddleDevice`: `cpu` (GPU が使える場合は `gpu`)
- `PaddleModelDir`: 必要に応じて指定

OCR エンジンを `PaddleOCR` に切り替えて保存します。

## 8) 動作確認
以下を確認します。
- OCR が Paddle で動作する
- Paddle が失敗した場合は WinRT にフォールバックする
- Overlay の座標が一致している

## 9) トラブルシュート
- `uv` が見つからない場合: `PaddleUvPath` にフルパス指定
- import error: `uv sync` を再実行
- GPU エラー: `PaddleDevice=cpu` に切り替える

# OneOCR Experiment

## Overview

Snipping Tool の private OCR 実装を独立 CLI として検証するための実験ディレクトリです。
`uv` で Python 環境を固定し、画像 1 枚から `text + bbox + confidence` を JSON として取り出します。

## Files

- `probe.py`
  - CLI エントリポイントです。
- `oneocr_bridge.py`
  - `ctypes` で `oneocr.dll` を薄くラップします。
- `vendor/`
  - `oneocr.dll`、`oneocr.onemodel`、`onnxruntime.dll` を手動配置します。
- `input/`
  - テスト画像を置きます。
- `output/`
  - JSON と bbox 可視化画像を出力します。

## Setup

1. `vendor/` に以下を配置します。
   - `oneocr.dll`
   - `oneocr.onemodel`
   - `onnxruntime.dll`
2. 依存を同期します。

```powershell
.\Tools\uv\uv.exe sync --project .\Tools\OneOcrExperiment
```

## Usage

```powershell
.\Tools\uv\uv.exe run --project .\Tools\OneOcrExperiment python .\Tools\OneOcrExperiment\probe.py --image .\Tools\OneOcrExperiment\input\sample.png --out-json .\Tools\OneOcrExperiment\output\sample.json --dump-overlay .\Tools\OneOcrExperiment\output\sample.overlay.png --pretty
```

### Arguments

- `--image <path>`
  - 入力画像です。PNG / JPG を想定しています。
- `--out-json <path>`
  - JSON の保存先です。省略時は標準出力へ出します。
- `--dump-overlay <path>`
  - line / word polygon を重ねた確認画像を出します。
- `--vendor-dir <path>`
  - private DLL 一式の配置先です。既定値は `vendor/` です。
- `--max-line-count <n>`
  - OneOCR へ渡す最大認識行数です。既定値は `1000` です。
- `--pretty`
  - JSON を整形して出力します。

## Output Shape

```json
{
  "engine": "OneOCR",
  "durationMs": 0.0,
  "imagePath": "C:/path/to/sample.png",
  "fullText": "",
  "imageAngle": 0.0,
  "imageWidth": 1920,
  "imageHeight": 1080,
  "lines": [
    {
      "text": "",
      "bbox": [0.0, 0.0, 0.0, 0.0],
      "polygon": [[0.0, 0.0], [0.0, 0.0], [0.0, 0.0], [0.0, 0.0]],
      "words": []
    }
  ],
  "words": [
    {
      "text": "",
      "confidence": 0.0,
      "bbox": [0.0, 0.0, 0.0, 0.0],
      "polygon": [[0.0, 0.0], [0.0, 0.0], [0.0, 0.0], [0.0, 0.0]]
    }
  ]
}
```

`bbox` は `[left, top, width, height]` です。

## Notes

- `AuroraWright/oneocr` の Python 実装を参考に exported function の並びを定義しています。
- `b1tg/win11-oneocr` に合わせて、DLL へ渡す画素形式は BGRA を使います。
- geometry が取れない場合は PoC 成立条件を満たさないため、成功扱いにしません。
- private 実装依存なので、Snipping Tool 更新で壊れる可能性があります。

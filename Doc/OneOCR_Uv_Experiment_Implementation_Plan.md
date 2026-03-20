# OneOCR UV Experiment Implementation Plan

## 1. Overview
Snipping Tool 系 OneOCR を private 実装のまま本体へ混ぜる前に、`uv` で自己完結する独立 PoC を用意し、画像 1 枚から `text + bbox + confidence` を抽出できるかを確認する。
PoC は `Tools/OneOcrExperiment` 配下に閉じ、本体アプリや既存 OCR エンジン設定には一切触れない。

## 2. Goal / Non-Goal
### Goal
- `uv` を使って Python 実験環境を固定し、端末差分を減らす。
- 単一画像入力から `fullText`、`lines`、`words`、`confidence`、`bbox` を JSON で出力する。
- 後続で `Hotkey-Translator` から別プロセス呼び出ししやすい CLI 契約を先に固める。

### Non-Goal
- `Hotkey-Translator` 本体への統合。
- Snipping Tool 依存ファイルの自動抽出、自動更新、再配布。
- `.onemodel` の解析や ONNX 再実装。
- 汎用 OCR フレームワーク化や複数バックエンド対応。

## 3. Scope
- 対象ディレクトリ: `Tools/OneOcrExperiment`
- 実行方式: `Tools/uv/uv.exe run`
- 対象入力: 単一の PNG / JPG 画像
- 対象出力: OCR 結果 JSON、任意の bbox 可視化画像

## 4. Current State
- 現在の本体 OCR には `Windows.Media.Ocr.OcrEngine` を使う WinRT 経路があり、Snipping Tool の体感精度とは差がある。
- 実験用ディレクトリ `Tools/OneOcrExperiment` と初期 README は既に追加済みで、`input/`、`output/`、`vendor/` の役割も整理済みである。
- リポジトリには `Tools/uv/uv.exe` が存在するため、PoC 側はシステム Python ではなく `uv` 起点で統一できる。
- コミュニティ実装では `Python + ctypes` が最短経路であり、まずはこれを採用するのが最も検証効率が高い。

## 5. Proposed Design
### Components
- `Tools/OneOcrExperiment/pyproject.toml`
  - `uv` で依存解決するための最小プロジェクト定義。
- `Tools/OneOcrExperiment/probe.py`
  - CLI エントリポイント。画像読込、OneOCR 呼び出し、JSON 出力を担当する。
- `Tools/OneOcrExperiment/oneocr_bridge.py`
  - `ctypes` で `oneocr.dll` をロードし、必要な関数だけを薄くラップする。
- `Tools/OneOcrExperiment/vendor/`
  - `oneocr.dll`、`oneocr.onemodel`、`onnxruntime.dll` を手動配置する。
- `Tools/OneOcrExperiment/input/`
  - テスト画像置き場。
- `Tools/OneOcrExperiment/output/`
  - JSON とデバッグ成果物の出力先。

### Flow
1. `uv run python .\probe.py --image <path>` を実行する。
2. `probe.py` が `vendor/` の必須ファイル存在を検証する。
3. `oneocr_bridge.py` が `oneocr.dll` をロードし、OCR パイプラインを初期化する。
4. 画像を OneOCR が要求する画素形式へ変換し、OCR 実行関数へ渡す。
5. 結果から行単位・語単位のテキスト、信頼度、矩形を取得して JSON 化する。
6. `--dump-overlay` 指定時だけ bbox を描いた確認画像を `output/` に保存する。

## 6. Interface Design
### CLI
```powershell
.\Tools\uv\uv.exe run --project .\Tools\OneOcrExperiment python .\Tools\OneOcrExperiment\probe.py --image .\Tools\OneOcrExperiment\input\sample.png --out-json .\Tools\OneOcrExperiment\output\sample.json
```

### Arguments
- `--image <path>`
  - 必須。単一画像を指定する。
- `--out-json <path>`
  - 任意。未指定時は標準出力へ JSON を出す。
- `--dump-overlay <path>`
  - 任意。bbox 描画画像を保存する。
- `--pretty`
  - 任意。JSON を整形して出力する。

### Output Schema
```json
{
  "engine": "OneOCR",
  "durationMs": 0,
  "imageWidth": 0,
  "imageHeight": 0,
  "fullText": "",
  "lines": [
    {
      "text": "",
      "bbox": [0, 0, 0, 0]
    }
  ],
  "words": [
    {
      "text": "",
      "confidence": 0.0,
      "bbox": [0, 0, 0, 0]
    }
  ]
}
```

### Error Policy
- `vendor/` に必要ファイルが無い場合は即失敗する。
- private API 呼び出し失敗時は握りつぶさず、終了コード非 0 で落とす。
- geometry が取得できない場合は暫定成功扱いにせず、PoC 不成立として扱う。
- 自動フォールバックは入れない。Fail fast を維持する。

## 7. UV Strategy
### Why UV
- システム Python や手動 `pip install` に依存すると、再現性より「たまたま動く」状態になりやすい。
- この PoC は private DLL 依存で壊れやすいため、少なくとも Python 側の実行環境は固定したほうが切り分けが速い。

### Project Layout
- `pyproject.toml`
  - `project.name = "oneocr-experiment"`
  - `project.requires-python = ">=3.11,<3.13"` を第一候補にする。
  - 依存は最小化し、初期候補は `Pillow` のみとする。
- `.python-version`
  - 必須ではないが、必要なら `3.11` 固定で追加する。

### Commands
- 初回同期:
```powershell
.\Tools\uv\uv.exe sync --project .\Tools\OneOcrExperiment
```

- 実行:
```powershell
.\Tools\uv\uv.exe run --project .\Tools\OneOcrExperiment python .\Tools\OneOcrExperiment\probe.py --image .\Tools\OneOcrExperiment\input\sample.png
```

- 依存追加:
```powershell
.\Tools\uv\uv.exe add --project .\Tools\OneOcrExperiment pillow
```

### Dependency Policy
- 初期依存は `Pillow` だけにする。
- `numpy` や `opencv-python` は必要になった時点で追加する。予防的には入れない。
- コミュニティ `oneocr` パッケージをそのまま依存に置くより、必要最小限の bridge を自前管理する。
  - WHY: 実験の主目的は private DLL の実機検証であり、外部ラッパー更新の影響を受けにくくするため。

## 8. Implementation Steps
1. `Doc/OneOCR_Uv_Experiment_Implementation_Plan.md` を追加し、PoC の前提と実行契約を固定する。
2. `Tools/OneOcrExperiment/pyproject.toml` を追加し、`uv` 実行環境を定義する。
3. `probe.py` に CLI を実装し、画像入力と JSON 出力だけを先に通す。
4. `oneocr_bridge.py` を追加し、`ctypes` 経由で OneOCR の最小関数だけをラップする。
5. 数枚の画像で `durationMs`、語数、bbox 妥当性を確認する。
6. 結果が良ければ、その時点で初めて本体から別プロセス呼び出しする統合案を起こす。

## 9. Non-Functional Considerations
- Performance: 1080p 前後のスクリーンショット 1 枚を体感 1 秒前後で処理できるかを確認する。
- Security: private DLL を扱うため、Git へ同梱しない。実験端末ローカルに限定する。
- Observability: `durationMs`、画像サイズ、行数、語数、失敗理由は必ず取得できる形にする。
- Compatibility: Snipping Tool 更新で壊れる前提なので、PoC は端末依存・バージョン依存の検証として扱う。
- Operations: `uv sync` と `uv run` だけで再現できる状態を目標にし、手順の分岐を増やさない。

## 10. Risk / Mitigation
- Risk: `ctypes` の関数定義がズレるとクラッシュし、原因が追いにくい。
- Mitigation: bridge は最小関数だけに限定し、引数型と戻り値型を段階的に確定する。

- Risk: OCR 結果の中に confidence や bbox が想定通り含まれない可能性がある。
- Mitigation: Step 1 の成功条件を「文字列抽出」ではなく「geometry を含む JSON 取得」に置く。

- Risk: `uv` 管理外で Python を直接実行され、再現性が崩れる。
- Mitigation: README と Doc のコマンド例をすべて `.\Tools\uv\uv.exe` 起点で統一する。

- Risk: 依存ライブラリを増やしすぎて PoC の切り分けが難しくなる。
- Mitigation: 初期依存は `Pillow` のみとし、追加は実際に必要になった時だけ行う。

## 11. Impact
- 変更候補は `Doc/` と `Tools/OneOcrExperiment/` に限定する。
- 本体の C# コード、既存 OCR 列挙、配布パッケージ構成は今回のスコープ外とする。
- 将来統合する場合でも、まずは「外部プロセス OCR」として足す前提で考える。

## 12. Definition of Done
- `uv` 前提の実験構成とコマンドが Doc に明記されている。
- `pyproject.toml`、`probe.py`、`oneocr_bridge.py` の責務分割が決まっている。
- 出力 JSON に `text + bbox + confidence` を含める方針が固定されている。
- 自動抽出・自動フォールバックを入れない前提が明文化されている。
- 次の実装作業が `uv sync` と `probe.py` 実装から開始できる状態になっている。

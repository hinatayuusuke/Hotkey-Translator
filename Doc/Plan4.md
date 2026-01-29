# Plan4: PaddleOCR 追加 (uv運用前提)

## Goal
- Windows OCR (WinRT) に加えて PaddleOCR を選択可能にする
- Paddle が使えない環境では安全に WinRT へフォールバックする
- 既存のパイプライン (Capture -> OCR -> Diff -> Translate -> Overlay) を崩さずに追加する

## 前提
- アプリは WPF/.NET 8
- 現在の OCR は `Services/OcrEngine.cs` が WinRT OCR を担当
- 設定は `SettingsService` + `AppSettings` に保存
- Python 側は uv を使って依存管理する

## 方針 (推奨)
- .NET 側は OCR プロバイダ抽象化を入れて選択/フォールバックを一元化する
- PaddleOCR は uv の project ディレクトリを用意し、`uv run` で呼び出す
- まずは「プロセス起動型」で実装し、性能が問題なら常駐化へ拡張する

## 変更点 (設計)

### 1) Settings
`Models/AppSettings.cs` に OCR エンジン選択と Paddle 設定を追加する。

- 例:
  - `public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.WinRt;`
  - `public string PaddleProjectDir { get; set; } = "Tools\\PaddleOcr";`
  - `public string PaddleUvPath { get; set; } = "uv";`
  - `public string PaddleLanguage { get; set; } = "japan";`
  - `public string PaddleDevice { get; set; } = "cpu";`
  - `public string? PaddleModelDir { get; set; }`

### 2) UI
- OCR エンジン選択用 ComboBox
  - `WinRT (Windows OCR)` / `PaddleOCR`
- Paddle 設定入力 (最小限)
  - ProjectDir / Language / Device
  - (任意) UvPath / ModelDir

### 3) OCR サービスの抽象化
- `IOcrProvider` を追加
  ```csharp
  Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken ct);
  ```
- 既存 WinRT 実装を `WinRtOcrProvider` として切り出し
- `PaddleOcrProvider` を追加
- `OcrEngine` は選択/フォールバック担当に変更
  - `settings.OcrEngine == Paddle` の場合は Paddle -> 失敗時は WinRT
  - ログで「選択 / 失敗 / フォールバック」を明示

### 4) PaddleOCR 実行方式 (uv)
- uv プロジェクトディレクトリを用意
  - `Tools/PaddleOcr/pyproject.toml`
  - `Tools/PaddleOcr/paddle_ocr_bridge.py`
- .NET から `uv run` で実行
  - 例: `uv run --project <dir> python paddle_ocr_bridge.py --image <path> --lang <lang> --device <cpu|gpu> --model <path>`
- 画像の受け渡しは一時ファイル方式で開始 (安定性優先)
  - 後で stdin/base64 へ移行可能

### 5) Python ブリッジ仕様
- 入力
  - 画像パス + 言語 + device + model_dir
- 出力 (JSON)
  ```json
  {
    "lines": [
      {"text": "...", "box": [x, y, w, h], "confidence": 0.98}
    ]
  }
  ```
- .NET 側で `OcrLine` へ変換
  - `Rect` は画像ピクセル座標
  - `OcrResultModel.PixelWidth/Height` は画像サイズ

### 6) ログ
- 使用エンジンの選択ログ
- Paddle 実行失敗時の stderr と exit code を記録
- フォールバック発動時の理由をログ出力

## uv セットアップ手順 (運用想定)
1. uv をインストール
2. `Tools/PaddleOcr` に project を用意
3. 例: `pyproject.toml`
   ```toml
   [project]
   name = "paddle-ocr-bridge"
   version = "0.1.0"
   dependencies = [
     "paddleocr",
     "paddlepaddle"
   ]
   ```
4. `uv sync` で依存を取得
5. アプリ設定で `PaddleProjectDir` を指定

## 互換性
- 既存ユーザーは `OcrEngine=WinRt` のまま動作
- 新規設定は未指定でも既定値で安全に動作

## リスクと対策
- リスク: Paddle 環境が未構築で失敗
  - 対策: WinRT へフォールバック + 明示ログ
- リスク: Python 起動コスト
  - 対策: まずは起動型、後で常駐化
- リスク: GPU 依存
  - 対策: `PaddleDevice=cpu` を既定値に

## テスト観点
- WinRT と Paddle の切替が機能する
- Paddle 不在時に WinRT へフォールバックする
- OCR 座標が overlay と一致する
- 大きい画像でも OCR/パイプラインが破綻しない

## 実装順 (推奨)
1. `OcrEngineKind` と Settings/UI 追加
2. `IOcrProvider` と `WinRtOcrProvider` への切り出し
3. `PaddleOcrProvider` (uv run + 一時ファイル)
4. フォールバックとログ整備
5. 余力があれば常駐プロセス化
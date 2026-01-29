# Microsoft Florence-2 (Large/Base) 追加実装案

## Goal
- OCR エンジンに Microsoft Florence-2 (Large/Base) を追加する
- 既存パイプライン (`CaptureManager` -> ROI -> `OcrEngine` -> `IOcrProvider`) に統合する
- 失敗時は既存の WinRT/Paddle にフォールバックし、パイプラインを止めない

## 前提
- Florence-2 は VLM 系モデルのため、OCR はローカル推論で提供する
- 既存の `OcrEngine` はエンジン種別による分岐と例外フォールバックを採用している
- OCR 座標は「入力画像ピクセル基準」で `OcrLine.Rect` に格納し、最終的に ROI オフセットで画面座標へ戻す

## 方針 (推奨)
- OCR エンジン種別に `Florence2` を追加
- **Python ブリッジ (uv) 経由のローカル推論**を前提にする
- モデルサイズは `Large` / `Base` を設定で切替える
- 依存衝突を避けるため専用 venv を分離する

## 変更点 (設計)

### 1) Settings 追加
`Models/AppSettings.cs` に Florence-2 用設定を追加する。

- 例:
  - `public OcrEngineKind OcrEngine { get; set; }` (既存)
  - `public string FlorenceModelName { get; set; } = "microsoft/Florence-2-large";`
  - `public string FlorenceProjectDir { get; set; } = "Tools\\Florence2";`
  - `public string FlorenceUvPath { get; set; } = "uv";`
  - `public string FlorenceDevice { get; set; } = "cuda";`
  - `public string? FlorenceModelDir { get; set; } = null;` (任意: ローカルモデル指定)

※ API Key は不要なため DPAPI 保護は不要。

### 2) UI
- OCR エンジン選択に `Florence-2 (Large/Base)` を追加
- Florence ローカル設定 UI を追加
  - ProjectDir / uv path / ModelName / Device / ModelDir
- モデル名の候補例:
  - `microsoft/Florence-2-large`
  - `microsoft/Florence-2-base`

### 3) Provider 追加
- `FlorenceOcrProvider` を追加
  ```csharp
  Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken ct);
  ```
- Python ブリッジ (例: `florence_ocr_bridge.py`) を uv で実行する
  - 画像を一時 PNG に保存して渡す
- JSON 文字列を受け取り、`OcrLine` 配列に変換
  - `Rect` は「入力 Bitmap のピクセル座標」
  - `LineHeight` は `rect.Height` を採用

### 4) ブリッジの入出力設計
- ブリッジは OCR 形式で返す (JSONのみ)
- 例:
  ```json
  {
    "lines": [
      {"text": "...", "box": [x, y, w, h], "confidence": 0.98}
    ]
  }
  ```
- 解析不能な出力はログに残し WinRT へフォールバック

### 5) 画像転送
- 画像を一時ファイルに保存し、Python ブリッジにパスで渡す
- 処理後は必ず削除する

### 6) フォールバック
- `OcrEngineKind.Florence2` 指定時に失敗したら WinRT へフォールバック
- 失敗理由をログで明示

## リスクと対策
- リスク: 返却 JSON の揺れ (座標欠落/空文字)
  - 対策: JSON パース失敗時はフォールバック
- リスク: 依存衝突や環境差異
  - 対策: 専用 venv を固定し、`ProjectDir` を分離
- リスク: 応答遅延 (GPU 未搭載時)
  - 対策: タイムアウト設定 + キャンセル対応

## テスト観点
- Florence-2 有効時に OCR 結果が取得できる
- Florence-2 無効/失敗時に WinRT へフォールバックする
- OCR 座標が overlay と一致する (ROI 由来の座標補正を含む)
- 大きい画像でレスポンスが安定する
 - `ModelDir` 指定時/未指定時で結果が変わらない

## 実装順 (推奨)
1) Settings/OcrEngineKind/SettingsService の追加
2) UI 追加
3) `FlorenceOcrProvider` 実装
4) `OcrEngine` での選択/フォールバック
5) テストとログ調整

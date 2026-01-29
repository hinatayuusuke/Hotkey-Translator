# PaddleOCR-VL Plan (vLLM サーバー連携)

## Goal
- PaddleOCR-VL を vLLM サーバー経由で利用できるようにする
- PaddleOCR-VL を既存 OCR パイプラインに統合し、WinRT/PaddleOCR と併存させる
- vLLM と PaddlePaddle を同一環境に入れない方針を維持する

## 前提
- vLLM サーバーは別環境 (GPU 推奨) で常駐起動
- アプリは OpenAI 互換 API を HTTP で呼び出す
- 既存コードは `CaptureManager` -> ROI 切り出し -> `OcrEngine` -> `IOcrProvider` の順で OCR 実行
- OCR 結果は `OcrLineGrouper` / `OcrDiffService` を経由し、翻訳は `TranslationFallbackService` で実施
- Overlay 描画は `OverlayPresenter` が担当し、ROI 由来の座標は最終的に画面座標へ戻す

## 方針 (推奨)
- OCR エンジン種別に `PaddleVllm` を追加
- vLLM を外部サービスとして扱い、ローカル/リモートどちらでも接続可能にする
- 失敗時は WinRT へフォールバックし、パイプラインを止めない
- ROI の Bitmap を渡す設計は維持し、vLLM 側は入力画像座標で矩形を返す

## 変更点 (設計)

### 1) Settings 追加
`Models/AppSettings.cs` に vLLM 向け設定を追加する。

- 例:
  - `public OcrEngineKind OcrEngine { get; set; }` (既存)
  - `public string VllmBaseUrl { get; set; } = "http://localhost:8000/v1";`
  - `public string VllmModelName { get; set; } = "PaddlePaddle/PaddleOCR-VL";`
  - `public string? VllmApiKey { get; set; } = null;`
  - `public string? VllmApiKeyProtected { get; set; } = null;`

`SettingsService` で API Key を DPAPI で保護して保存する (既存の Gemini/DeepL と同様の扱い)。

### 2) UI
- OCR エンジン選択に `PaddleOCR-VL (vLLM)` を追加
- vLLM 接続設定 UI を追加
  - BaseUrl / ModelName / ApiKey

### 3) Provider 追加
- `PaddleVllmOcrProvider` を追加
  ```csharp
  Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken ct);
  ```
- OpenAI 互換 `chat.completions` を呼ぶ
  - `messages` に `image_url` + OCR 指示テキストを含める
- JSON 文字列を受け取り、`OcrLine` 配列に変換
  - `Rect` は「入力 Bitmap のピクセル座標」を前提とし、`LineHeight` は `rect.Height` を採用
  - パース失敗時は例外を投げて `OcrEngine` 側フォールバックに委ねる

### 4) プロンプト/レスポンス設計
- モデルに OCR 形式で返すよう明示する
  - 出力フォーマットを固定 (JSONのみ)
- 例 (座標は入力画像のピクセル基準):
  ```json
  {
    "lines": [
      {"text": "...", "box": [x, y, w, h], "confidence": 0.98}
    ]
  }
  ```
- 解析不能な出力はログに残し WinRT へフォールバック

### 5) 画像転送
- 画像を PNG に変換して `data:image/png;base64` を利用
- vLLM 連携では `data:image` の方が安定

### 6) フォールバック
- `OcrEngineKind.PaddleVllm` 指定時に失敗したら WinRT を使う
- 失敗理由をログで明示
- `OcrEngine` は既存の Paddle/WinRT と同様の例外捕捉パターンに合わせる

## vLLM サーバーセットアップ (別環境)
- `Doc/PaddleOCR-VL.md` の手順に従い vLLM を起動
- 例:
  ```bash
  vllm serve PaddlePaddle/PaddleOCR-VL \
    --trust-remote-code \
    --max-num-batched-tokens 16384 \
    --no-enable-prefix-caching \
    --mm-processor-cache-gb 0
  ```

## リスクと対策
- リスク: 出力が JSON 以外になる
  - 対策: JSON 強制プロンプト + 解析失敗時のログ/フォールバック
- リスク: 画像転送が大きく遅い
  - 対策: PNG 圧縮・サイズ制限・必要なら解像度縮小
- リスク: vLLM の応答が遅い
  - 対策: タイムアウト設定 + キャンセル対応

## テスト観点
- vLLM が有効なとき OCR 結果が取れる
- vLLM 無効/失敗時に WinRT へフォールバックする
- OCR 座標が overlay と一致する (ROI 切り出し後のオフセット復元も含む)
- 大きい画像でレスポンスが安定する
- 既存の翻訳キャッシュ/差分判定が破綻しない

## 実装順 (推奨)
1) Settings/OcrEngineKind/SettingsService の追加
2) UI 追加
3) `PaddleVllmOcrProvider` 実装
4) `OcrEngine` での選択/フォールバック
5) テストとログ調整

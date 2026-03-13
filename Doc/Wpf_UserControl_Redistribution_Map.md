# WPF UserControl Redistribution Map

## 1. 目的
`Overview` に残っている legacy 設定を削除する前に、現状の全 `UserControl` と設定項目の持ち場を整理する。

この文書では次の 3 点を決める。

- 現在の `UserControl` 名と役割
- 現在の設定項目がどこにあるか
- 最終的にどの `UserControl` を正規 owner にするか、新規作成するか

## 2. 対象範囲
対象は設定コンソールの `UserControl` と、まだ `MainWindow.xaml` の `Home` タブに残っている legacy 設定群。

対象外:

- `OverlayWindow.xaml`
- `RoiSelectorWindow.xaml`
- `OcrPreviewZoomWindow.xaml`

## 3. 現在の UserControl 一覧
| 現在名 | 現在の役割 | 判断 |
| --- | --- | --- |
| `OverviewControl` | 構成サマリ、主要ホットキー、即時操作、主要運用設定 | 維持。主要運用設定の primary owner とする |
| `CaptureControl` | capture mode / provider / ROI 詳細 | 維持 |
| `OcrSettingsControl` | OCR 共通設定、OCR 前処理、行結合系調整 | 維持 |
| `OcrEnginesControl` | PaddleOCR / PaddleOCR-VL 系設定 | 維持 |
| `VisionLlmSettingsControl` | VisionLLM 固有設定 | 維持 |
| `OverlayBehaviorControl` | overlay readability、scene change / auto-translate 条件 | 維持 |
| `HookFullscreenControl` | Hook / Fullscreen 設定 | 維持 |
| `TranslationControl` | 翻訳優先順位、Llama.cpp、DeepL/Gemini API 設定 | 維持 |
| `HotkeysControl` | ホットキー割り当て | 維持 |
| `SystemSettingsControl` | logging / resource budget | 維持 |
| `RuntimeLogsControl` | Preview / Log 表示 | 維持 |

今回新規作成したもの:

- `CaptureControl`
- `OverlayBehaviorControl`

## 4. 現在の設定項目
### 4.1 `OverviewControl`
現在 `OverviewControl` が持っている設定項目:

- `SourceLanguageTag`
- `SourceLanguageCustom`
- `TargetLanguageTag`
- `TargetLanguageCustom`
- `OcrEngineTag`
- `EnableDeepL`
- `EnableGemini`
- `EnableLlamaCppTranslation`
- `EnableRoi`
- `EnableGraphicsHookPipeline`
- `GraphicsHookApiTag`
- `GraphicsHookOverlayEnabled`
- `EnableMirrorFullscreenMode`
- `OverlayFontSize`
- `OverlayBackgroundOpacity`
- `EnableFixedRoiOverlay`

設定以外の quick action / state:

- `RunOnceCommand`
- `SelectRoiCommand`
- `SwapLanguagesCommand`
- ROI slot 選択
- hotkey summary
- current setup summary

### 4.2 `OcrSettingsControl`
- `PhashThresholdText`
- `IouThresholdText`
- `VerticalModeOverrideTag`
- `EnableSimpleMergeTuning`
- `HorizontalMergeStrength`
- `VerticalMergeStrength`
- `EnableOcrBinarization`
- `EnableOcrAutoThreshold`
- `EnableOcrGamma`
- `EnableOcrGrayscale`
- `EnableOcrContrast`
- `EnableOcrTwoPass`
- `OcrTwoPassPreferAuto`
- `OcrTwoPassLowThreshold`
- `OcrTwoPassHighThreshold`
- `OcrBinarizationThreshold`
- `OcrGamma`
- `OcrContrast`
- `EnableOcrDownsampling`
- `OcrDownsampleScale`

### 4.3 `CaptureControl`
- `CaptureModeTag`
- `CaptureProviderTag`
- `IsCaptureProviderFixed`
- `EnableRoi`

### 4.4 `OcrEnginesControl`
- `PaddleDetectionModelName`
- `PaddleRecognitionModelName`
- `PaddleTextDetThreshText`
- `PaddleTextDetBoxThreshText`
- `PaddleTextDetUnclipRatioText`
- `PaddleTextRecScoreThreshText`
- `EnablePaddleConfidenceFilter`
- `PaddleConfidenceThreshold`
- `PaddleVlPipelineVersion`
- `PaddleVlMaxPixelsText`
- `PaddleVlLayoutThresholdText`
- `PaddleVlMaxNewTokensText`
- `PaddleVlUseLayoutDetectionModeTag`
- `PaddleVlPrecisionTag`

### 4.5 `VisionLlmSettingsControl`
- `VisionLlmSelectedModelFileName`
- `VisionLlmSelectedMmprojFileName`
- `VisionLlmHostText`
- `VisionLlmPortText`
- `VisionLlmContextSizeText`
- `VisionLlmGpuLayersText`
- `VisionLlmThreadsText`
- `VisionLlmParallelText`
- `VisionLlmBatchSizeText`
- `VisionLlmMaxTokensText`
- `VisionLlmMaxImageSideText`
- `EnableVisionLlmSharedLocalTranslation`
- `EnableVisionGeometryHybridOcr`
- `VisionGeometryHybridBaseEngineTag`

### 4.6 `OverlayBehaviorControl`
- `EnableOverlayFontStabilization`
- `EnableSmallBoxReadabilityBoost`
- `SmallTextThresholdPx`
- `EnableSceneChangeAutoHide`
- `EnableSceneChangeAutoTranslate`
- `ShowAutoTranslateBadgeIcon`
- `EnableSceneChangeQuietWindow`
- `SceneChangeQuietWindowMsText`
- `EnableSceneChangeTextWeighted`
- `SceneChangeThreshold`
- `SceneChangeWatchIntervalMs`
- `SceneChangeWatchPhashThreshold`

### 4.7 `HookFullscreenControl`
- `EnableGraphicsHookPipeline`
- `GraphicsHookApiTag`
- `GraphicsHookOverlayEnabled`
- `GraphicsHookFallbackOnError`
- `GraphicsHookCaptureFpsLimitText`
- `GraphicsHookPerfDiagLog`
- `GraphicsHookDiagFileSink`
- `EnableGraphicsHookLauncher`
- `GraphicsHookLauncherExePath`
- `GraphicsHookLauncherArgs`
- `EnableMirrorFullscreenMode`
- `MagpieProfileIndexText`

### 4.8 `TranslationControl`
- `LlamaSelectedModelFileName`
- `LlamaHostText`
- `LlamaPortText`
- `LlamaContextSizeText`
- `LlamaGpuLayersText`
- `LlamaThreadsText`
- `LlamaParallelText`
- `LlamaBatchSizeText`
- `LlamaMaxTokensText`
- `LlamaTemperatureText`
- `LlamaTopPText`
- `LlamaTopKText`
- `LlamaRepeatPenaltyText`
- `DeepLApiKeyText`
- `DeepLEndpointText`
- `ApiKeyText`

`TranslationPriority` は `Settings.*` ではないが、`TranslationControl` の責務に含める。

### 4.9 `HotkeysControl`
各ホットキーは `Key / Ctrl / Alt / Shift` の組で持つ。

- `HotkeyRunOnce*`
- `HotkeyRunNextRoi*`
- `HotkeyRunNextNextRoi*`
- `HotkeyToggleOverlay*`
- `HotkeyForceRun*`
- `HotkeyForceRunNextRoi*`
- `HotkeyForceRunNextNextRoi*`
- `HotkeyForceGeminiStrict*`
- `HotkeyOcrOnly*`
- `HotkeyToggleSceneAutoTranslate*`
- `HotkeySelectRoi*`
- `HotkeyLockCaptureWindow*`
- `HotkeyUnlockCaptureWindow*`
- `HotkeyToggleMirrorFullscreen*`
- `EnableRawInputHotkeys`

### 4.10 `SystemSettingsControl`
- `ResourceBudgetProfileTag`
- `EnableLogging`
- `EnableOcrPerfLog`
- `OcrPerfLogThresholdText`

### 4.11 `RuntimeLogsControl`
`Settings.*` の owner は持たない。表示・確認専用。

### 4.12 `MainWindow.xaml` の `Home` タブにまだ残っている legacy 設定
現在、`MainWindow.xaml` の `Home` タブに残る `Settings.*` ベースの legacy 設定はない。

## 5. 正規 owner の再配分方針
### 5.1 `OverviewControl`
`OverviewControl` は quick access だけではなく、主要運用設定の primary owner とする。

`Overview` に primary owner として残す項目:

- `SourceLanguageTag`
- `SourceLanguageCustom`
- `TargetLanguageTag`
- `TargetLanguageCustom`
- `OcrEngineTag`
- `EnableDeepL`
- `EnableGemini`
- `EnableLlamaCppTranslation`
- `EnableRoi`
- `OverlayFontSize`
- `OverlayBackgroundOpacity`
- `EnableFixedRoiOverlay`

`Overview` に quick access として残すもの:

- ROI slot
- `Run test`
- `Select ROI`

`Overview` から外すべき legacy / detailed 設定:

- `CaptureModeTag`
- `CaptureProviderTag`
- `IsCaptureProviderFixed`
- `EnableOverlayFontStabilization`
- `EnableSmallBoxReadabilityBoost`
- `SmallTextThresholdPx`

### 5.2 `CaptureControl`

正規 owner にする項目:

- `CaptureModeTag`
- `CaptureProviderTag`
- `IsCaptureProviderFixed`

今後ここへ寄せるもの:

- ROI slot の正規編集
- fixed target / fixed window 系設定
- capture route / provider fallback 関連

補足:

- `Overview` の ROI slot と `EnableRoi` は主要運用設定として残してよい
- `Current Setup` の capture summary は `CaptureControl` の設定値を参照するだけにする

### 5.3 `OcrSettingsControl`
維持する。

理由:

- `OcrEngineTag` は `Overview` に残し、`OcrSettingsControl` は OCR 共通設定と前処理設定を担う
- 日常運用で頻繁に切り替える値まで詳細設定側へ押し込まない

### 5.4 `OcrEnginesControl`
維持する。

正規 owner:

- PaddleOCR / PaddleOCR-VL 系の engine 固有設定

判断:

- 現時点では `OCREngines` という名前のままでよい
- 将来的に engine ごとの panel 分割を進める場合のみ再命名を検討する

### 5.5 `VisionLlmSettingsControl`
維持する。

正規 owner:

- VisionLLM 固有設定
- VisionLLM translation shared / geometry assist の設定

### 5.6 `TranslationControl`
維持する。

理由:

- `TranslationControl` は優先順位、Llama.cpp runtime、DeepL/Gemini API などの詳細設定を担う
- `Source / Target language` と translation engine enable は `Overview` に残し、詳細設定側へ無理に移さない

### 5.7 `HookFullscreenControl`
維持する。

正規 owner:

- 既存の Hook / Fullscreen 全設定

### 5.8 `OverlayBehaviorControl`

正規 owner にする項目:

- `EnableSceneChangeAutoHide`
- `EnableSceneChangeAutoTranslate`
- `ShowAutoTranslateBadgeIcon`
- `EnableSceneChangeQuietWindow`
- `SceneChangeQuietWindowMsText`
- `EnableSceneChangeTextWeighted`
- `SceneChangeThreshold`
- `SceneChangeWatchIntervalMs`
- `SceneChangeWatchPhashThreshold`
- `EnableOverlayFontStabilization`
- `EnableSmallBoxReadabilityBoost`
- `SmallTextThresholdPx`

理由:

- scene change / auto-translate と readability 系は、どちらも overlay の見え方と自動制御に属する
- `EnableOverlayFontStabilization`、`EnableSmallBoxReadabilityBoost`、`SmallTextThresholdPx` は `Overview` には重く、`Overlay Behavior` に置くべき
- `EnableFixedRoiOverlay`、`OverlayFontSize`、`OverlayBackgroundOpacity` は主要運用設定として `Overview` に残してよい

### 5.9 `HotkeysControl`
維持する。

正規 owner:

- 全 hotkey 割り当て
- raw input backend の切替

### 5.10 `SystemSettingsControl`
維持する。

正規 owner:

- logging 設定
- resource budget 設定

### 5.11 `RuntimeLogsControl`
維持する。

正規 owner:

- なし

役割:

- Preview / Log の表示
- runtime 状態確認

## 6. 実装順の判断
今回の再配分で完了したこと:

1. `CaptureControl` を作成し、`Home` の capture legacy 設定を移した
2. `OverlayBehaviorControl` を作成し、`EnableOverlayFontStabilization` / `EnableSmallBoxReadabilityBoost` / `SmallTextThresholdPx` を移した
3. `Overview` から capture legacy 設定を削除した
4. `Overview` は主要運用設定だけを残し、サイドパネルは詳細設定へ寄せた

## 7. 結論
今回の判断は次の通り。

- `CaptureControl` を新規作成した
- `OverlayBehaviorControl` を新規作成した
- `OverviewControl` は主要運用設定の primary owner とする
- `CaptureModeTag` / `CaptureProviderTag` / `IsCaptureProviderFixed` は `CaptureControl`
- `EnableOverlayFontStabilization` / `EnableSmallBoxReadabilityBoost` / `SmallTextThresholdPx` は `OverlayBehaviorControl`
- `SourceLanguage*` / `TargetLanguage*` / `EnableDeepL` / `EnableGemini` / `EnableLlamaCppTranslation` は `OverviewControl`
- `OcrEngineTag` は `OverviewControl`
- `EnableFixedRoiOverlay` / `OverlayFontSize` / `OverlayBackgroundOpacity` は `OverviewControl`

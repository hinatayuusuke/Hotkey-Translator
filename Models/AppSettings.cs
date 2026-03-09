using System.Text.Json.Serialization;

using System.Collections.Generic;

namespace Hotkey_Translator.Models;

public enum CaptureMode
{
    Screen,
    ActiveWindow
}

public enum CaptureProviderMode
{
    Auto,
    Fixed
}

public enum GraphicsHookApiKind : uint
{
    Dx11 = 1,
    Dx12 = 2,
    OpenGl = 3,
    Vulkan = 4,
    Dx9 = 5
}

public enum VerticalModeOverride
{
    // COMPAT: Persisted to settings.json; keep numeric values stable.
    Auto = 0,
    Horizontal = 1,
    Vertical = 2
}

public enum VerticalColumnOrder
{
    // COMPAT: Persisted to settings.json; keep numeric values stable.
    RightToLeft = 0,
    LeftToRight = 1
}

public sealed class AppSettings
{
    public List<RoiPreset> RoiPresets { get; set; } = new();
    public int ActiveRoiPresetIndex { get; set; }
    public CaptureMode CaptureMode { get; set; } = CaptureMode.ActiveWindow;
    public SerializableRect? Roi { get; set; }
    public NormalizedRect? NormalizedRoi { get; set; }
    public bool EnableRoi { get; set; } = true;
    public CaptureProviderMode CaptureProviderMode { get; set; } = CaptureProviderMode.Auto;
    public CaptureProviderKind PreferredCaptureProvider { get; set; } = CaptureProviderKind.Gdi;
    public bool EnableWgcCapture { get; set; } = true;
    public bool EnableDxgiCapture { get; set; } = true;
    public bool EnableFixedCaptureWindow { get; set; } = false;
    public long FixedCaptureWindowHandle { get; set; }
    public int FixedCaptureWindowProcessId { get; set; }
    public string FixedCaptureWindowProcessName { get; set; } = string.Empty;
    public string FixedCaptureWindowClassName { get; set; } = string.Empty;
    public string FixedCaptureWindowTitle { get; set; } = string.Empty;
    public int BlackFrameThreshold { get; set; } = 3;
    public double BlackLumaThreshold { get; set; } = 16.0;
    public double BlackVarianceThreshold { get; set; } = 8.0;
    public int BlackSampleStride { get; set; } = 8;
    public double ProviderCooldownSeconds { get; set; } = 0.5;
    public int PhashThreshold { get; set; } = 4;
    public double OcrIouThreshold { get; set; } = 0.85;
    public bool EnableOcrBinarization { get; set; } = false;
    public int OcrBinarizationThreshold { get; set; } = 160;
    public bool EnableOcrAutoThreshold { get; set; } = false;
    public bool EnableOcrGamma { get; set; } = false;
    public double OcrGamma { get; set; } = 1.0;
    public bool EnableOcrGrayscale { get; set; } = false;
    public bool EnableOcrContrast { get; set; } = false;
    public double OcrContrast { get; set; } = 1.5;
    public bool EnableLogging { get; set; } = false;
    public bool EnableOcrPerfLog { get; set; } = false;
    public int OcrPerfLogThresholdMs { get; set; } = 200;
    public bool EnableOcrDownsampling { get; set; } = false;
    public double OcrDownsampleScale { get; set; } = 0.75;
    public bool EnableOcrTwoPass { get; set; } = false;
    public int OcrTwoPassLowThreshold { get; set; } = 120;
    public int OcrTwoPassHighThreshold { get; set; } = 180;
    public bool OcrTwoPassPreferAuto { get; set; } = true;
    public bool EnableFixedRoiOverlay { get; set; } = false;
    public bool EnableOverlayFontStabilization { get; set; } = true;
    public bool EnableSmallBoxReadabilityBoost { get; set; } = false;
    public double SmallTextThresholdPx { get; set; } = 22;
    public double SmallBoxMaxScale { get; set; } = 1.6;
    public double SmallBoxFontScaleWeight { get; set; } = 0.7;
    public double SmallBoxSlenderAspectThreshold { get; set; } = 3.0;
    public double SmallBoxSlenderThresholdBoost { get; set; } = 1.2;
    public bool EnableSceneChangeAutoHide { get; set; } = true;
    public bool EnableSceneChangeAutoTranslate { get; set; } = false;
    public bool ShowAutoTranslateBadgeIcon { get; set; } = true;
    public bool EnableGraphicsHookPipeline { get; set; } = false;
    public GraphicsHookApiKind GraphicsHookApi { get; set; } = GraphicsHookApiKind.Dx11;
    public int GraphicsHookCaptureFpsLimit { get; set; } = 15;
    public bool GraphicsHookOverlayEnabled { get; set; } = true;
    public bool GraphicsHookFallbackOnError { get; set; } = true;
    public bool EnableGraphicsHookPerfDiagLog { get; set; } = false;
    public bool EnableGraphicsHookDiagFileSink { get; set; } = false;
    public bool EnableGraphicsHookLauncher { get; set; } = false;
    public string GraphicsHookLauncherExePath { get; set; } = string.Empty;
    public string GraphicsHookLauncherArgs { get; set; } = string.Empty;
    public string GraphicsHookPipeName { get; set; } = "hotkey_translator_hook";
    public bool EnableMirrorFullscreenMode { get; set; } = false;
    public int MagpieProfileIndex { get; set; } = 0;
    public bool EnableSceneChangeTextWeighted { get; set; } = false;
    public double SceneChangeThreshold { get; set; } = 0.2;
    public int SceneChangeWatchIntervalMs { get; set; } = 1000;
    public int SceneChangeWatchPhashThreshold { get; set; } = 8;
    public bool EnableSceneChangeSemanticGate { get; set; } = true;
    public bool EnableSceneChangeQuietWindow { get; set; } = true;
    public int SceneChangeQuietWindowMs { get; set; } = 450;
    public double SceneSemanticBlockIouThreshold { get; set; } = 0.5;
    public int SceneSemanticMinChars { get; set; } = 2;
    public int SceneSemanticRequireConfirmTicks { get; set; } = 1;
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.WinRt;
    public string PaddleDevice { get; set; } = "cpu";
    public string? PaddleModelDir { get; set; }
    public string PaddleTextDetectionModelName { get; set; } = "PP-OCRv5_mobile_det";
    public string PaddleTextRecognitionModelName { get; set; } = "PP-OCRv5_server_rec";
    public double PaddleTextDetThresh { get; set; } = 0.3;
    public double PaddleTextDetBoxThresh { get; set; } = 0.5;
    public double PaddleTextDetUnclipRatio { get; set; } = 1.0;
    public double PaddleTextRecScoreThresh { get; set; } = 0.58;
    public bool EnablePaddleConfidenceFilter { get; set; } = false;
    public double PaddleConfidenceThreshold { get; set; } = 0.6;
    public bool EnablePaddleGrpcHost { get; set; } = true;
    public string PaddleGrpcEndpoint { get; set; } = "http://127.0.0.1:50051";
    public string PaddleGrpcHost { get; set; } = "127.0.0.1";
    public int PaddleGrpcPort { get; set; } = 50051;
    public int PaddleGrpcReadyTimeoutMs { get; set; } = 120000;
    public int PaddleGrpcRestartMax { get; set; } = 3;
    public int PaddleGrpcRestartWindowSeconds { get; set; } = 30;
    public bool EnablePaddleVlGrpcHost { get; set; } = true;
    public string PaddleVlGrpcEndpoint { get; set; } = "http://127.0.0.1:50052";
    public string PaddleVlGrpcHost { get; set; } = "127.0.0.1";
    public int PaddleVlGrpcPort { get; set; } = 50052;
    public int PaddleVlGrpcReadyTimeoutMs { get; set; } = 180000;
    public int PaddleVlGrpcRestartMax { get; set; } = 3;
    public int PaddleVlGrpcRestartWindowSeconds { get; set; } = 30;
    public string PaddleVlDevice { get; set; } = "gpu:0";
    public string PaddleVlPipelineVersion { get; set; } = "v1.5";
    public int? PaddleVlMaxPixels { get; set; }
    public double? PaddleVlLayoutThreshold { get; set; }
    public bool? PaddleVlLayoutNms { get; set; }
    public double? PaddleVlLayoutUnclipRatio { get; set; }
    public string? PaddleVlLayoutMergeBboxesMode { get; set; }
    public double? PaddleVlLayoutMergeBboxesIouThreshold { get; set; }
    public int? PaddleVlMaxNewTokens { get; set; }
    public bool? PaddleVlMergeLayoutBlocks { get; set; }
    public bool? PaddleVlUseOcrForImageBlock { get; set; }
    public bool? PaddleVlUseLayoutDetection { get; set; }
    public bool PaddleVlEnableHpi { get; set; } = false;
    public bool? PaddleVlUseTensorrt { get; set; }
    public string? PaddleVlPrecision { get; set; } = "fp16";
    public bool EnableNdlGrpcHost { get; set; } = true;
    public string NdlGrpcEndpoint { get; set; } = "http://127.0.0.1:50053";
    public string NdlGrpcHost { get; set; } = "127.0.0.1";
    public int NdlGrpcPort { get; set; } = 50053;
    public int NdlGrpcReadyTimeoutMs { get; set; } = 120000;
    public int NdlGrpcRestartMax { get; set; } = 3;
    public int NdlGrpcRestartWindowSeconds { get; set; } = 30;
    public string NdlDevice { get; set; } = "cpu";
    public string? NdlModelDir { get; set; }
    public string? NdlConfigDir { get; set; }
    public double NdlDetScoreThreshold { get; set; } = 0.2;
    public double NdlDetConfThreshold { get; set; } = 0.25;
    public double NdlDetIouThreshold { get; set; } = 0.2;
    public bool EnableVisionLlmGrpcHost { get; set; } = true;
    public string VisionLlmGrpcEndpoint { get; set; } = "http://127.0.0.1:50074";
    public string VisionLlmGrpcHost { get; set; } = "127.0.0.1";
    public int VisionLlmGrpcPort { get; set; } = 50074;
    public int VisionLlmGrpcReadyTimeoutMs { get; set; } = 120000;
    public int VisionLlmGrpcRestartMax { get; set; } = 3;
    public int VisionLlmGrpcRestartWindowSeconds { get; set; } = 30;
    public string VisionLlmHost { get; set; } = "127.0.0.1";
    public int VisionLlmPort { get; set; } = 8089;
    public int VisionLlmContextSize { get; set; } = 2048;
    public int VisionLlmGpuLayers { get; set; } = 999;
    public int VisionLlmThreads { get; set; } = 4;
    public int VisionLlmParallel { get; set; } = 1;
    public int VisionLlmBatchSize { get; set; } = 1024;
    public int VisionLlmMaxTokens { get; set; } = 1024;
    public string VisionLlmSelectedModelFileName { get; set; } = "Qwen3.5-4B-Q4_K_M.gguf";
    public string VisionLlmSelectedMmprojFileName { get; set; } = "4Bmmproj-F16.gguf";
    public int VisionLlmMaxImageSide { get; set; } = 768;
    public bool EnableVisionLlmSharedLocalTranslation { get; set; } = true;
    public bool EnableVisionLlmDiagFileLog { get; set; } = false;
    public string VisionLlmDiagLogPath { get; set; } = string.Empty;
    public string FlorenceProjectDir { get; set; } = "Tools\\Florence2";
    public string FlorenceUvPath { get; set; } = "uv";
    public string FlorenceModelName { get; set; } = "microsoft/Florence-2-large";
    public string FlorenceDevice { get; set; } = "cuda";
    public string? FlorenceModelDir { get; set; }
    public bool EnableLineMerge { get; set; } = true;
    public bool EnableEngineScaledLineMergeProfile { get; set; } = false;
    public bool EnableTwoStageLineMerge { get; set; } = true;
    public bool EnableSimpleMergeTuning { get; set; } = false;
    public int HorizontalMergeStrength { get; set; } = 50;
    public int VerticalMergeStrength { get; set; } = 50;
    public double MergeOverlapRatioThreshold { get; set; } = 0.1;
    public double MergeVerticalWeight { get; set; } = 0.5;
    public double MergeThresholdRatio { get; set; } = 0.9;
    public int MergeNeighborCount { get; set; } = 12;
    public double RowMergeYCenterToleranceRatio { get; set; } = 0.45;
    public double RowMergeHeightRatioMin { get; set; } = 0.55;
    public double RowMergeMaxGapRatio { get; set; } = 1.5;
    public double RowMergeHardBreakRatio { get; set; } = 2;
    public int RowMergeNeighborCount { get; set; } = 24;
    public bool EnableVerticalMerge { get; set; } = true;
    public bool VerticalModeAutoDetect { get; set; } = true;
    public VerticalModeOverride VerticalModeOverride { get; set; } = VerticalModeOverride.Auto;
    public VerticalColumnOrder VerticalColumnOrder { get; set; } = VerticalColumnOrder.RightToLeft;
    public double VerticalGapRatio { get; set; } = 1.25;
    public bool EnableVerticalColumnMerge { get; set; } = true;
    public int VerticalColumnMergeNeighborCount { get; set; } = 8;
    public double VerticalColumnMergeOverlapRatioThreshold { get; set; } = 0.20;
    public double VerticalColumnMergeWeight { get; set; } = 0.5;
    public double VerticalColumnMergeThresholdRatio { get; set; } = 0.9;
    public double VerticalColumnMergeHardBreakRatio { get; set; } = 1.8;
    public double PaddleMergeOverlapScale { get; set; } = 0.5333333333;
    public double PaddleMergeVerticalWeightScale { get; set; } = 1.2;
    public double PaddleMergeThresholdScale { get; set; } = 0.83;
    public double PaddleRowMergeYCenterToleranceScale { get; set; } = 1.2222222222;
    public double PaddleRowMergeHeightRatioMinScale { get; set; } = 0.8181818182;
    public double PaddleRowMergeMaxGapScale { get; set; } = 0.7857142857;
    public double PaddleRowMergeHardBreakScale { get; set; } = 0.7777777778;
    public double PaddleVerticalGapScale { get; set; } = 1.0;
    public double PaddleVerticalColumnMergeOverlapScale { get; set; } = 1.0;
    public double PaddleVerticalColumnMergeWeightScale { get; set; } = 1.0;
    public double PaddleVerticalColumnMergeThresholdScale { get; set; } = 1.0;
    public double PaddleVerticalColumnMergeHardBreakScale { get; set; } = 1.0;
    public string HotkeyRunOnceKey { get; set; } = "F8";
    public string HotkeyRunOnceModifiers { get; set; } = "None";
    public string HotkeyRunNextRoiKey { get; set; } = "F8";
    public string HotkeyRunNextRoiModifiers { get; set; } = "Shift";
    public string HotkeyRunNextNextRoiKey { get; set; } = "F8";
    public string HotkeyRunNextNextRoiModifiers { get; set; } = "Control";
    public string HotkeyToggleOverlayKey { get; set; } = "F9";
    public string HotkeyToggleOverlayModifiers { get; set; } = "None";
    public string HotkeyForceRunKey { get; set; } = "F10";
    public string HotkeyForceRunModifiers { get; set; } = "None";
    public string HotkeyForceRunNextRoiKey { get; set; } = "F10";
    public string HotkeyForceRunNextRoiModifiers { get; set; } = "Shift";
    public string HotkeyForceRunNextNextRoiKey { get; set; } = "F10";
    public string HotkeyForceRunNextNextRoiModifiers { get; set; } = "Control";
    public string HotkeyForceGeminiStrictKey { get; set; } = "F8";
    public string HotkeyForceGeminiStrictModifiers { get; set; } = "Alt";
    public string HotkeyOcrOnlyKey { get; set; } = "F11";
    public string HotkeyOcrOnlyModifiers { get; set; } = "None";
    public string HotkeyToggleSceneAutoTranslateKey { get; set; } = "F5";
    public string HotkeyToggleSceneAutoTranslateModifiers { get; set; } = "None";
    public string HotkeySelectRoiKey { get; set; } = "F6";
    public string HotkeySelectRoiModifiers { get; set; } = "None";
    public string HotkeyNextRoiPresetKey { get; set; } = "F6";
    public string HotkeyNextRoiPresetModifiers { get; set; } = "Shift";
    public string HotkeyPreviousRoiPresetKey { get; set; } = "F6";
    public string HotkeyPreviousRoiPresetModifiers { get; set; } = "Control";
    public string HotkeyLockCaptureWindowKey { get; set; } = "F7";
    public string HotkeyLockCaptureWindowModifiers { get; set; } = "None";
    public string HotkeyUnlockCaptureWindowKey { get; set; } = "F7";
    public string HotkeyUnlockCaptureWindowModifiers { get; set; } = "Shift";
    public string HotkeyToggleMirrorFullscreenKey { get; set; } = "F7";
    public string HotkeyToggleMirrorFullscreenModifiers { get; set; } = "Control";
    public bool EnableRawInputHotkeys { get; set; } = false;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "ja";
    public string StyleId { get; set; } = "default";
    public string GlossaryVersion { get; set; } = "v1";
    public bool EnableGemini { get; set; } = false;
    public string GeminiModel { get; set; } = "gemini-2.5Flash lite";
    public string GeminiEndpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";
    public bool EnableDeepL { get; set; } = false;
    public string DeepLEndpoint { get; set; } = "https://api-free.deepl.com/v2/translate";
    public bool EnableLlamaCppTranslation { get; set; } = false;
    public string LlamaGrpcEndpoint { get; set; } = "http://127.0.0.1:50071";
    public string LlamaGrpcHost { get; set; } = "127.0.0.1";
    public int LlamaGrpcPort { get; set; } = 50071;
    public int LlamaGrpcReadyTimeoutMs { get; set; } = 60000;
    public int LlamaGrpcRestartMax { get; set; } = 3;
    public int LlamaGrpcRestartWindowSeconds { get; set; } = 30;
    public string LlamaHost { get; set; } = "127.0.0.1";
    public int LlamaPort { get; set; } = 8088;
    public int LlamaContextSize { get; set; } = 4096;
    public int LlamaGpuLayers { get; set; } = 999;
    public int LlamaThreads { get; set; } = 4;
    public int LlamaParallel { get; set; } = 1;
    public int LlamaBatchSize { get; set; } = 2048;
    public int LlamaMaxTokens { get; set; } = 2048;
    public double LlamaTemperature { get; set; } = 0.3;
    public double LlamaTopP { get; set; } = 0.6;
    public int LlamaTopK { get; set; } = 20;
    public double LlamaRepeatPenalty { get; set; } = 1.05;
    public string LlamaSelectedModelFileName { get; set; } = "HY-MT1.5-1.8B-Q8_0.gguf";
    public List<string> TranslationPriority { get; set; } = new();
    public double OverlayFontSize { get; set; } = 48;
    public string OverlayForeground { get; set; } = "#FFFFFFFF";
    public string OverlayBackground { get; set; } = "#AA000000";
    public double OverlayBackgroundOpacity { get; set; } = 0.75;

    [JsonIgnore]
    public string? ApiKey { get; set; }

    public string? ApiKeyProtected { get; set; }

    [JsonIgnore]
    public string? DeepLApiKey { get; set; }

    public string? DeepLApiKeyProtected { get; set; }
}



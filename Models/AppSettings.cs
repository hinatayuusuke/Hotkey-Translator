using System.Text.Json.Serialization;

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

public sealed class AppSettings
{
    public CaptureMode CaptureMode { get; set; } = CaptureMode.ActiveWindow;
    public SerializableRect? Roi { get; set; }
    public NormalizedRect? NormalizedRoi { get; set; }
    public bool EnableRoi { get; set; } = true;
    public CaptureProviderMode CaptureProviderMode { get; set; } = CaptureProviderMode.Auto;
    public CaptureProviderKind PreferredCaptureProvider { get; set; } = CaptureProviderKind.Gdi;
    public bool EnableWgcCapture { get; set; } = true;
    public bool EnableDxgiCapture { get; set; } = true;
    public int BlackFrameThreshold { get; set; } = 3;
    public double BlackLumaThreshold { get; set; } = 16.0;
    public double BlackVarianceThreshold { get; set; } = 8.0;
    public int BlackSampleStride { get; set; } = 8;
    public int ProviderCooldownSeconds { get; set; } = 3;
    public int PhashThreshold { get; set; } = 4;
    public double OcrIouThreshold { get; set; } = 0.85;
    public bool EnableOcrBinarization { get; set; } = false;
    public int OcrBinarizationThreshold { get; set; } = 160;
    public bool EnableOcrAutoThreshold { get; set; } = false;
    public bool EnableOcrAutoInvert { get; set; } = false;
    public bool EnableOcrGamma { get; set; } = false;
    public double OcrGamma { get; set; } = 1.0;
    public bool EnableLogging { get; set; } = true;
    public bool EnableOcrPerfLog { get; set; } = false;
    public int OcrPerfLogThresholdMs { get; set; } = 200;
    public bool EnableOcrDownsampling { get; set; } = false;
    public double OcrDownsampleScale { get; set; } = 0.75;
    public bool EnableOcrTwoPass { get; set; } = false;
    public int OcrTwoPassLowThreshold { get; set; } = 120;
    public int OcrTwoPassHighThreshold { get; set; } = 180;
    public bool OcrTwoPassPreferAuto { get; set; } = true;
    public bool EnableFixedRoiOverlay { get; set; } = false;
    public bool EnableSceneChangeAutoHide { get; set; } = true;
    public bool EnableSceneChangeTextWeighted { get; set; } = false;
    public double SceneChangeThreshold { get; set; } = 0.2;
    public int SceneChangeWatchIntervalMs { get; set; } = 1000;
    public int SceneChangeWatchPhashThreshold { get; set; } = 8;
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.WinRt;
    public string PaddleProjectDir { get; set; } = "Tools\\PaddleOcr";
    public string PaddleUvPath { get; set; } = "uv";
    public string PaddleLanguage { get; set; } = "japan";
    public string PaddleDevice { get; set; } = "cpu";
    public string? PaddleModelDir { get; set; }
    public string FlorenceProjectDir { get; set; } = "Tools\\Florence2";
    public string FlorenceUvPath { get; set; } = "uv";
    public string FlorenceModelName { get; set; } = "microsoft/Florence-2-large";
    public string FlorenceDevice { get; set; } = "cuda";
    public string? FlorenceModelDir { get; set; }
    public string VllmBaseUrl { get; set; } = "http://localhost:8000/v1";
    public string VllmModelName { get; set; } = "PaddlePaddle/PaddleOCR-VL";
    public bool EnableLineMerge { get; set; } = true;
    public double MergeOverlapRatioThreshold { get; set; } = 0.1;
    public double MergeVerticalWeight { get; set; } = 0.9;
    public double MergeThresholdRatio { get; set; } = 0.9;
    public int MergeNeighborCount { get; set; } = 12;
    public string HotkeyRunOnceKey { get; set; } = "F8";
    public string HotkeyRunOnceModifiers { get; set; } = "None";
    public string HotkeyToggleOverlayKey { get; set; } = "F9";
    public string HotkeyToggleOverlayModifiers { get; set; } = "None";
    public string HotkeyForceRunKey { get; set; } = "F10";
    public string HotkeyForceRunModifiers { get; set; } = "None";
    public string HotkeyOcrOnlyKey { get; set; } = "F11";
    public string HotkeyOcrOnlyModifiers { get; set; } = "None";
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "ja";
    public string StyleId { get; set; } = "default";
    public string GlossaryVersion { get; set; } = "v1";
    public bool EnableGemini { get; set; } = false;
    public string GeminiModel { get; set; } = "gemini-2.5-flash-lite";
    public string GeminiEndpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";
    public bool EnableDeepL { get; set; } = false;
    public string DeepLEndpoint { get; set; } = "https://api-free.deepl.com/v2/translate";
    public List<string> TranslationPriority { get; set; } = new(TranslationProviderNames.Defaults);
    public double OverlayFontSize { get; set; } = 48;
    public string OverlayForeground { get; set; } = "#FFFFFFFF";
    public string OverlayBackground { get; set; } = "#AA000000";
    public double OverlayBackgroundOpacity { get; set; } = 0.75;

    [JsonIgnore]
    public string? ApiKey { get; set; }

    public string? ApiKeyProtected { get; set; }

    [JsonIgnore]
    public string? VllmApiKey { get; set; }

    public string? VllmApiKeyProtected { get; set; }

    [JsonIgnore]
    public string? DeepLApiKey { get; set; }

    public string? DeepLApiKeyProtected { get; set; }
}

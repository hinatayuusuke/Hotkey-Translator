using System.Text.Json.Serialization;

namespace Hotkey_Translator.Models;

public enum CaptureMode
{
    Screen,
    ActiveWindow
}

public sealed class AppSettings
{
    public CaptureMode CaptureMode { get; set; } = CaptureMode.ActiveWindow;
    public SerializableRect? Roi { get; set; }
    public NormalizedRect? NormalizedRoi { get; set; }
    public bool EnableRoi { get; set; } = true;
    public CaptureProviderKind PreferredCaptureProvider { get; set; } = CaptureProviderKind.Wgc;
    public bool EnableWgcCapture { get; set; } = true;
    public bool EnableDxgiCapture { get; set; } = true;
    public int BlackFrameThreshold { get; set; } = 3;
    public double BlackLumaThreshold { get; set; } = 16.0;
    public double BlackVarianceThreshold { get; set; } = 8.0;
    public int BlackSampleStride { get; set; } = 8;
    public int ProviderCooldownSeconds { get; set; } = 3;
    public int PhashThreshold { get; set; } = 10;
    public double OcrIouThreshold { get; set; } = 0.85;
    public bool EnableLineMerge { get; set; } = true;
    public double MergeOverlapRatioThreshold { get; set; } = 0.1;
    public double MergeVerticalWeight { get; set; } = 0.5;
    public double MergeThresholdRatio { get; set; } = 1.0;
    public int MergeNeighborCount { get; set; } = 12;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "ja";
    public string StyleId { get; set; } = "default";
    public string GlossaryVersion { get; set; } = "v1";
    public bool EnableGemini { get; set; } = false;
    public string GeminiModel { get; set; } = "gemini-3-flash-preview";
    public string GeminiEndpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";
    public double OverlayFontSize { get; set; } = 18;
    public string OverlayForeground { get; set; } = "#FFFFFFFF";
    public string OverlayBackground { get; set; } = "#AA000000";

    [JsonIgnore]
    public string? ApiKey { get; set; }

    public string? ApiKeyProtected { get; set; }
}

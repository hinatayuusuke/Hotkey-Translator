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
    public int PhashThreshold { get; set; } = 10;
    public double OcrIouThreshold { get; set; } = 0.85;
    public string SourceLanguage { get; set; } = "ja";
    public string TargetLanguage { get; set; } = "en";
    public string StyleId { get; set; } = "default";
    public string GlossaryVersion { get; set; } = "v1";
    public bool EnableGemini { get; set; } = false;
    public string GeminiModel { get; set; } = "gemini-3.0-flash";
    public string GeminiEndpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";
    public double OverlayFontSize { get; set; } = 18;
    public string OverlayForeground { get; set; } = "#FFFFFFFF";
    public string OverlayBackground { get; set; } = "#AA000000";

    [JsonIgnore]
    public string? ApiKey { get; set; }

    public string? ApiKeyProtected { get; set; }
}

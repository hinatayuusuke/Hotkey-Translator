namespace Hotkey_Translator.Models;

public sealed class FixedCaptureWindowSpec
{
    public long Hwnd { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
}

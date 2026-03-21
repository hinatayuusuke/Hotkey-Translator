namespace Hotkey_Translator.Models;

public sealed record UserGlossaryEntry
{
    public string SourceTerm { get; set; } = string.Empty;
    public string TargetTerm { get; set; } = string.Empty;
    public string? SourceLanguage { get; set; }
    public string? TargetLanguage { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
}

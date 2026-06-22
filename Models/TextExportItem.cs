namespace Hotkey_Translator.Models;

public sealed record TextExportItem(
    int UnitId,
    string OriginalText,
    string TranslationText);

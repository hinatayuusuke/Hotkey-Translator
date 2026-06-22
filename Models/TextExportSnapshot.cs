namespace Hotkey_Translator.Models;

public sealed record TextExportSnapshot(
    DateTimeOffset GeneratedAt,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<TextExportItem> Items)
{
    public bool HasContent => Items.Any(item =>
        !string.IsNullOrWhiteSpace(item.OriginalText) ||
        !string.IsNullOrWhiteSpace(item.TranslationText));

    public static TextExportSnapshot Create(
        IReadOnlyList<ReadingUnit> readingUnits,
        IReadOnlyDictionary<int, string> translations,
        DateTimeOffset generatedAt,
        string sourceLanguage,
        string targetLanguage)
    {
        var items = readingUnits
            .Select(unit => new TextExportItem(
                unit.Id,
                unit.Text,
                translations.TryGetValue(unit.Id, out var translated) ? translated : string.Empty))
            .ToList();

        return new TextExportSnapshot(generatedAt, sourceLanguage, targetLanguage, items);
    }
}

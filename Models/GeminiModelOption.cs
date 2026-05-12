namespace Hotkey_Translator.Models;

public sealed record GeminiModelOption(
    string Value,
    string Display,
    string Description,
    int? InputTokenLimit,
    int? OutputTokenLimit);

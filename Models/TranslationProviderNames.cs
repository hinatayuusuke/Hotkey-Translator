namespace Hotkey_Translator.Models;

public static class TranslationProviderNames
{
    public const string Gemini = "Gemini";
    public const string DeepL = "DeepL";
    public const string GoogleWeb = "GoogleWeb";

    public static readonly string[] Defaults = { Gemini, DeepL, GoogleWeb };
}

using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class CacheKeyBuilder
{
    public string Build(AppSettings settings, string normalizedText)
    {
        return $"{settings.SourceLanguage}_{settings.TargetLanguage}_{settings.StyleId}_{settings.GlossaryVersion}_{normalizedText}";
    }
}

using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class CacheKeyBuilder
{
    private readonly UserGlossaryService _userGlossaryService;

    public CacheKeyBuilder(UserGlossaryService userGlossaryService)
    {
        _userGlossaryService = userGlossaryService;
    }

    public string Build(AppSettings settings, string normalizedText)
    {
        var glossaryScope = _userGlossaryService.BuildGlossaryScope(settings);
        return $"{settings.SourceLanguage}_{settings.TargetLanguage}_{settings.StyleId}_{glossaryScope}_{normalizedText}";
    }
}

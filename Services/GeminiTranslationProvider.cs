using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class GeminiTranslationProvider : ITranslationProvider
{
    private readonly GeminiClient _client;

    public GeminiTranslationProvider(GeminiClient client)
    {
        _client = client;
    }

    public string Name => TranslationProviderNames.Gemini;

    public bool IsEnabled(AppSettings settings)
    {
        return settings.EnableGemini && !string.IsNullOrWhiteSpace(settings.ApiKey);
    }

    public Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<string> texts,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        return _client.TranslateAsync(texts, settings, cancellationToken);
    }
}

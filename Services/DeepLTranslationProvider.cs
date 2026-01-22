using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly AppLogger? _logger;

    public DeepLTranslationProvider(HttpClient httpClient, AppLogger? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string Name => TranslationProviderNames.DeepL;

    public bool IsEnabled(AppSettings settings)
    {
        return settings.EnableDeepL && !string.IsNullOrWhiteSpace(settings.DeepLApiKey);
    }

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<string> texts,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var endpoint = string.IsNullOrWhiteSpace(settings.DeepLEndpoint)
            ? "https://api-free.deepl.com/v2/translate"
            : settings.DeepLEndpoint.Trim();
        var apiKey = settings.DeepLApiKey ?? string.Empty;

        using var content = BuildRequestContent(texts, settings, apiKey);
        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger?.Info($"DeepL HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            return new Dictionary<string, string>();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseTranslations(json, texts);
    }

    private FormUrlEncodedContent BuildRequestContent(IReadOnlyList<string> texts, AppSettings settings, string apiKey)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("auth_key", apiKey),
            new("target_lang", NormalizeLang(settings.TargetLanguage))
        };

        var sourceLang = NormalizeLang(settings.SourceLanguage);
        if (!string.IsNullOrWhiteSpace(sourceLang))
        {
            parameters.Add(new KeyValuePair<string, string>("source_lang", sourceLang));
        }

        foreach (var text in texts)
        {
            parameters.Add(new KeyValuePair<string, string>("text", text));
        }

        return new FormUrlEncodedContent(parameters);
    }

    private IReadOnlyDictionary<string, string> ParseTranslations(string json, IReadOnlyList<string> sources)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("translations", out var translations))
            {
                _logger?.Info("DeepL response missing translations.");
                return new Dictionary<string, string>();
            }

            var results = translations.EnumerateArray()
                .Select(item => item.GetProperty("text").GetString() ?? string.Empty)
                .ToList();

            var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
            var count = Math.Min(sources.Count, results.Count);
            for (var i = 0; i < count; i++)
            {
                if (!string.IsNullOrWhiteSpace(results[i]))
                {
                    mapped[sources[i]] = results[i];
                }
            }

            return mapped;
        }
        catch (Exception ex)
        {
            _logger?.Info($"DeepL response parse failed: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    private static string NormalizeLang(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        var normalized = language.Trim().Replace('_', '-');
        return normalized.ToUpperInvariant();
    }
}

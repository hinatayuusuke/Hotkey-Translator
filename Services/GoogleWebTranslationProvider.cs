using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class GoogleWebTranslationProvider : ITranslationProvider
{
    private const string Endpoint = "https://translate-pa.googleapis.com/v1/translateHtml";
    private const string RequestSource = "wt_lib";
    // COMPAT: This mirrors the reference implementation's unofficial web endpoint contract and may stop working if Google changes it.
    private const string WebApiKey = "AIzaSyATBXajvzQLTDHEQbcpq0Ihe0vWDHmO520";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;
    private readonly AppLogger? _logger;

    public GoogleWebTranslationProvider(HttpClient httpClient, AppLogger? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string Name => TranslationProviderNames.GoogleWeb;

    public bool IsEnabled(AppSettings settings)
    {
        return settings.EnableGoogleWeb;
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

        var payload = JsonSerializer.Serialize(
            new object[]
            {
                new object[]
                {
                    texts,
                    NormalizeSourceLang(settings.SourceLanguage),
                    NormalizeTargetLang(settings.TargetLanguage)
                },
                RequestSource
            },
            PayloadJsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf")
        };
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", WebApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.Info($"GoogleWeb HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            return new Dictionary<string, string>();
        }

        return ParseTranslations(body, texts);
    }

    private IReadOnlyDictionary<string, string> ParseTranslations(string json, IReadOnlyList<string> sources)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                _logger?.Info("GoogleWeb response root is not an array.");
                return new Dictionary<string, string>();
            }

            var translatedTexts = ExtractTranslatedTexts(doc.RootElement[0], sources.Count);
            if (translatedTexts.Count == 0)
            {
                _logger?.Info("GoogleWeb response missing translations.");
                return new Dictionary<string, string>();
            }

            if (translatedTexts.Count != sources.Count)
            {
                _logger?.Info($"GoogleWeb translation count mismatch: in={sources.Count}, out={translatedTexts.Count}.");
            }

            var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
            var count = Math.Min(sources.Count, translatedTexts.Count);
            for (var i = 0; i < count; i++)
            {
                if (!string.IsNullOrWhiteSpace(translatedTexts[i]))
                {
                    mapped[sources[i]] = translatedTexts[i];
                }
            }

            return mapped;
        }
        catch (Exception ex)
        {
            _logger?.Info($"GoogleWeb response parse failed: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    private static List<string> ExtractTranslatedTexts(JsonElement element, int expectedCount)
    {
        var results = new List<string>();
        if (element.ValueKind == JsonValueKind.String)
        {
            results.Add(WebUtility.HtmlDecode(element.GetString() ?? string.Empty));
            return results;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (results.Count == expectedCount)
            {
                break;
            }

            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    results.Add(WebUtility.HtmlDecode(item.GetString() ?? string.Empty));
                    break;
                case JsonValueKind.Array when item.GetArrayLength() > 0 && item[0].ValueKind == JsonValueKind.String:
                    results.Add(WebUtility.HtmlDecode(item[0].GetString() ?? string.Empty));
                    break;
            }
        }

        return results;
    }

    private static string NormalizeSourceLang(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "auto";
        }

        return NormalizeLang(language);
    }

    private static string NormalizeTargetLang(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "ja";
        }

        return NormalizeLang(language);
    }

    private static string NormalizeLang(string language)
    {
        var normalized = language.Trim().Replace('_', '-');
        if (normalized.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-Hant-", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-MO", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-TW";
        }

        if (normalized.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-Hans-", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("zh-SG", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        return normalized;
    }
}

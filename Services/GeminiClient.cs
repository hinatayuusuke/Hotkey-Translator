using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class GeminiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly AppLogger? _logger;

    public GeminiClient(HttpClient httpClient, AppLogger? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<string> texts,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.EnableGemini)
        {
            _logger?.Info("Gemini skipped: disabled.");
            return new Dictionary<string, string>();
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            _logger?.Info("Gemini skipped: API key missing.");
            return new Dictionary<string, string>();
        }

        if (texts.Count == 0)
        {
            _logger?.Info("Gemini skipped: no texts to translate.");
            return new Dictionary<string, string>();
        }

        var endpoint = BuildEndpoint(settings);
        var prompt = BuildPrompt(texts, settings);
        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = prompt } }
                }
            },
            // SECURITY: Safety settings are explicitly set to avoid model-side blocking that would break OCR text mapping.
            safetySettings = new[]
            {
                new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
            },
            generationConfig = new
            {
                temperature = 0.7,
                // NOTE: Cap output to avoid runaway verbose responses that stall the overlay.
                maxOutputTokens = 4096,
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "object",
                    required = new[] { "translations" },
                    properties = new
                    {
                        translations = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        }
                    }
                },
                thinkingConfig = new
                {
                    includeThoughts = false,
                    thinkingBudget = 0
                }
            }
        };

        var payload = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var requestStopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        requestStopwatch.Stop();
        _logger?.Info($"Gemini HTTP: status={(int)response.StatusCode}, latency_ms={requestStopwatch.ElapsedMilliseconds}, count_in={texts.Count}.");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteGeminiRawResponseAsync(
                    body,
                    settings.GeminiModel,
                    texts.Count,
                    requestStopwatch.ElapsedMilliseconds,
                    (int)response.StatusCode,
                    "http_error",
                    cancellationToken)
                .ConfigureAwait(false);
            return new Dictionary<string, string>();
        }

        var jsonText = ExtractJsonText(body);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            _logger?.Info("Gemini response missing JSON text.");
            await WriteGeminiRawResponseAsync(
                    body,
                    settings.GeminiModel,
                    texts.Count,
                    requestStopwatch.ElapsedMilliseconds,
                    (int)response.StatusCode,
                    "missing_json_text",
                    cancellationToken)
                .ConfigureAwait(false);
            return new Dictionary<string, string>();
        }

        if (!TryParseTranslations(jsonText, out var parsedTranslations))
        {
            _logger?.Info("Gemini response parse failed.");
            await WriteGeminiRawResponseAsync(
                    body,
                    settings.GeminiModel,
                    texts.Count,
                    requestStopwatch.ElapsedMilliseconds,
                    (int)response.StatusCode,
                    "parse_failed",
                    cancellationToken)
                .ConfigureAwait(false);
            return new Dictionary<string, string>();
        }

        var translations = RemapByIndex(texts, parsedTranslations);
        _logger?.Info($"Gemini translation counts: in={texts.Count}, out_raw={parsedTranslations.Count}, out_mapped={translations.Count}.");
        if (parsedTranslations.Count < texts.Count)
        {
            // WHY: Short output means partial apply risk; preserve raw payload for postmortem.
            await WriteGeminiRawResponseAsync(
                    body,
                    settings.GeminiModel,
                    texts.Count,
                    requestStopwatch.ElapsedMilliseconds,
                    (int)response.StatusCode,
                    "count_mismatch_short",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return translations;
    }

    private async Task WriteGeminiRawResponseAsync(
        string body,
        string? modelName,
        int itemCount,
        long latencyMs,
        int statusCode,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Hotkey-Translator",
                "debug",
                "gemini");
            Directory.CreateDirectory(root);

            var filePath = Path.Combine(
                root,
                $"gemini_response_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}.txt");
            var builder = new StringBuilder();
            builder.AppendLine($"utc={DateTimeOffset.UtcNow:O}");
            builder.AppendLine($"model={modelName ?? string.Empty}");
            builder.AppendLine($"status={statusCode}");
            builder.AppendLine($"latency_ms={latencyMs}");
            builder.AppendLine($"items={itemCount}");
            builder.AppendLine($"reason={reason}");
            builder.AppendLine("---");
            builder.Append(body);

            await File.WriteAllTextAsync(filePath, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Gemini raw response saved: {filePath}");
        }
        catch (Exception ex)
        {
            // WHY: Debug dump failures must not interrupt translation flow.
            _logger?.Info($"Gemini raw response dump failed: {ex.Message}");
        }
    }

    private static string BuildEndpoint(AppSettings settings)
    {
        return $"{settings.GeminiEndpoint}/{settings.GeminiModel}:generateContent?key={settings.ApiKey}";
    }

    private static string BuildPrompt(IReadOnlyList<string> texts, AppSettings settings)
    {
        var inputJson = JsonSerializer.Serialize(texts);
        var targetLanguage = ResolveGeminiLanguageName(settings.TargetLanguage);
        return $@"Translate each input text into {targetLanguage}.
            Output must be in UTF-8 characters; do not use Unicode escape sequences like \uXXXX.
            Keep the same array length and order as the input.
            Input: {inputJson}";
    }

    private static string ResolveGeminiLanguageName(string? language)
    {
        var normalized = (language ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Japanese";
        }

        if (normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "English";
        }

        if (normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "Japanese";
        }

        if (normalized.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            return "Russian";
        }

        if (IsTraditionalChinese(normalized))
        {
            return "Traditional Chinese";
        }

        if (IsSimplifiedChinese(normalized))
        {
            return "Simplified Chinese";
        }

        return normalized;
    }

    private static bool IsTraditionalChinese(string language)
    {
        return language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
               || language.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
               || language.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)
               || language.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase)
               || language.StartsWith("zh-Hant-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSimplifiedChinese(string language)
    {
        return language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
               || language.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
               || language.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
               || language.StartsWith("zh-Hans-", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractJsonText(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var candidate = doc.RootElement.GetProperty("candidates")[0];
            var parts = candidate.GetProperty("content").GetProperty("parts");
            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("text", out var textElement))
                {
                    continue;
                }

                // WHY: Gemini may emit "thought" parts before the final JSON response.
                var isThought = part.TryGetProperty("thought", out var thoughtElement) && thoughtElement.GetBoolean();
                if (!isThought)
                {
                    return textElement.GetString();
                }
            }

            return parts[0].GetProperty("text").GetString();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseTranslations(string jsonText, out List<string> translations)
    {
        translations = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (!doc.RootElement.TryGetProperty("translations", out var translationsElement)
                || translationsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in translationsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    translations.Add(item.GetString() ?? string.Empty);
                    continue;
                }

                // COMPAT: Accept legacy object response during rollout and extract translated_text.
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("translated_text", out var translatedElement)
                    && translatedElement.ValueKind == JsonValueKind.String)
                {
                    translations.Add(translatedElement.GetString() ?? string.Empty);
                    continue;
                }

                translations.Add(string.Empty);
            }

            return true;
        }
        catch
        {
            translations = new List<string>();
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> RemapByIndex(
        IReadOnlyList<string> sourceTexts,
        IReadOnlyList<string> translatedTexts)
    {
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        var limit = Math.Min(sourceTexts.Count, translatedTexts.Count);
        for (var i = 0; i < limit; i++)
        {
            var translated = translatedTexts[i];
            if (string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }

            // WHY: Provider interface is keyed by source text; duplicate keys intentionally keep the latest value.
            mapped[sourceTexts[i]] = translated;
        }

        return mapped;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            safety_settings = new[]
            {
                new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
            },
            generationConfig = new
            {
                temperature = 0.2,
                // NOTE: Cap output to avoid runaway verbose responses that stall the overlay.
                maxOutputTokens = 5000,
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        translations = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    source_text = new { type = "string" },
                                    translated_text = new { type = "string" }
                                },
                                required = new[] { "source_text", "translated_text" }
                            }
                        }
                    }
                }
            }
        };

        var payload = JsonSerializer.Serialize(requestBody, JsonOptions);
        _logger?.Info($"Gemini request prepared: {texts.Count} items, {payload.Length} chars.");
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var requestStopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        requestStopwatch.Stop();
        _logger?.Info($"Gemini HTTP {(int)response.StatusCode} {response.ReasonPhrase} in {requestStopwatch.ElapsedMilliseconds} ms.");
        if (!response.IsSuccessStatusCode)
        {
            return new Dictionary<string, string>();
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger?.Info($"Gemini response body length: {body.Length} chars.");
        var jsonText = ExtractJsonText(body);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            _logger?.Info("Gemini response missing JSON text.");
            return new Dictionary<string, string>();
        }

        _logger?.Info($"Gemini response JSON text length: {jsonText.Length} chars.");
        var translations = ParseTranslations(jsonText);
        _logger?.Info($"Gemini translations parsed: {translations.Count}.");
        if (translations.Count > 0)
        {
            for (var i = 0; i < texts.Count; i++)
            {
                if (translations.TryGetValue(texts[i], out var translated))
                {
                    _logger?.Info($"Gemini translation length[{i}]: {translated.Length} chars.");
                }
            }
        }
        return translations;
    }

    private static string BuildEndpoint(AppSettings settings)
    {
        return $"{settings.GeminiEndpoint}/{settings.GeminiModel}:generateContent?key={settings.ApiKey}";
    }

    private static string BuildPrompt(IReadOnlyList<string> texts, AppSettings settings)
    {
        var inputJson = JsonSerializer.Serialize(texts, JsonOptions);
        return $"Translate each string from {settings.SourceLanguage} to {settings.TargetLanguage}. " +
               "Return JSON with 'translations' array. Keep order and count unchanged. " +
               $"Input array: {inputJson}";
    }

    private static string? ExtractJsonText(string rawResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var candidate = doc.RootElement.GetProperty("candidates")[0];
            var content = candidate.GetProperty("content");
            var part = content.GetProperty("parts")[0];
            return part.GetProperty("text").GetString();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseTranslations(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (!doc.RootElement.TryGetProperty("translations", out var translations))
            {
                return new Dictionary<string, string>();
            }

            return translations.EnumerateArray()
                .Select(item => new TranslationItem(
                    item.GetProperty("source_text").GetString() ?? string.Empty,
                    item.GetProperty("translated_text").GetString() ?? string.Empty))
                .Where(item => !string.IsNullOrWhiteSpace(item.SourceText))
                .ToDictionary(item => item.SourceText, item => item.TranslatedText, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}

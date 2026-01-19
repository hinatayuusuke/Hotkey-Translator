using System;
using System.Collections.Generic;
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

    public GeminiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<string> texts,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.EnableGemini || string.IsNullOrWhiteSpace(settings.ApiKey) || texts.Count == 0)
        {
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
                response_mime_type = "application/json",
                response_schema = new
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
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new Dictionary<string, string>();
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var jsonText = ExtractJsonText(body);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return new Dictionary<string, string>();
        }

        return ParseTranslations(jsonText);
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

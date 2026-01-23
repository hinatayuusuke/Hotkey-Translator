using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class PaddleVllmOcrProvider : IOcrProvider
{
    private const string DataUrlPrefix = "data:image/png;base64,";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly AppLogger? _logger;

    public PaddleVllmOcrProvider(HttpClient httpClient, AppLogger? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var baseUrl = settings.VllmBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("vLLM base URL is not set.");
        }

        var model = settings.VllmModelName?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("vLLM model name is not set.");
        }

        var endpoint = BuildEndpoint(baseUrl);
        var imageUrl = BuildImageDataUrl(bitmap);
        var requestBody = new
        {
            model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = imageUrl } },
                        new { type = "text", text = BuildPrompt() }
                    }
                }
            },
            temperature = 0.0
        };

        var payload = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var apiKey = settings.VllmApiKey?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger?.Info($"vLLM OCR HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            throw new InvalidOperationException("vLLM OCR request failed.");
        }

        var content = ExtractMessageContent(body);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("vLLM response missing message content.");
        }

        var json = ExtractJson(content) ?? content;
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("vLLM response JSON is empty.");
        }

        var parsed = JsonSerializer.Deserialize<VllmOcrResponse>(json, JsonOptions);
        if (parsed?.Lines == null)
        {
            throw new InvalidOperationException("vLLM response is missing lines.");
        }

        var lines = new List<OcrLine>(parsed.Lines.Count);
        foreach (var line in parsed.Lines)
        {
            if (line.Box == null || line.Box.Length < 4)
            {
                continue;
            }

            var rect = new Rect(line.Box[0], line.Box[1], line.Box[2], line.Box[3]);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            var confidence = line.Confidence.HasValue ? (float)line.Confidence.Value : 1.0f;
            lines.Add(new OcrLine(line.Text ?? string.Empty, rect, confidence, 1, rect.Height));
        }

        return new OcrResultModel(lines, bitmap.Width, bitmap.Height);
    }

    private static string BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return $"{trimmed}/chat/completions";
    }

    private static string BuildImageDataUrl(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var base64 = Convert.ToBase64String(stream.ToArray());
        return DataUrlPrefix + base64;
    }

    private static string BuildPrompt()
    {
        return "Perform OCR on the image. Return only JSON in the format: "
               + "{\"lines\":[{\"text\":\"...\",\"box\":[x,y,w,h],\"confidence\":0.98}]}. "
               + "Use pixel coordinates relative to the input image.";
    }

    private static string? ExtractMessageContent(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var message))
            {
                return null;
            }

            if (!message.TryGetProperty("content", out var content))
            {
                return null;
            }

            return content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        // WHY: Models sometimes wrap JSON with extra text; trim to the first/last braces.
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return content.Substring(start, end - start + 1);
    }

    private sealed class VllmOcrResponse
    {
        public List<VllmOcrLine>? Lines { get; set; }
    }

    private sealed class VllmOcrLine
    {
        public string? Text { get; set; }
        public double[]? Box { get; set; }
        public double? Confidence { get; set; }
    }
}

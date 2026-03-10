using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.OcrGrpc;

namespace Hotkey_Translator.Services;

public sealed class VisionLlmGrpcOcrProvider : IOcrProvider, IDisposable
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex LineBreakRegex = new(@"\r\n|\r|\n", RegexOptions.Compiled);
    private readonly AppLogger? _logger;
    private readonly object _lock = new();
    private string? _endpoint;
    private GrpcChannel? _channel;
    private static bool _http2Enabled;

    public VisionLlmGrpcOcrProvider(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var endpoint = ResolveEndpoint(settings);
        var client = ResolveClient(endpoint);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        _logger?.Info($"stage=ocr_grpc host=vision_llm event=request_bytes bytes={stream.Length}.");

        var request = new OcrRequest
        {
            Image = Google.Protobuf.ByteString.CopyFrom(stream.ToArray()),
            Language = settings.SourceLanguage ?? string.Empty,
            // WHY: Hybrid mode needs VisionLLM to preserve visible lines so geometry matching can
            // align line-by-line instead of fighting the text-only sentence merge prompt.
            PreserveVisualLines = settings.EnableVisionGeometryHybridOcr
        };

        var response = await client.RecognizeAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response.Json))
        {
            throw new InvalidOperationException("VisionLLM gRPC returned empty JSON.");
        }

        var parsed = JsonSerializer.Deserialize<VisionLlmOcrResponse>(response.Json, JsonOptions)
            ?? throw new InvalidOperationException("VisionLLM gRPC returned invalid JSON.");
        var lines = BuildSyntheticLines(parsed.Text, bitmap.Width, bitmap.Height);
        _logger?.Info($"VisionLLM gRPC returned {lines.Count} synthesized lines.");
        return new OcrResultModel(lines, bitmap.Width, bitmap.Height);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _channel?.Dispose();
            _channel = null;
        }
    }

    private static List<OcrLine> BuildSyntheticLines(string? text, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<OcrLine>();
        }

        var split = LineBreakRegex
            .Split(text)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        if (split.Count == 0)
        {
            return new List<OcrLine>();
        }

        var lineHeight = Math.Max(1.0, height / (double)split.Count);
        var lines = new List<OcrLine>(split.Count);
        for (var i = 0; i < split.Count; i++)
        {
            var y = Math.Min(height - 1.0, i * lineHeight);
            var rectHeight = Math.Max(1.0, Math.Min(height - y, lineHeight));
            var rect = new Rect(0, y, Math.Max(1.0, width), rectHeight);
            // WHY: VisionLLM text-only OCR currently has no geometry. Use stable full-width strips so the
            // existing grouping/overlay pipeline can render something until a coordinate path is reintroduced.
            lines.Add(new OcrLine(split[i], rect, 1.0f, 1, rect.Height));
        }

        return lines;
    }

    private OcrService.OcrServiceClient ResolveClient(string endpoint)
    {
        lock (_lock)
        {
            if (_channel == null || !string.Equals(_endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            {
                if (!_http2Enabled)
                {
                    AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
                    _http2Enabled = true;
                }

                _endpoint = endpoint;
                _channel?.Dispose();
                _channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
                {
                    MaxReceiveMessageSize = MaxMessageBytes,
                    MaxSendMessageSize = MaxMessageBytes
                });
            }

            return new OcrService.OcrServiceClient(_channel);
        }
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.VisionLlmGrpcEndpoint))
        {
            return settings.VisionLlmGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.VisionLlmGrpcHost) ? "127.0.0.1" : settings.VisionLlmGrpcHost.Trim();
        var port = settings.VisionLlmGrpcPort <= 0 ? 50074 : settings.VisionLlmGrpcPort;
        return $"http://{host}:{port}";
    }

    private sealed class VisionLlmOcrResponse
    {
        public string? Text { get; set; }
    }
}

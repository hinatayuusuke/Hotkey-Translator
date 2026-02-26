using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.OcrGrpc;

namespace Hotkey_Translator.Services;

public sealed class NdlGrpcOcrProvider : IOcrProvider, IDisposable
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly AppLogger? _logger;
    private readonly object _lock = new();
    private string? _endpoint;
    private GrpcChannel? _channel;
    private static bool _http2Enabled;

    public NdlGrpcOcrProvider(AppLogger? logger = null)
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
        _logger?.Info($"stage=ocr_grpc host=ndl event=request_bytes bytes={stream.Length}.");

        var request = new OcrRequest
        {
            Image = Google.Protobuf.ByteString.CopyFrom(stream.ToArray())
        };

        var response = await client.RecognizeAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response.Json))
        {
            throw new InvalidOperationException("NDLOCR gRPC returned empty JSON.");
        }

        var parsed = JsonSerializer.Deserialize<NdlOcrResponse>(response.Json, JsonOptions);
        if (parsed?.Lines is null)
        {
            throw new InvalidOperationException("NDLOCR gRPC response is missing lines.");
        }

        var lines = new List<OcrLine>(parsed.Lines.Count);
        foreach (var line in parsed.Lines)
        {
            if (line.Box is null || line.Box.Length < 4)
            {
                continue;
            }

            var rect = new Rect(line.Box[0], line.Box[1], line.Box[2], line.Box[3]);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            var confidence = line.Confidence ?? line.RecognitionConfidence ?? line.DetectionConfidence ?? 1.0;
            lines.Add(new OcrLine(line.Text ?? string.Empty, rect, (float)confidence, 1, rect.Height));
        }

        _logger?.Info($"NDLOCR gRPC returned {lines.Count} lines.");
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

    private OcrService.OcrServiceClient ResolveClient(string endpoint)
    {
        lock (_lock)
        {
            if (_channel == null || !string.Equals(_endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            {
                if (!_http2Enabled)
                {
                    // WHY: gRPC uses HTTP/2; enable cleartext support for localhost endpoints.
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
        if (!string.IsNullOrWhiteSpace(settings.NdlGrpcEndpoint))
        {
            return settings.NdlGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.NdlGrpcHost) ? "127.0.0.1" : settings.NdlGrpcHost.Trim();
        var port = settings.NdlGrpcPort <= 0 ? 50053 : settings.NdlGrpcPort;
        return $"http://{host}:{port}";
    }

    private sealed class NdlOcrResponse
    {
        public List<NdlOcrLine>? Lines { get; set; }
    }

    private sealed class NdlOcrLine
    {
        public string? Text { get; set; }
        public double[]? Box { get; set; }
        public double? Confidence { get; set; }
        public double? DetectionConfidence { get; set; }
        public double? RecognitionConfidence { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.TranslationGrpc;

namespace Hotkey_Translator.Services;

public sealed class LlamaGrpcTranslationProvider : ITranslationProvider
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private readonly AppLogger? _logger;
    private readonly object _lock = new();
    private string? _endpoint;
    private GrpcChannel? _channel;
    private static bool _http2Enabled;

    public LlamaGrpcTranslationProvider(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public string Name => TranslationProviderNames.LlamaCpp;

    public bool IsEnabled(AppSettings settings)
    {
        return settings.EnableLlamaCppTranslation;
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

        var endpoint = ResolveEndpoint(settings);
        var client = ResolveClient(endpoint);

        var request = new TranslateRequest
        {
            SourceLang = settings.SourceLanguage ?? string.Empty,
            TargetLang = settings.TargetLanguage ?? string.Empty
        };
        request.Texts.AddRange(texts);

        try
        {
            var response = await client.TranslateAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response.Translations.Count == 0)
            {
                _logger?.Info("Llama gRPC returned empty translations.");
                return new Dictionary<string, string>();
            }

            var results = response.Translations.ToList();
            var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
            var count = Math.Min(texts.Count, results.Count);
            for (var i = 0; i < count; i++)
            {
                if (!string.IsNullOrWhiteSpace(results[i]))
                {
                    mapped[texts[i]] = results[i];
                }
            }

            return mapped;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
        {
            _logger?.Info($"Llama gRPC busy: {ex.Status.Detail}");
            return new Dictionary<string, string>();
        }
    }

    private TranslationService.TranslationServiceClient ResolveClient(string endpoint)
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

            return new TranslationService.TranslationServiceClient(_channel);
        }
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LlamaGrpcEndpoint))
        {
            return settings.LlamaGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.LlamaGrpcHost) ? "127.0.0.1" : settings.LlamaGrpcHost.Trim();
        var port = settings.LlamaGrpcPort <= 0 ? 50071 : settings.LlamaGrpcPort;
        return $"http://{host}:{port}";
    }
}

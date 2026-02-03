using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.TranslationGrpc;

namespace Hotkey_Translator.Services;

public sealed class CTranslate2GrpcTranslationProvider : ITranslationProvider
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private readonly AppLogger? _logger;
    private readonly object _lock = new();
    private string? _endpoint;
    private GrpcChannel? _channel;
    private static bool _http2Enabled;

    public CTranslate2GrpcTranslationProvider(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public string Name => TranslationProviderNames.CTranslate2;

    public bool IsEnabled(AppSettings settings)
    {
        return settings.EnableCTranslate2;
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
        var sourceLang = ResolveNllbLanguage(settings.SourceLanguage, "eng_Latn");
        var targetLang = ResolveNllbLanguage(settings.TargetLanguage, "eng_Latn");

        var request = new TranslateRequest
        {
            SourceLang = sourceLang,
            TargetLang = targetLang
        };
        request.Texts.AddRange(texts);

        var response = await client.TranslateAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Translations.Count == 0)
        {
            _logger?.Info("CTranslate2 gRPC returned empty translations.");
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
        if (!string.IsNullOrWhiteSpace(settings.CTranslate2GrpcEndpoint))
        {
            return settings.CTranslate2GrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.CTranslate2GrpcHost) ? "127.0.0.1" : settings.CTranslate2GrpcHost.Trim();
        var port = settings.CTranslate2GrpcPort <= 0 ? 50061 : settings.CTranslate2GrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveNllbLanguage(string? language, string fallback)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return fallback;
        }

        var trimmed = language.Trim();
        if (trimmed.Contains('_') && trimmed.Length >= 5)
        {
            return trimmed;
        }

        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "eng_Latn";
        }

        if (trimmed.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "jpn_Jpan";
        }

        if (trimmed.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            return "rus_Cyrl";
        }

        if (trimmed.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return "kor_Hang";
        }

        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zho_Hans";
        }

        return fallback;
    }
}

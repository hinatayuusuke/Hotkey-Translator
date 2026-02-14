using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.GrpcHost;

namespace Hotkey_Translator.Services.Application;

internal sealed class ResourceHostFacade : IDisposable
{
    private const string HostIdPaddle = "paddle_grpc";
    private const string HostIdPaddleVl = "paddle_vl_grpc";
    private const string HostIdCt2 = "ct2_grpc";
    private const string HostIdLlama = "llama_grpc";

    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Action<bool, string?> _setBusyOverlay;
    private readonly Action<AppSettings, bool> _syncSettingsToView;
    private readonly Action<string> _showLoadFailure;
    private readonly SemaphoreSlim _resourceLoadGate = new(1, 1);
    private readonly GrpcHostOrchestrator _hostOrchestrator;
    private readonly PaddleGrpcHost _paddleGrpcHost;
    private readonly PaddleVlGrpcHost _paddleVlGrpcHost;
    private readonly CTranslate2GrpcHost _ct2GrpcHost;
    private readonly LlamaGrpcHost _llamaGrpcHost;
    private readonly GrpcHostRegistry _hostRegistry;

    private CTranslate2HostConfig? _ct2HostConfig;
    private LlamaHostConfig? _llamaHostConfig;

    public ResourceHostFacade(
        Func<AppLogger?> loggerAccessor,
        Action<bool, string?> setBusyOverlay,
        Action<AppSettings, bool> syncSettingsToView,
        Action<string> showLoadFailure)
    {
        _loggerAccessor = loggerAccessor;
        _setBusyOverlay = setBusyOverlay;
        _syncSettingsToView = syncSettingsToView;
        _showLoadFailure = showLoadFailure;

        _hostOrchestrator = new GrpcHostOrchestrator(_loggerAccessor, _setBusyOverlay, _showLoadFailure);
        _paddleGrpcHost = new PaddleGrpcHost(_loggerAccessor());
        _paddleVlGrpcHost = new PaddleVlGrpcHost(_loggerAccessor());
        _ct2GrpcHost = new CTranslate2GrpcHost(_loggerAccessor());
        _llamaGrpcHost = new LlamaGrpcHost(_loggerAccessor());
        _hostRegistry = new GrpcHostRegistry(BuildHostDescriptors());
    }

    public bool IsPaddleVlRunning => _paddleVlGrpcHost.IsRunning;

    public async Task<bool> EnsureResourceHostsAsync(AppSettings settings)
    {
        if (!ShouldLoadPaddle(settings) && !ShouldLoadPaddleVl(settings) &&
            !ShouldLoadCTranslate2(settings) && !ShouldLoadLlama(settings))
        {
            return false;
        }

        await _resourceLoadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            return await _hostOrchestrator
                .EnsureHostsAsync(settings, _hostRegistry, CancellationToken.None)
                .ConfigureAwait(true);
        }
        finally
        {
            _resourceLoadGate.Release();
        }
    }

    public void StopPaddle()
    {
        _paddleGrpcHost.Stop();
    }

    public void StopPaddleVl()
    {
        _paddleVlGrpcHost.Stop();
    }

    public void StopCTranslate2()
    {
        _ct2GrpcHost.Stop();
        _ct2HostConfig = null;
    }

    public void StopLlama()
    {
        _llamaGrpcHost.Stop();
        _llamaHostConfig = null;
    }

    public void StopAll()
    {
        StopPaddle();
        StopPaddleVl();
        StopCTranslate2();
        StopLlama();
    }

    public void Dispose()
    {
        StopAll();
        _resourceLoadGate.Dispose();
    }

    private IReadOnlyList<GrpcHostDescriptor> BuildHostDescriptors()
    {
        return new List<GrpcHostDescriptor>
        {
            new()
            {
                HostId = HostIdPaddle,
                ShouldLoad = ShouldLoadPaddle,
                IsRunning = () => _paddleGrpcHost.IsRunning,
                StartAsync = (settings, token) => _paddleGrpcHost.StartAsync(settings, token),
                Stop = () => _paddleGrpcHost.Stop(),
                BusyMessage = _ => "Loading PaddleOCR...",
                DisableOnFailure = DisablePaddleOcr,
                FailureLogMessage = "Paddle gRPC host failed to start.",
                FailureUserMessage = "Failed to load PaddleOCR. The setting has been turned OFF. See the logs for details.",
                StopBeforeStartHostIds = new[] { HostIdPaddleVl }
            },
            new()
            {
                HostId = HostIdPaddleVl,
                ShouldLoad = ShouldLoadPaddleVl,
                IsRunning = () => _paddleVlGrpcHost.IsRunning,
                StartAsync = (settings, token) => _paddleVlGrpcHost.StartAsync(settings, token),
                Stop = () => _paddleVlGrpcHost.Stop(),
                BusyMessage = _ => "Loading PaddleOCR-VL...",
                DisableOnFailure = DisablePaddleVlOcr,
                FailureLogMessage = "PaddleOCR-VL gRPC host failed to start.",
                FailureUserMessage = "Failed to load PaddleOCR-VL. The setting has been turned OFF. See the logs for details.",
                StopBeforeStartHostIds = new[] { HostIdPaddle }
            },
            new()
            {
                HostId = HostIdCt2,
                ShouldLoad = ShouldLoadCTranslate2,
                IsRunning = () => _ct2GrpcHost.IsRunning,
                StartAsync = (settings, token) => _ct2GrpcHost.StartAsync(settings, token),
                Stop = () => _ct2GrpcHost.Stop(),
                BusyMessage = _ => "Loading CTranslate2...",
                DisableOnFailure = DisableCTranslate2,
                FailureLogMessage = "CTranslate2 gRPC host failed to start.",
                FailureUserMessage = "Failed to load CTranslate2. The setting has been turned OFF. See the logs for details.",
                HasDeferredConfigChange = settings =>
                    _ct2HostConfig.HasValue && !_ct2HostConfig.Value.Equals(BuildCTranslate2HostConfig(settings)),
                OnDeferredConfigDetected = () =>
                    _loggerAccessor()?.Info("CTranslate2 settings changed; reload deferred until restart."),
                OnStartSucceeded = settings => _ct2HostConfig = BuildCTranslate2HostConfig(settings),
                OnStopped = () => _ct2HostConfig = null
            },
            new()
            {
                HostId = HostIdLlama,
                ShouldLoad = ShouldLoadLlama,
                IsRunning = () => _llamaGrpcHost.IsRunning,
                StartAsync = (settings, token) => _llamaGrpcHost.StartAsync(settings, token),
                Stop = () => _llamaGrpcHost.Stop(),
                BusyMessage = _ => "Loading Llama.cpp...",
                DisableOnFailure = DisableLlamaTranslation,
                FailureLogMessage = "Llama gRPC host failed to start.",
                FailureUserMessage = "Failed to load Llama.cpp. The setting has been turned OFF. See the logs for details.",
                StopBeforeStartHostIds = new[] { HostIdCt2 },
                HasDeferredConfigChange = settings =>
                    _llamaHostConfig.HasValue && !_llamaHostConfig.Value.Equals(BuildLlamaHostConfig(settings)),
                OnDeferredConfigDetected = () =>
                    _loggerAccessor()?.Info("Llama settings changed; reload deferred until restart."),
                OnStartSucceeded = settings => _llamaHostConfig = BuildLlamaHostConfig(settings),
                OnStopped = () => _llamaHostConfig = null
            }
        };
    }

    private static bool ShouldLoadPaddle(AppSettings settings)
    {
        return settings.OcrEngine == OcrEngineKind.Paddle && settings.EnablePaddleGrpcHost;
    }

    private static bool ShouldLoadPaddleVl(AppSettings settings)
    {
        return settings.OcrEngine == OcrEngineKind.PaddleVllm && settings.EnablePaddleVlGrpcHost;
    }

    private static bool ShouldLoadCTranslate2(AppSettings settings)
    {
        // WHY: CTranslate2 translation path is retired; keep host disabled even if legacy settings remain.
        return false;
    }

    private static bool ShouldLoadLlama(AppSettings settings)
    {
        return settings.EnableLlamaCppTranslation;
    }

    private void DisablePaddleOcr(AppSettings settings)
    {
        settings.OcrEngine = OcrEngineKind.WinRt;
        _syncSettingsToView(settings, false);
    }

    private void DisablePaddleVlOcr(AppSettings settings)
    {
        settings.OcrEngine = OcrEngineKind.WinRt;
        _syncSettingsToView(settings, false);
    }

    private void DisableCTranslate2(AppSettings settings)
    {
        settings.EnableCTranslate2 = false;
        _syncSettingsToView(settings, true);
    }

    private void DisableLlamaTranslation(AppSettings settings)
    {
        settings.EnableLlamaCppTranslation = false;
        _syncSettingsToView(settings, true);
    }

    private static CTranslate2HostConfig BuildCTranslate2HostConfig(AppSettings settings)
    {
        SettingsUiController.NormalizeCTranslate2Settings(settings);
        return new CTranslate2HostConfig(
            settings.CTranslate2Device,
            settings.CTranslate2Precision,
            settings.CTranslate2ModelId,
            settings.CTranslate2ModelDir,
            settings.CTranslate2GrpcEndpoint,
            settings.CTranslate2GrpcHost,
            settings.CTranslate2GrpcPort,
            settings.CTranslate2GrpcProjectDir,
            settings.CTranslate2GrpcUvPath,
            settings.CTranslate2GrpcServerScript);
    }

    private static LlamaHostConfig BuildLlamaHostConfig(AppSettings settings)
    {
        SettingsUiController.NormalizeLlamaSettings(settings);
        return new LlamaHostConfig(
            settings.LlamaHost,
            settings.LlamaPort,
            settings.LlamaContextSize,
            settings.LlamaGpuLayers,
            settings.LlamaThreads,
            settings.LlamaParallel,
            settings.LlamaBatchSize,
            settings.LlamaMaxTokens,
            settings.LlamaTemperature,
            settings.LlamaTopP,
            settings.LlamaTopK,
            settings.LlamaRepeatPenalty,
            settings.LlamaSelectedModelFileName,
            settings.LlamaGrpcEndpoint,
            settings.LlamaGrpcHost,
            settings.LlamaGrpcPort,
            settings.LlamaGrpcProjectDir,
            settings.LlamaGrpcUvPath,
            settings.LlamaGrpcServerScript);
    }

    private readonly record struct CTranslate2HostConfig(
        string Device,
        string Precision,
        string ModelId,
        string? ModelDir,
        string Endpoint,
        string Host,
        int Port,
        string ProjectDir,
        string UvPath,
        string ServerScript);

    private readonly record struct LlamaHostConfig(
        string Host,
        int Port,
        int ContextSize,
        int GpuLayers,
        int Threads,
        int Parallel,
        int BatchSize,
        int MaxTokens,
        double Temperature,
        double TopP,
        int TopK,
        double RepeatPenalty,
        string ModelFileName,
        string Endpoint,
        string GrpcHost,
        int GrpcPort,
        string ProjectDir,
        string UvPath,
        string ServerScript);
}

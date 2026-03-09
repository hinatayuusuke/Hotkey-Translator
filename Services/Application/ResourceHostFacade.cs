using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.GrpcHost;
using Hotkey_Translator.Services.Settings;
using Hotkey_Translator.Services.Settings.FeatureSettings;

namespace Hotkey_Translator.Services.Application;

internal sealed class ResourceHostFacade : IDisposable
{
    private const string HostIdPaddle = "paddle_grpc";
    private const string HostIdPaddleVl = "paddle_vl_grpc";
    private const string HostIdNdl = "ndl_grpc";
    private const string HostIdVisionLlm = "vision_llm_grpc";
    private const string HostIdLlama = "llama_grpc";

    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Action<bool, string?> _setBusyOverlay;
    private readonly Action<AppSettings, bool> _syncSettingsToView;
    private readonly Action<string> _showLoadFailure;
    private readonly SemaphoreSlim _resourceLoadGate = new(1, 1);
    private readonly GrpcHostOrchestrator _hostOrchestrator;
    private readonly PaddleGrpcHost _paddleGrpcHost;
    private readonly PaddleVlGrpcHost _paddleVlGrpcHost;
    private readonly NdlGrpcHost _ndlGrpcHost;
    private readonly VisionLlmGrpcHost _visionLlmGrpcHost;
    private readonly LlamaGrpcHost _llamaGrpcHost;
    private readonly GrpcHostRegistry _hostRegistry;
    private readonly FeatureSettingsProvider _featureSettingsProvider = new();

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
        // WHY: Host instances can be created before OnLoaded assigns AppLogger; use accessor to avoid capturing null.
        _paddleGrpcHost = new PaddleGrpcHost(_loggerAccessor);
        _paddleVlGrpcHost = new PaddleVlGrpcHost(_loggerAccessor);
        _ndlGrpcHost = new NdlGrpcHost(_loggerAccessor);
        _visionLlmGrpcHost = new VisionLlmGrpcHost(_loggerAccessor);
        _llamaGrpcHost = new LlamaGrpcHost(_loggerAccessor);
        _hostRegistry = new GrpcHostRegistry(BuildHostDescriptors());
    }

    public bool IsPaddleVlRunning => _paddleVlGrpcHost.IsRunning;

    public bool IsVisionLlmRunning => _visionLlmGrpcHost.IsRunning;

    public async Task<bool> EnsureResourceHostsAsync(AppSettings settings)
    {
        await _resourceLoadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            StopHostsNoLongerNeeded(settings);

            if (!ShouldLoadPaddle(settings) && !ShouldLoadPaddleVl(settings) && !ShouldLoadNdl(settings) &&
                !ShouldLoadVisionLlm(settings) &&
                !ShouldLoadLlama(settings))
            {
                return false;
            }

            if (ShouldLoadVisionLlm(settings) && UseVisionSharedLocalTranslation(settings))
            {
                // WHY: Shared VisionLLM translation must not keep the pure-translation llama host resident,
                // otherwise the same family of models occupies VRAM twice.
                StopLlama();
            }

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

    public void StopNdl()
    {
        _ndlGrpcHost.Stop();
    }

    public void StopVisionLlm()
    {
        _visionLlmGrpcHost.Stop();
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
        StopNdl();
        StopVisionLlm();
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
                // WHY: Allow Paddle + NDL to coexist (hot-switch ready). Keep PaddleVL and VisionLLM exclusive.
                StopBeforeStartHostIds = new[] { HostIdPaddleVl, HostIdVisionLlm }
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
                StopBeforeStartHostIds = new[] { HostIdPaddle, HostIdNdl, HostIdVisionLlm }
            },
            new()
            {
                HostId = HostIdNdl,
                ShouldLoad = ShouldLoadNdl,
                IsRunning = () => _ndlGrpcHost.IsRunning,
                StartAsync = (settings, token) => _ndlGrpcHost.StartAsync(settings, token),
                Stop = () => _ndlGrpcHost.Stop(),
                BusyMessage = _ => "Loading NDLOCR-Lite...",
                DisableOnFailure = DisableNdlOcr,
                FailureLogMessage = "NDLOCR gRPC host failed to start.",
                FailureUserMessage = "Failed to load NDLOCR-Lite. The setting has been turned OFF. See the logs for details.",
                // WHY: Allow NDL + Paddle to coexist (hot-switch ready). Keep PaddleVL exclusive.
                StopBeforeStartHostIds = new[] { HostIdPaddleVl }
            },
            new()
            {
                HostId = HostIdVisionLlm,
                ShouldLoad = ShouldLoadVisionLlm,
                IsRunning = () => _visionLlmGrpcHost.IsRunning,
                StartAsync = (settings, token) => _visionLlmGrpcHost.StartAsync(settings, token),
                Stop = () => _visionLlmGrpcHost.Stop(),
                BusyMessage = _ => "Loading VisionLLM OCR...",
                DisableOnFailure = DisableVisionLlmOcr,
                FailureLogMessage = "VisionLLM gRPC host failed to start.",
                FailureUserMessage = "Failed to load VisionLLM OCR. The setting has been turned OFF. See the logs for details.",
                StopBeforeStartHostIds = new[] { HostIdPaddle, HostIdPaddleVl }
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
                HasDeferredConfigChange = settings =>
                    _llamaHostConfig.HasValue && !_llamaHostConfig.Value.Equals(BuildLlamaHostConfig(settings)),
                OnDeferredConfigDetected = () =>
                    _loggerAccessor()?.Info("Llama settings changed; reload deferred until restart."),
                OnStartSucceeded = settings => _llamaHostConfig = BuildLlamaHostConfig(settings),
                OnStopped = () => _llamaHostConfig = null
            }
        };
    }

    private bool ShouldLoadPaddle(AppSettings settings)
    {
        var host = _featureSettingsProvider.GetHost(settings);
        return host.OcrEngine == OcrEngineKind.Paddle && host.EnablePaddleGrpcHost;
    }

    private bool ShouldLoadPaddleVl(AppSettings settings)
    {
        var host = _featureSettingsProvider.GetHost(settings);
        return host.OcrEngine == OcrEngineKind.PaddleVllm && host.EnablePaddleVlGrpcHost;
    }

    private bool ShouldLoadNdl(AppSettings settings)
    {
        var host = _featureSettingsProvider.GetHost(settings);
        return host.OcrEngine == OcrEngineKind.Ndl && host.EnableNdlGrpcHost;
    }

    private bool ShouldLoadVisionLlm(AppSettings settings)
    {
        var host = _featureSettingsProvider.GetHost(settings);
        return host.OcrEngine == OcrEngineKind.VisionLlm && host.EnableVisionLlmGrpcHost;
    }

    private bool ShouldLoadLlama(AppSettings settings)
    {
        var host = _featureSettingsProvider.GetHost(settings);
        return host.EnableLlamaCppTranslation && !UseVisionSharedLocalTranslation(settings);
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

    private void DisableNdlOcr(AppSettings settings)
    {
        settings.OcrEngine = OcrEngineKind.WinRt;
        _syncSettingsToView(settings, false);
    }

    private void DisableVisionLlmOcr(AppSettings settings)
    {
        settings.OcrEngine = OcrEngineKind.WinRt;
        _syncSettingsToView(settings, false);
    }

    private void DisableLlamaTranslation(AppSettings settings)
    {
        settings.EnableLlamaCppTranslation = false;
        _syncSettingsToView(settings, true);
    }

    private void StopHostsNoLongerNeeded(AppSettings settings)
    {
        if (_paddleGrpcHost.IsRunning && !ShouldKeepPaddleResident(settings))
        {
            _loggerAccessor()?.Info("stage=grpc_host host=paddle_grpc event=stop_unused.");
            StopPaddle();
        }

        if (_paddleVlGrpcHost.IsRunning && !ShouldKeepPaddleVlResident(settings))
        {
            _loggerAccessor()?.Info("stage=grpc_host host=paddle_vl_grpc event=stop_unused.");
            StopPaddleVl();
        }

        if (_ndlGrpcHost.IsRunning && !ShouldKeepNdlResident(settings))
        {
            _loggerAccessor()?.Info("stage=grpc_host host=ndl_grpc event=stop_unused.");
            StopNdl();
        }

        if (_visionLlmGrpcHost.IsRunning && !ShouldKeepVisionLlmResident(settings))
        {
            _loggerAccessor()?.Info("stage=grpc_host host=vision_llm_grpc event=stop_unused.");
            StopVisionLlm();
        }

        if (_llamaGrpcHost.IsRunning && ShouldStopLlamaAsUnused(settings))
        {
            _loggerAccessor()?.Info("stage=grpc_host host=llama_grpc event=stop_unused.");
            StopLlama();
        }
    }

    private static bool UseVisionSharedLocalTranslation(AppSettings settings)
    {
        return settings.OcrEngine == OcrEngineKind.VisionLlm &&
               settings.EnableVisionLlmGrpcHost &&
               settings.EnableVisionLlmSharedLocalTranslation &&
               settings.EnableLlamaCppTranslation;
    }

    private static bool ShouldKeepPaddleResident(AppSettings settings)
    {
        // WHY: Paddle and NDL are intentionally hot-switch ready together, so selecting either OCR
        // keeps both hosts resident. VisionLLM and PaddleOCR-VL stay exclusive because they carry
        // their own heavier pipelines.
        return settings.EnablePaddleGrpcHost &&
               settings.OcrEngine is OcrEngineKind.Paddle or OcrEngineKind.Ndl;
    }

    private static bool ShouldKeepPaddleVlResident(AppSettings settings)
    {
        return settings.EnablePaddleVlGrpcHost &&
               settings.OcrEngine == OcrEngineKind.PaddleVllm;
    }

    private static bool ShouldKeepNdlResident(AppSettings settings)
    {
        return settings.EnableNdlGrpcHost &&
               settings.OcrEngine is OcrEngineKind.Paddle or OcrEngineKind.Ndl;
    }

    private static bool ShouldKeepVisionLlmResident(AppSettings settings)
    {
        if (!settings.EnableVisionLlmGrpcHost)
        {
            return false;
        }

        if (settings.OcrEngine == OcrEngineKind.VisionLlm)
        {
            return true;
        }

        // WHY: When local Llama translation is disabled, VisionLLM can stay warm across WinRT/NDL
        // switches because it is no longer competing with the pure-translation llama host for VRAM.
        return !settings.EnableLlamaCppTranslation &&
               settings.OcrEngine is OcrEngineKind.WinRt or OcrEngineKind.Ndl;
    }

    private static bool ShouldStopLlamaAsUnused(AppSettings settings)
    {
        // WHY: The translation enable toggle controls provider usage, not process lifetime.
        // Keep the pure translation host resident until the user explicitly stops it, unless
        // Vision shared translation needs the VRAM back for its own llama-server instance.
        return UseVisionSharedLocalTranslation(settings);
    }

    private static LlamaHostConfig BuildLlamaHostConfig(AppSettings settings)
    {
        _ = SettingsHostNormalizer.NormalizeLlamaSettings(settings);
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
            settings.LlamaGrpcPort);
    }

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
        int GrpcPort);
}

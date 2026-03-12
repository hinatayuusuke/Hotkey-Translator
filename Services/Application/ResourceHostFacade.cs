using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.GrpcHost;
using Hotkey_Translator.Services.Settings;

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
    private readonly HashSet<string> _plannedHostIds = new(StringComparer.Ordinal);

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

    public bool TryValidateBudget(AppSettings settings, out string? message)
    {
        var requiredHosts = BuildRequiredHosts(settings);
        var requiredWeight = requiredHosts.Sum(static host => host.Weight);
        var limit = GetBudgetLimit(settings);
        if (requiredWeight <= limit)
        {
            message = null;
            return true;
        }

        var hostSummary = string.Join(", ", requiredHosts.Select(host => $"{host.DisplayName}({host.Weight})"));
        message =
            $"The selected OCR/translation combination exceeds the {settings.ResourceBudgetProfile} VRAM budget ({requiredWeight}/{limit}). Required hosts: {hostSummary}.";
        _loggerAccessor()?.Info(
            $"stage=grpc_host_plan event=budget_reject profile={settings.ResourceBudgetProfile} limit={limit} requiredWeight={requiredWeight} hosts=\"{hostSummary}\".");
        return false;
    }

    public async Task<bool> EnsureResourceHostsAsync(AppSettings settings)
    {
        await _resourceLoadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var requiredHosts = BuildRequiredHosts(settings);
            if (!EnsureBudgetForHosts(settings, requiredHosts, out var rejectMessage))
            {
                _showLoadFailure(rejectMessage ?? "Resource host VRAM budget exceeded.");
                return false;
            }

            StopHostsNoLongerNeeded();
            if (_plannedHostIds.Count == 0)
            {
                return false;
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
                ShouldLoad = _ => _plannedHostIds.Contains(HostIdPaddle),
                IsRunning = () => _paddleGrpcHost.IsRunning,
                StartAsync = (settings, token) => _paddleGrpcHost.StartAsync(settings, token),
                Stop = () => _paddleGrpcHost.Stop(),
                BusyMessage = _ => "Loading PaddleOCR...",
                DisableOnFailure = DisablePaddleOcr,
                FailureLogMessage = "Paddle gRPC host failed to start.",
                FailureUserMessage = "Failed to load PaddleOCR. The setting has been turned OFF. See the logs for details."
            },
            new()
            {
                HostId = HostIdPaddleVl,
                ShouldLoad = _ => _plannedHostIds.Contains(HostIdPaddleVl),
                IsRunning = () => _paddleVlGrpcHost.IsRunning,
                StartAsync = (settings, token) => _paddleVlGrpcHost.StartAsync(settings, token),
                Stop = () => _paddleVlGrpcHost.Stop(),
                BusyMessage = _ => "Loading PaddleOCR-VL...",
                DisableOnFailure = DisablePaddleVlOcr,
                FailureLogMessage = "PaddleOCR-VL gRPC host failed to start.",
                FailureUserMessage = "Failed to load PaddleOCR-VL. The setting has been turned OFF. See the logs for details."
            },
            new()
            {
                HostId = HostIdNdl,
                ShouldLoad = _ => _plannedHostIds.Contains(HostIdNdl),
                IsRunning = () => _ndlGrpcHost.IsRunning,
                StartAsync = (settings, token) => _ndlGrpcHost.StartAsync(settings, token),
                Stop = () => _ndlGrpcHost.Stop(),
                BusyMessage = _ => "Loading NDLOCR-Lite...",
                DisableOnFailure = DisableNdlOcr,
                FailureLogMessage = "NDLOCR gRPC host failed to start.",
                FailureUserMessage = "Failed to load NDLOCR-Lite. The setting has been turned OFF. See the logs for details."
            },
            new()
            {
                HostId = HostIdVisionLlm,
                ShouldLoad = _ => _plannedHostIds.Contains(HostIdVisionLlm),
                IsRunning = () => _visionLlmGrpcHost.IsRunning,
                StartAsync = (settings, token) => _visionLlmGrpcHost.StartAsync(settings, token),
                Stop = () => _visionLlmGrpcHost.Stop(),
                BusyMessage = _ => "Loading VisionLLM OCR...",
                DisableOnFailure = DisableVisionLlmOcr,
                FailureLogMessage = "VisionLLM gRPC host failed to start.",
                FailureUserMessage = "Failed to load VisionLLM OCR. The setting has been turned OFF. See the logs for details."
            },
            new()
            {
                HostId = HostIdLlama,
                ShouldLoad = _ => _plannedHostIds.Contains(HostIdLlama),
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

    private bool EnsureBudgetForHosts(AppSettings settings, IReadOnlyList<RequiredHost> requiredHosts, out string? rejectMessage)
    {
        var limit = GetBudgetLimit(settings);
        var requiredHostIds = requiredHosts.Select(static host => host.HostId).ToHashSet(StringComparer.Ordinal);
        var requiredWeight = requiredHosts.Sum(static host => host.Weight);
        _loggerAccessor()?.Info(
            $"stage=grpc_host_plan event=budget_request profile={settings.ResourceBudgetProfile} limit={limit} requiredWeight={requiredWeight} required=\"{string.Join(",", requiredHostIds)}\" uses_vision_local_translation={(UsesVisionLocalTranslation(settings) ? "yes" : "no")}.");
        if (requiredWeight > limit)
        {
            var hostSummary = string.Join(", ", requiredHosts.Select(host => $"{host.DisplayName}({host.Weight})"));
            rejectMessage =
                $"The selected OCR/translation combination exceeds the {settings.ResourceBudgetProfile} VRAM budget ({requiredWeight}/{limit}). Required hosts: {hostSummary}.";
            _loggerAccessor()?.Info(
                $"stage=grpc_host_plan event=budget_reject profile={settings.ResourceBudgetProfile} limit={limit} requiredWeight={requiredWeight} hosts=\"{hostSummary}\".");
            return false;
        }

        var hostStates = BuildHostStates(settings, requiredHostIds);
        var plannedHostIds = hostStates
            .Where(static state => state.IsRunning && state.AllowResident)
            .Select(static state => state.HostId)
            .ToHashSet(StringComparer.Ordinal);
        plannedHostIds.UnionWith(requiredHostIds);

        var evictableStates = hostStates
            .Where(state => state.IsRunning && plannedHostIds.Contains(state.HostId) && !requiredHostIds.Contains(state.HostId))
            .OrderByDescending(static state => state.Weight)
            .ThenBy(static state => state.HostId, StringComparer.Ordinal)
            .ToArray();
        var plannedWeight = SumWeights(settings, plannedHostIds);
        foreach (var evictableState in evictableStates)
        {
            if (plannedWeight <= limit)
            {
                break;
            }

            plannedHostIds.Remove(evictableState.HostId);
            plannedWeight -= evictableState.Weight;
            _loggerAccessor()?.Info(
                $"stage=grpc_host_plan event=budget_evict host={evictableState.HostId} weight={evictableState.Weight} reason=unused plannedWeight={plannedWeight} limit={limit}.");
        }

        if (plannedWeight > limit)
        {
            rejectMessage =
                $"Unable to free enough VRAM for the selected OCR/translation route under the {settings.ResourceBudgetProfile} budget ({plannedWeight}/{limit}).";
            _loggerAccessor()?.Info(
                $"stage=grpc_host_plan event=budget_reject profile={settings.ResourceBudgetProfile} limit={limit} plannedWeight={plannedWeight} requiredWeight={requiredWeight} hosts=\"{string.Join(",", plannedHostIds)}\".");
            return false;
        }

        _plannedHostIds.Clear();
        foreach (var plannedHostId in plannedHostIds)
        {
            _plannedHostIds.Add(plannedHostId);
        }

        _loggerAccessor()?.Info(
            $"stage=grpc_host_plan event=budget_decision profile={settings.ResourceBudgetProfile} limit={limit} plannedWeight={plannedWeight} planned=\"{string.Join(",", plannedHostIds.OrderBy(static hostId => hostId, StringComparer.Ordinal))}\".");

        rejectMessage = null;
        return true;
    }

    private void StopHostsNoLongerNeeded()
    {
        StopIfUnused(HostIdPaddle, _paddleGrpcHost.IsRunning, StopPaddle);
        StopIfUnused(HostIdPaddleVl, _paddleVlGrpcHost.IsRunning, StopPaddleVl);
        StopIfUnused(HostIdNdl, _ndlGrpcHost.IsRunning, StopNdl);
        StopIfUnused(HostIdVisionLlm, _visionLlmGrpcHost.IsRunning, StopVisionLlm);
        StopIfUnused(HostIdLlama, _llamaGrpcHost.IsRunning, StopLlama);
    }

    private void StopIfUnused(string hostId, bool isRunning, Action stop)
    {
        if (!isRunning || _plannedHostIds.Contains(hostId))
        {
            return;
        }

        _loggerAccessor()?.Info($"stage=grpc_host host={hostId} event=stop_unused.");
        stop();
    }

    private IReadOnlyList<RequiredHost> BuildRequiredHosts(AppSettings settings)
    {
        var requiredHosts = new List<RequiredHost>();
        if (settings.OcrEngine == OcrEngineKind.Paddle && settings.EnablePaddleGrpcHost)
        {
            requiredHosts.Add(CreateRequiredHost(HostIdPaddle, "PaddleOCR", settings));
        }

        if (settings.OcrEngine == OcrEngineKind.PaddleVllm && settings.EnablePaddleVlGrpcHost)
        {
            requiredHosts.Add(CreateRequiredHost(HostIdPaddleVl, "PaddleOCR-VL", settings));
        }

        if (settings.OcrEngine == OcrEngineKind.Ndl && settings.EnableNdlGrpcHost)
        {
            requiredHosts.Add(CreateRequiredHost(HostIdNdl, "NDLOCR-Lite", settings));
        }

        if (settings.OcrEngine == OcrEngineKind.VisionLlm && settings.EnableVisionLlmGrpcHost)
        {
            requiredHosts.Add(CreateRequiredHost(HostIdVisionLlm, "VisionLLM", settings));
            if (settings.EnableVisionGeometryHybridOcr)
            {
                if (settings.VisionGeometryHybridBaseEngine == VisionGeometryHybridBaseEngineKind.Paddle && settings.EnablePaddleGrpcHost)
                {
                    requiredHosts.Add(CreateRequiredHost(HostIdPaddle, "PaddleOCR", settings));
                }

                if (settings.VisionGeometryHybridBaseEngine == VisionGeometryHybridBaseEngineKind.Ndl && settings.EnableNdlGrpcHost)
                {
                    requiredHosts.Add(CreateRequiredHost(HostIdNdl, "NDLOCR-Lite", settings));
                }
            }
        }

        if (!UsesVisionLocalTranslation(settings) && settings.EnableLlamaCppTranslation)
        {
            requiredHosts.Add(CreateRequiredHost(HostIdLlama, "Llama.cpp", settings));
        }

        return requiredHosts;
    }

    private RequiredHost CreateRequiredHost(string hostId, string displayName, AppSettings settings)
    {
        return new RequiredHost(hostId, displayName, GetHostWeight(hostId, settings));
    }

    private IReadOnlyList<HostRuntimeState> BuildHostStates(AppSettings settings, IReadOnlySet<string> requiredHostIds)
    {
        return new[]
        {
            CreateHostRuntimeState(HostIdPaddle, "PaddleOCR", _paddleGrpcHost.IsRunning, AllowPaddleResident(settings), requiredHostIds.Contains(HostIdPaddle), settings),
            CreateHostRuntimeState(HostIdPaddleVl, "PaddleOCR-VL", _paddleVlGrpcHost.IsRunning, settings.EnablePaddleVlGrpcHost, requiredHostIds.Contains(HostIdPaddleVl), settings),
            CreateHostRuntimeState(HostIdNdl, "NDLOCR-Lite", _ndlGrpcHost.IsRunning, settings.EnableNdlGrpcHost, requiredHostIds.Contains(HostIdNdl), settings),
            CreateHostRuntimeState(HostIdVisionLlm, "VisionLLM", _visionLlmGrpcHost.IsRunning, settings.EnableVisionLlmGrpcHost, requiredHostIds.Contains(HostIdVisionLlm), settings),
            CreateHostRuntimeState(HostIdLlama, "Llama.cpp", _llamaGrpcHost.IsRunning, AllowLlamaResident(settings), requiredHostIds.Contains(HostIdLlama), settings)
        };
    }

    private HostRuntimeState CreateHostRuntimeState(
        string hostId,
        string displayName,
        bool isRunning,
        bool allowResident,
        bool isRequiredNow,
        AppSettings settings)
    {
        return new HostRuntimeState(hostId, displayName, GetHostWeight(hostId, settings), isRunning, allowResident, isRequiredNow);
    }

    private static bool AllowPaddleResident(AppSettings settings)
    {
        return settings.EnablePaddleGrpcHost;
    }

    private bool AllowLlamaResident(AppSettings settings)
    {
        return settings.EnableLlamaCppTranslation || _llamaGrpcHost.IsRunning;
    }

    private static bool UsesVisionLocalTranslation(AppSettings settings)
    {
        return settings.OcrEngine == OcrEngineKind.VisionLlm &&
               settings.EnableVisionLlmGrpcHost &&
               settings.EnableVisionLlmSharedLocalTranslation &&
               settings.EnableLlamaCppTranslation;
    }

    private static int GetHostWeight(string hostId, AppSettings settings)
    {
        return hostId switch
        {
            HostIdPaddle => IsPaddleGpu(settings) ? 1 : 0,
            HostIdPaddleVl => 6,
            HostIdNdl => 0,
            HostIdVisionLlm => 4,
            HostIdLlama => 3,
            _ => 0
        };
    }

    private static int SumWeights(AppSettings settings, IEnumerable<string> hostIds)
    {
        return hostIds.Distinct(StringComparer.Ordinal).Sum(hostId => GetHostWeight(hostId, settings));
    }

    private static int GetBudgetLimit(AppSettings settings)
    {
        return settings.ResourceBudgetProfile switch
        {
            GraphicsResourceBudgetProfile.LowVram => 4,
            GraphicsResourceBudgetProfile.Balanced => 6,
            GraphicsResourceBudgetProfile.HighVram => 8,
            GraphicsResourceBudgetProfile.UltraVram => 10,
            _ => 6
        };
    }

    private static bool IsPaddleGpu(AppSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.PaddleDevice) &&
               settings.PaddleDevice.Trim().StartsWith("gpu", StringComparison.OrdinalIgnoreCase);
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

    private readonly record struct RequiredHost(string HostId, string DisplayName, int Weight);

    private readonly record struct HostRuntimeState(
        string HostId,
        string DisplayName,
        int Weight,
        bool IsRunning,
        bool AllowResident,
        bool IsRequiredNow);

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

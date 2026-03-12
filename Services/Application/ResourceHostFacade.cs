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
        var desiredHosts = BuildDesiredHostSet(settings);
        var limit = GetBudgetLimit(settings);
        var requiredHosts = desiredHosts.Where(static host => host.Required).ToArray();
        var requiredWeight = requiredHosts.Sum(static host => host.BudgetWeight);
        if (requiredWeight <= limit)
        {
            message = null;
            return true;
        }

        var hostSummary = string.Join(", ", requiredHosts.Select(host => $"{host.DisplayName}({host.BudgetWeight})"));
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
            var desiredHosts = BuildDesiredHostSet(settings);
            var limit = GetBudgetLimit(settings);
            var requiredWeight = desiredHosts.Where(static host => host.Required).Sum(static host => host.BudgetWeight);
            if (requiredWeight > limit)
            {
                _loggerAccessor()?.Info(
                    $"stage=grpc_host_plan event=required_over_budget_allowed profile={settings.ResourceBudgetProfile} limit={limit} requiredWeight={requiredWeight}.");
            }

            var plannedHosts = ApplyVramBudget(settings, desiredHosts);
            SetPlannedHostIds(plannedHosts);
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
                ShouldLoad = ShouldLoadPaddle,
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
                ShouldLoad = ShouldLoadPaddleVl,
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
                ShouldLoad = ShouldLoadNdl,
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
                ShouldLoad = ShouldLoadVisionLlm,
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

    private bool ShouldLoadPaddle(AppSettings _) => _plannedHostIds.Contains(HostIdPaddle);

    private bool ShouldLoadPaddleVl(AppSettings _) => _plannedHostIds.Contains(HostIdPaddleVl);

    private bool ShouldLoadNdl(AppSettings _) => _plannedHostIds.Contains(HostIdNdl);

    private bool ShouldLoadVisionLlm(AppSettings _) => _plannedHostIds.Contains(HostIdVisionLlm);

    private bool ShouldLoadLlama(AppSettings _) => _plannedHostIds.Contains(HostIdLlama);

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

    private IReadOnlyList<PlannedHost> BuildDesiredHostSet(AppSettings settings)
    {
        var desiredHosts = new List<PlannedHost>();
        if (settings.OcrEngine == OcrEngineKind.Paddle && settings.EnablePaddleGrpcHost)
        {
            desiredHosts.Add(CreatePlannedHost(HostIdPaddle, "PaddleOCR", HostPlanRole.PrimaryOcr, settings, required: true, reason: "primary_ocr"));
        }

        if (settings.OcrEngine == OcrEngineKind.PaddleVllm && settings.EnablePaddleVlGrpcHost)
        {
            desiredHosts.Add(CreatePlannedHost(HostIdPaddleVl, "PaddleOCR-VL", HostPlanRole.PrimaryOcr, settings, required: true, reason: "primary_ocr"));
        }

        if (settings.OcrEngine == OcrEngineKind.Ndl && settings.EnableNdlGrpcHost)
        {
            desiredHosts.Add(CreatePlannedHost(HostIdNdl, "NDLOCR-Lite", HostPlanRole.PrimaryOcr, settings, required: true, reason: "primary_ocr"));
        }

        if (settings.OcrEngine == OcrEngineKind.VisionLlm && settings.EnableVisionLlmGrpcHost)
        {
            desiredHosts.Add(CreatePlannedHost(HostIdVisionLlm, "VisionLLM", HostPlanRole.PrimaryOcr, settings, required: true, reason: "primary_ocr"));
        }

        if (settings.OcrEngine == OcrEngineKind.VisionLlm &&
            settings.EnableVisionLlmGrpcHost &&
            settings.EnableVisionGeometryHybridOcr)
        {
            if (settings.VisionGeometryHybridBaseEngine == VisionGeometryHybridBaseEngineKind.Paddle && settings.EnablePaddleGrpcHost)
            {
                desiredHosts.Add(CreatePlannedHost(HostIdPaddle, "PaddleOCR", HostPlanRole.HelperOcr, settings, required: false, reason: "vision_geometry_helper"));
            }

            if (settings.VisionGeometryHybridBaseEngine == VisionGeometryHybridBaseEngineKind.Ndl && settings.EnableNdlGrpcHost)
            {
                desiredHosts.Add(CreatePlannedHost(HostIdNdl, "NDLOCR-Lite", HostPlanRole.HelperOcr, settings, required: false, reason: "vision_geometry_helper"));
            }
        }

        if (!UsesVisionLocalTranslation(settings) && settings.EnableLlamaCppTranslation)
        {
            desiredHosts.Add(CreatePlannedHost(HostIdLlama, "Llama.cpp", HostPlanRole.Translation, settings, required: true, reason: "translation"));
        }

        return desiredHosts;
    }

    private PlannedHost CreatePlannedHost(
        string hostId,
        string displayName,
        HostPlanRole role,
        AppSettings settings,
        bool required,
        string reason)
    {
        return new PlannedHost(hostId, displayName, role, GetHostBudgetWeight(settings, hostId), required, reason);
    }

    private IReadOnlyList<PlannedHost> ApplyVramBudget(AppSettings settings, IReadOnlyList<PlannedHost> desiredHosts)
    {
        var limit = GetBudgetLimit(settings);
        var plannedHosts = new List<PlannedHost>();
        var currentWeight = 0;
        foreach (var host in desiredHosts.Where(static host => host.Required))
        {
            plannedHosts.Add(host);
            currentWeight += host.BudgetWeight;
        }

        foreach (var host in desiredHosts.Where(static host => !host.Required))
        {
            if (currentWeight + host.BudgetWeight <= limit)
            {
                plannedHosts.Add(host);
                currentWeight += host.BudgetWeight;
                continue;
            }

            _loggerAccessor()?.Info(
                $"stage=grpc_host_plan event=budget_drop host={host.HostId} role={host.Role} weight={host.BudgetWeight} limit={limit} currentWeight={currentWeight} reason={host.Reason}.");
        }

        var desiredSummary = string.Join(",", desiredHosts.Select(host => host.HostId));
        var plannedSummary = string.Join(",", plannedHosts.Select(host => host.HostId));
        _loggerAccessor()?.Info(
            $"stage=grpc_host_plan event=budget_decision profile={settings.ResourceBudgetProfile} limit={limit} desiredWeight={desiredHosts.Sum(static host => host.BudgetWeight)} plannedWeight={plannedHosts.Sum(static host => host.BudgetWeight)} desired=\"{desiredSummary}\" planned=\"{plannedSummary}\" uses_vision_local_translation={(UsesVisionLocalTranslation(settings) ? "yes" : "no")}.");
        return plannedHosts;
    }

    private static bool UsesVisionLocalTranslation(AppSettings settings)
    {
        return settings.OcrEngine == OcrEngineKind.VisionLlm &&
               settings.EnableVisionLlmGrpcHost &&
               settings.EnableVisionLlmSharedLocalTranslation &&
               settings.EnableLlamaCppTranslation;
    }

    private static int GetHostBudgetWeight(AppSettings settings, string hostId)
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

    private void SetPlannedHostIds(IReadOnlyList<PlannedHost> plannedHosts)
    {
        _plannedHostIds.Clear();
        foreach (var plannedHost in plannedHosts)
        {
            _plannedHostIds.Add(plannedHost.HostId);
        }
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

    private enum HostPlanRole
    {
        PrimaryOcr,
        HelperOcr,
        Translation
    }

    private readonly record struct PlannedHost(
        string HostId,
        string DisplayName,
        HostPlanRole Role,
        int BudgetWeight,
        bool Required,
        string Reason);

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

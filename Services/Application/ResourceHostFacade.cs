using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const string LlamaProjectRelativePath = "TranslationServiceLlama";
    private const string VisionProjectRelativePath = "OcrServiceVisionLlm";
    private const string SharedModelsRelativePath = "TranslationServiceLlama\\LlamaCpp\\Models";
    private const string LlamaManifestFileName = "model_manifest.json";
    private const string VisionManifestFileName = "model_manifest.json";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

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
    private readonly Dictionary<string, string> _cancelledSelections = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _loadCts;
    private long _operationId;
    private bool _loadOwnsBusy;
    private HostLoadProgress? _bootstrapProgress;
    private readonly Action<bool> _setCancelable;
    private readonly Action<HostLoadProgress> _updateProgress;
    private readonly Action<string?> _updateLoadStatus;

    public bool IsLoading => _loadCts != null;
    public bool OwnsBusyOverlay => IsLoading && _loadOwnsBusy;
    public bool IsCurrentLoad(long operationId)
    {
        var source = _loadCts;
        return source != null && !source.IsCancellationRequested && operationId == Interlocked.Read(ref _operationId);
    }
    public ResourceHostLoadResult LastLoadResult { get; private set; } = ResourceHostLoadResult.Success;

    private LlamaHostConfig? _llamaHostConfig;

    public ResourceHostFacade(
        Func<AppLogger?> loggerAccessor,
        Action<bool, string?> setBusyOverlay,
        Action<AppSettings, bool> syncSettingsToView,
        Action<string> showLoadFailure,
        Action<bool> setCancelable,
        Action<HostLoadProgress> updateProgress,
        Action<string?> updateLoadStatus)
    {
        _loggerAccessor = loggerAccessor;
        _setBusyOverlay = setBusyOverlay;
        _syncSettingsToView = syncSettingsToView;
        _showLoadFailure = showLoadFailure;
        _setCancelable = setCancelable;
        _updateProgress = updateProgress;
        _updateLoadStatus = updateLoadStatus;

        _hostOrchestrator = new GrpcHostOrchestrator(_loggerAccessor, message =>
        {
            _loadOwnsBusy = true;
            _setBusyOverlay(true, message);
            _setCancelable(true);
        });
        // WHY: Host instances can be created before OnLoaded assigns AppLogger; use accessor to avoid capturing null.
        _paddleGrpcHost = new PaddleGrpcHost(_loggerAccessor);
        _paddleVlGrpcHost = new PaddleVlGrpcHost(_loggerAccessor);
        _ndlGrpcHost = new NdlGrpcHost(_loggerAccessor);
        _visionLlmGrpcHost = new VisionLlmGrpcHost(_loggerAccessor);
        _llamaGrpcHost = new LlamaGrpcHost(_loggerAccessor);
        _hostRegistry = new GrpcHostRegistry(BuildHostDescriptors());
    }

    public bool IsPaddleVlRunning => _paddleVlGrpcHost.IsRunning;

    internal ResourceHostFacade(Func<AppLogger?> loggerAccessor, Action<bool, string?> setBusyOverlay,
        Action<AppSettings, bool> syncSettingsToView, Action<string> showLoadFailure, Action<bool> setCancelable,
        Action<HostLoadProgress> updateProgress, Action<string?> updateLoadStatus, GrpcHostRegistry registry)
        : this(loggerAccessor, setBusyOverlay, syncSettingsToView, showLoadFailure, setCancelable, updateProgress, updateLoadStatus)
    {
        _hostRegistry = registry;
    }

    public bool IsVisionLlmRunning => _visionLlmGrpcHost.IsRunning;
    public bool IsLlamaRunning => _llamaGrpcHost.IsRunning;

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

    public async Task<ResourceHostLoadResult> EnsureResourceHostsAsync(AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (_shutdown.IsCancellationRequested) return ResourceHostLoadResult.Cancelled;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try { await _resourceLoadGate.WaitAsync(operation.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { return ResourceHostLoadResult.Cancelled; }
        var id = ++_operationId;
        _loadCts = operation;
        _loadOwnsBusy = false;
        var result = ResourceHostLoadResult.Success;
        void Progress(HostLoadProgress value)
        {
            if (IsCurrentLoad(id)) _updateProgress(value with { OperationId = id });
        }
        _llamaGrpcHost.LoadProgress += Progress;
        _visionLlmGrpcHost.LoadProgress += Progress;
        try
        {
            var requiredHosts = BuildRequiredHosts(settings);
            if (!EnsureBudgetForHosts(settings, requiredHosts, out var rejectMessage))
            {
                result = new(ResourceHostLoadStatus.Failed, false, new[]
                {
                    new HostLoadFailure("resource_budget", "", null, HostLoadPhase.Environment,
                        rejectMessage ?? "Resource host VRAM budget exceeded.", "")
                });
            }
            else
            {
                // NOTE: Cancellation belongs to this selection, not to unrelated settings saves.
                RefreshCancelledSelections(settings);
                await Task.Run(StopHostsNoLongerNeeded).ConfigureAwait(true);
                result = await _hostOrchestrator.EnsureHostsAsync(settings, _hostRegistry, operation.Token,
                    _cancelledSelections.Keys.ToHashSet(StringComparer.Ordinal)).ConfigureAwait(true);
                if (result.Status == ResourceHostLoadStatus.Cancelled)
                {
                    foreach (var descriptor in _hostRegistry.All)
                        if (_plannedHostIds.Contains(descriptor.HostId) && !descriptor.IsRunning())
                            _cancelledSelections[descriptor.HostId] = GetSelection(settings, descriptor.HostId);
                }
                else if (result.Status == ResourceHostLoadStatus.Succeeded && GetUnavailableMessage(settings) != null)
                    result = result with { Status = ResourceHostLoadStatus.Cancelled };
            }
        }
        finally
        {
            // WHY: Invalidate pending dispatcher progress before hiding busy UI and showing any modal error.
            _loadCts = null;
            _llamaGrpcHost.LoadProgress -= Progress;
            _visionLlmGrpcHost.LoadProgress -= Progress;
            if (_loadOwnsBusy)
            {
                _setCancelable(false);
                _setBusyOverlay(false, null);
                _loadOwnsBusy = false;
            }
            _updateLoadStatus(GetUnavailableMessage(settings));
            _resourceLoadGate.Release();
        }
        LastLoadResult = result;
        if (!_shutdown.IsCancellationRequested && result.Failures.Count > 0)
            _showLoadFailure(string.Join(Environment.NewLine + Environment.NewLine, result.Failures.Select(failure => FormatFailure(failure, settings))));
        _loggerAccessor()?.Info($"stage=grpc_host_load operation_id={id} event=completed result={result.Status}.");
        return result;
    }

    public bool CancelLoading()
    {
        if (_loadCts == null || !_loadOwnsBusy) return false;
        _setCancelable(false);
        _setBusyOverlay(true, LocalizationService.Instance["ResourceHost_Cancelling"]);
        _loadCts.Cancel();
        return true;
    }

    public void AllowExplicitReload(bool translationOnly)
    {
        foreach (var id in _cancelledSelections.Keys.ToArray())
            if ((id == HostIdLlama) == translationOnly) _cancelledSelections.Remove(id);
    }

    public void RefreshCancelledSelections(AppSettings settings)
    {
        var required = BuildRequiredHosts(settings);
        foreach (var hostId in _cancelledSelections.Keys.ToArray())
            if (!required.Any(host => host.HostId == hostId) || _cancelledSelections[hostId] != GetSelection(settings, hostId))
                _cancelledSelections.Remove(hostId);
        _updateLoadStatus(GetUnavailableMessage(settings));
    }

    public string? GetUnavailableMessage(AppSettings settings)
    {
        var unavailable = BuildRequiredHosts(settings).Where(host => _cancelledSelections.TryGetValue(host.HostId, out var selection)
                && selection == GetSelection(settings, host.HostId))
            .Where(host => !_hostRegistry.All.First(descriptor => descriptor.HostId == host.HostId).IsRunning())
            .Select(host => host.DisplayName).ToArray();
        return unavailable.Length == 0 ? null : LocalizationService.Instance.GetString("ResourceHost_NotLoaded", string.Join(", ", unavailable));
    }

    private static string GetSelection(AppSettings settings, string hostId) => hostId switch
    {
        HostIdLlama => settings.LlamaSelectedModelFileName,
        HostIdVisionLlm => settings.VisionLlmSelectedModelFileName + "|" + settings.VisionLlmSelectedMmprojFileName,
        _ => settings.OcrEngine.ToString()
    };

    private string FormatFailure(HostLoadFailure failure, AppSettings settings)
    {
        var strings = LocalizationService.Instance;
        var descriptor = _hostRegistry.All.FirstOrDefault(item => item.HostId == failure.HostId);
        var title = descriptor?.FailureUserMessage(settings) ?? failure.Reason;
        if (descriptor == null) return title;
        if (string.IsNullOrWhiteSpace(failure.Model)) return title + Environment.NewLine + failure.Reason;
        var details = strings.GetString("ResourceHost_FailureDetails", failure.Model,
            strings["ResourceHost_Phase_" + failure.Phase], failure.Reason);
        if (!string.IsNullOrWhiteSpace(failure.Mmproj)) details += Environment.NewLine + strings.GetString("ResourceHost_Mmproj", failure.Mmproj);
        if (!string.IsNullOrWhiteSpace(failure.Details)) details += Environment.NewLine + failure.Details;
        return title + Environment.NewLine + details;
    }

    public async Task ShutdownAsync()
    {
        _shutdown.Cancel();
        _loadCts?.Cancel();
        await _resourceLoadGate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(StopAll).ConfigureAwait(false); }
        finally { _resourceLoadGate.Release(); }
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
        foreach (var descriptor in _hostRegistry.All)
        {
            descriptor.Stop();
            descriptor.OnStopped?.Invoke();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        StopAll();
        _resourceLoadGate.Dispose();
        _shutdown.Dispose();
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
                BusyMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Loading_Paddle"),
                DisableOnFailure = DisablePaddleOcr,
                FailureLogMessage = "Paddle gRPC host failed to start.",
                FailureUserMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Failed_Paddle")
            },
            new()
            {
                HostId = HostIdPaddleVl,
                ShouldLoad = _ => _plannedHostIds.Contains(HostIdPaddleVl),
                IsRunning = () => _paddleVlGrpcHost.IsRunning,
                StartAsync = (settings, token) => _paddleVlGrpcHost.StartAsync(settings, token),
                Stop = () => _paddleVlGrpcHost.Stop(),
                BusyMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Loading_PaddleVl"),
                DisableOnFailure = DisablePaddleVlOcr,
                FailureLogMessage = "PaddleOCR-VL gRPC host failed to start.",
                FailureUserMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Failed_PaddleVl")
            },
            new()
            {
                HostId = HostIdNdl,
                ShouldLoad = _ => _plannedHostIds.Contains(HostIdNdl),
                IsRunning = () => _ndlGrpcHost.IsRunning,
                StartAsync = (settings, token) => _ndlGrpcHost.StartAsync(settings, token),
                Stop = () => _ndlGrpcHost.Stop(),
                BusyMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Loading_Ndl"),
                DisableOnFailure = DisableNdlOcr,
                FailureLogMessage = "NDLOCR gRPC host failed to start.",
                FailureUserMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Failed_Ndl")
            },
            new()
            {
                HostId = HostIdVisionLlm,
                ShouldLoad = _ => _plannedHostIds.Contains(HostIdVisionLlm),
                IsRunning = () => _visionLlmGrpcHost.IsRunning,
                StartAsync = (settings, token) => _visionLlmGrpcHost.StartAsync(settings, token),
                Stop = () => _visionLlmGrpcHost.Stop(),
                BusyMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Loading_VisionLlm"),
                DisableOnFailure = DisableVisionLlmOcr,
                FailureLogMessage = "VisionLLM gRPC host failed to start.",
                DescribeFailure = (settings, ex) => _visionLlmGrpcHost.DescribeLoadFailure(
                    settings.VisionLlmSelectedModelFileName, settings.VisionLlmSelectedMmprojFileName, ex),
                FailureUserMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Failed_VisionLlm")
            },
            new()
            {
                HostId = HostIdLlama,
                ShouldLoad = _ => _plannedHostIds.Contains(HostIdLlama),
                IsRunning = () => _llamaGrpcHost.IsRunning,
                StartAsync = (settings, token) => _llamaGrpcHost.StartAsync(settings, token),
                Stop = () => _llamaGrpcHost.Stop(),
                BusyMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Loading_Llama"),
                DisableOnFailure = DisableLlamaTranslation,
                FailureLogMessage = "Llama gRPC host failed to start.",
                DescribeFailure = (settings, ex) => _llamaGrpcHost.DescribeLoadFailure(settings.LlamaSelectedModelFileName, null, ex),
                FailureUserMessage = _ => LocalizationService.Instance.GetString("ResourceHost_Failed_Llama"),
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

    public async Task<ResourceBootstrapPlan?> BuildBootstrapPlanAsync(AppSettings settings, ResourceBootstrapIntent intent)
    {
        if (_shutdown.IsCancellationRequested) return null;
        if (BuildRequiredHosts(settings).All(host =>
            (_hostRegistry.TryGet(host.HostId, out var descriptor) && descriptor.IsRunning()) ||
            (_cancelledSelections.TryGetValue(host.HostId, out var selection) && selection == GetSelection(settings, host.HostId))))
            return new ResourceBootstrapPlan(Array.Empty<ResourceBootstrapItem>());
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        try { await _resourceLoadGate.WaitAsync(operation.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { return null; }
        var id = Interlocked.Increment(ref _operationId);
        _loadCts = operation;
        _loadOwnsBusy = true;
        _bootstrapProgress = null;
        HostLoadFailure? failure = null;
        _setBusyOverlay(true, LocalizationService.Instance["ResourceHost_Phase_Validation"]);
        _setCancelable(true);
        try
        {
            // WHY: Confirmation used to hash multi-GB models synchronously before the cancellable loading UI existed.
            return await Task.Run(() => BuildBootstrapPlan(settings, intent, operation.Token, id), operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            MarkSelectionCancelled(settings);
            return null;
        }
        catch (Exception ex)
        {
            var hostId = _bootstrapProgress?.HostId ?? "resource_bootstrap";
            var model = hostId == HostIdVisionLlm ? settings.VisionLlmSelectedModelFileName : settings.LlamaSelectedModelFileName;
            failure = new(hostId, model, hostId == HostIdVisionLlm ? settings.VisionLlmSelectedMmprojFileName : null,
                HostLoadPhase.Validation, HostLoadDiagnostics.Sanitize(ex.Message), "");
            // NOTE: Before confirmation, keep the selection but block runs against an unverified/unloaded host.
            MarkSelectionCancelled(settings);
            LastLoadResult = new(ResourceHostLoadStatus.Failed, false, new[] { failure });
            _loggerAccessor()?.Error(ex, "Resource bootstrap validation failed.");
        }
        finally
        {
            _loadCts = null;
            _loadOwnsBusy = false;
            _setCancelable(false);
            _setBusyOverlay(false, null);
            _updateLoadStatus(GetUnavailableMessage(settings));
            _resourceLoadGate.Release();
        }
        if (!_shutdown.IsCancellationRequested && failure != null)
            _showLoadFailure(LocalizationService.Instance["ResourceHost_PreflightFailed"] + Environment.NewLine +
                LocalizationService.Instance.GetString("ResourceHost_FailureDetails", failure.Model,
                    LocalizationService.Instance["ResourceHost_Phase_Validation"], failure.Reason));
        return null;
    }

    public void MarkSelectionCancelled(AppSettings settings)
    {
        foreach (var host in BuildRequiredHosts(settings))
            if (!_hostRegistry.All.First(descriptor => descriptor.HostId == host.HostId).IsRunning())
                _cancelledSelections[host.HostId] = GetSelection(settings, host.HostId);
        LastLoadResult = ResourceHostLoadResult.Cancelled;
        _updateLoadStatus(GetUnavailableMessage(settings));
    }

    private bool ValidateBootstrapAsset(string path, ModelAssetDescriptor asset, string hostId,
        CancellationToken token, long operationId)
    {
        _bootstrapProgress = new(hostId, HostLoadPhase.Validation, asset.LocalFileName, OperationId: operationId);
        _updateProgress(_bootstrapProgress);
        return ModelAssetProvisioner.ValidateAssetAsync(path, asset, token).GetAwaiter().GetResult().Valid;
    }

    private ResourceBootstrapPlan BuildBootstrapPlan(AppSettings settings, ResourceBootstrapIntent intent,
        CancellationToken cancellationToken, long operationId)
    {
        var items = new List<ResourceBootstrapItem>();
        foreach (var requiredHost in BuildRequiredHosts(settings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_hostRegistry.TryGet(requiredHost.HostId, out var descriptor) && descriptor.IsRunning()) continue;
            if (_cancelledSelections.TryGetValue(requiredHost.HostId, out var selection) &&
                selection == GetSelection(settings, requiredHost.HostId)) continue;
            switch (requiredHost.HostId)
            {
                case HostIdLlama:
                    TryAddLlamaBootstrapItem(items, settings, cancellationToken, operationId);
                    break;
                case HostIdVisionLlm:
                    // WHY: Settings save should stay cheap. VisionLLM asset existence/integrity is enforced
                    // by the actual startup/download path, so only app-load confirmation keeps this preview.
                    if (intent == ResourceBootstrapIntent.AppLoad)
                    {
                        TryAddVisionBootstrapItem(items, settings, cancellationToken, operationId);
                    }

                    break;
                case HostIdPaddle:
                    TryAddPaddleBootstrapItem(items, settings);
                    break;
                case HostIdPaddleVl:
                    TryAddPaddleVlBootstrapItem(items, settings);
                    break;
            }
        }

        return new ResourceBootstrapPlan(items);
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
            settings.EnableLlamaMtp,
            settings.LlamaMtpDraftTokens,
            settings.LlamaSelectedModelFileName,
            settings.LlamaGrpcEndpoint,
            settings.LlamaGrpcHost,
            settings.LlamaGrpcPort);
    }

    private void TryAddLlamaBootstrapItem(ICollection<ResourceBootstrapItem> items, AppSettings settings,
        CancellationToken cancellationToken, long operationId)
    {
        var projectDir = ResolveAppRelativePath(LlamaProjectRelativePath);
        var needsRuntimeSetup = !Directory.Exists(Path.Combine(projectDir, ".venv"));
        var manifestPath = Path.Combine(projectDir, LlamaManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var manifest = ReadLlamaManifest(manifestPath);
        if (manifest is null)
        {
            return;
        }

        var selectedModelFileName = SettingsHostNormalizer.NormalizeLlamaModelFileName(settings.LlamaSelectedModelFileName);
        var isDefaultSelection = string.Equals(selectedModelFileName, manifest.Filename, StringComparison.OrdinalIgnoreCase);
        var modelPath = Path.Combine(ResolveAppRelativePath(SharedModelsRelativePath), selectedModelFileName);
        var needsModelDownload = isDefaultSelection &&
            !ValidateBootstrapAsset(
                modelPath,
                new ModelAssetDescriptor(
                    manifest.Filename,
                    manifest.DownloadUrl,
                    manifest.Sha256,
                    manifest.SizeBytes),
                HostIdLlama, cancellationToken, operationId);
        if (!needsRuntimeSetup && !needsModelDownload)
        {
            return;
        }

        var details = new List<string>();
        if (needsRuntimeSetup)
        {
            details.Add("Python runtime setup");
        }

        if (needsModelDownload)
        {
            details.Add("default translation model download");
        }

        items.Add(new ResourceBootstrapItem(
            "Llama.cpp",
            $"Will start {string.Join(" and ", details)} before the host can run.",
            IsDefinite: true,
            KnownDownloadBytes: needsModelDownload ? manifest.SizeBytes : null,
            ApprovalKey: null));
    }

    private void TryAddVisionBootstrapItem(ICollection<ResourceBootstrapItem> items, AppSettings settings,
        CancellationToken cancellationToken, long operationId)
    {
        var projectDir = ResolveAppRelativePath(VisionProjectRelativePath);
        var needsRuntimeSetup = !Directory.Exists(Path.Combine(projectDir, ".venv"));
        var manifestPath = Path.Combine(projectDir, VisionManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var manifest = ReadVisionManifest(manifestPath);
        if (manifest?.Model is null || manifest.Mmproj is null)
        {
            return;
        }

        var selectedModelFileName = SettingsHostNormalizer.NormalizeVisionLlmModelFileName(settings.VisionLlmSelectedModelFileName);
        var selectedMmprojFileName = SettingsHostNormalizer.NormalizeVisionLlmMmprojFileName(settings.VisionLlmSelectedMmprojFileName);
        var usesManifestAssets =
            string.Equals(selectedModelFileName, manifest.Model.LocalFileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(selectedMmprojFileName, manifest.Mmproj.LocalFileName, StringComparison.OrdinalIgnoreCase);
        var sharedModelsDir = ResolveAppRelativePath(SharedModelsRelativePath);
        var needsModelDownload = false;
        var needsMmprojDownload = false;
        if (usesManifestAssets)
        {
            needsModelDownload = !ValidateBootstrapAsset(
                Path.Combine(sharedModelsDir, selectedModelFileName),
                manifest.Model.ToDescriptor(),
                HostIdVisionLlm, cancellationToken, operationId);
            needsMmprojDownload = !ValidateBootstrapAsset(
                Path.Combine(sharedModelsDir, selectedMmprojFileName),
                manifest.Mmproj.ToDescriptor(),
                HostIdVisionLlm, cancellationToken, operationId);
        }

        if (!needsRuntimeSetup && !needsModelDownload && !needsMmprojDownload)
        {
            return;
        }

        var details = new List<string>();
        if (needsRuntimeSetup)
        {
            details.Add("Python runtime setup");
        }

        if (needsModelDownload || needsMmprojDownload)
        {
            details.Add("VisionLLM model file download");
        }

        long? knownBytes = null;
        if (needsModelDownload || needsMmprojDownload)
        {
            knownBytes = 0;
            if (needsModelDownload && manifest.Model.SizeBytes is > 0)
            {
                knownBytes += manifest.Model.SizeBytes.Value;
            }

            if (needsMmprojDownload && manifest.Mmproj.SizeBytes is > 0)
            {
                knownBytes += manifest.Mmproj.SizeBytes.Value;
            }
        }

        items.Add(new ResourceBootstrapItem(
            "VisionLLM OCR",
            $"Will start {string.Join(" and ", details)} before the host can run.",
            IsDefinite: true,
            KnownDownloadBytes: knownBytes > 0 ? knownBytes : null,
            ApprovalKey: null));
    }

    private void TryAddPaddleBootstrapItem(ICollection<ResourceBootstrapItem> items, AppSettings settings)
    {
        var projectDir = ResolveAppRelativePath("OcrService");
        var needsRuntimeSetup = !Directory.Exists(Path.Combine(projectDir, ".venv"));
        var usesLibraryManagedModels = string.IsNullOrWhiteSpace(settings.PaddleModelDir);
        if (!needsRuntimeSetup && !usesLibraryManagedModels)
        {
            return;
        }

        var approvalKey =
            $"paddle_ocr|lang={PaddleModelResolver.ResolvePaddleLanguage(settings.SourceLanguage)}|det={PaddleModelResolver.NormalizeDetectionModelName(settings.PaddleTextDetectionModelName)}|rec={PaddleModelResolver.ResolveRecognitionModelForExecution(settings.PaddleTextRecognitionModelName, settings.SourceLanguage)}|managed={(usesLibraryManagedModels ? "yes" : "no")}";
        if (IsBootstrapApprovalGranted(settings, approvalKey))
        {
            return;
        }

        var details = new List<string>();
        if (needsRuntimeSetup)
        {
            details.Add("Python runtime setup");
        }

        if (usesLibraryManagedModels)
        {
            details.Add("PaddleOCR model download depending on local cache");
        }

        items.Add(new ResourceBootstrapItem(
            "PaddleOCR",
            $"May start {string.Join(" and ", details)} before OCR becomes available.",
            IsDefinite: false,
            KnownDownloadBytes: null,
            ApprovalKey: approvalKey));
    }

    private void TryAddPaddleVlBootstrapItem(ICollection<ResourceBootstrapItem> items, AppSettings settings)
    {
        var projectDir = ResolveAppRelativePath("OcrServiceVL");
        var needsRuntimeSetup = !Directory.Exists(Path.Combine(projectDir, ".venv"));
        var pipelineVersion = string.IsNullOrWhiteSpace(settings.PaddleVlPipelineVersion) ? "v1.5" : settings.PaddleVlPipelineVersion.Trim();
        var precision = string.IsNullOrWhiteSpace(settings.PaddleVlPrecision) ? "fp32" : settings.PaddleVlPrecision.Trim().ToLowerInvariant();
        var approvalKey = $"paddle_vl|pipeline={pipelineVersion}|precision={precision}|hpi={(settings.PaddleVlEnableHpi ? "yes" : "no")}";
        if (IsBootstrapApprovalGranted(settings, approvalKey))
        {
            return;
        }

        var details = new List<string>();
        if (needsRuntimeSetup)
        {
            details.Add("Python runtime setup");
        }

        details.Add($"PaddleOCR-VL pipeline download for {pipelineVersion} depending on local cache");
        items.Add(new ResourceBootstrapItem(
            "PaddleOCR-VL",
            $"May start {string.Join(" and ", details)} before OCR becomes available.",
            IsDefinite: false,
            KnownDownloadBytes: null,
            ApprovalKey: approvalKey));
    }

    private static bool IsBootstrapApprovalGranted(AppSettings settings, string approvalKey)
    {
        var approvals = settings.ApprovedResourceBootstrapKeys ??= new List<string>();
        return approvals.Any(existing => string.Equals(existing, approvalKey, StringComparison.Ordinal));
    }

    private static string ResolveAppRelativePath(string path)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static LlamaModelManifest? ReadLlamaManifest(string manifestPath)
    {
        try
        {
            var text = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<LlamaModelManifest>(text, ManifestJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static VisionBootstrapManifest? ReadVisionManifest(string manifestPath)
    {
        try
        {
            var text = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<VisionBootstrapManifest>(text, ManifestJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct RequiredHost(string HostId, string DisplayName, int Weight);

    private sealed class LlamaModelManifest
    {
        [JsonPropertyName("filename")]
        public string Filename { get; init; } = string.Empty;

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;

        [JsonPropertyName("size_bytes")]
        public long? SizeBytes { get; init; }
    }

    private sealed class VisionBootstrapManifest
    {
        [JsonPropertyName("model")]
        public VisionBootstrapAssetManifest? Model { get; init; }

        [JsonPropertyName("mmproj")]
        public VisionBootstrapAssetManifest? Mmproj { get; init; }
    }

    private sealed class VisionBootstrapAssetManifest
    {
        [JsonPropertyName("local_filename")]
        public string LocalFileName { get; init; } = string.Empty;

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;

        [JsonPropertyName("size_bytes")]
        public long? SizeBytes { get; init; }

        public ModelAssetDescriptor ToDescriptor()
        {
            return new ModelAssetDescriptor(
                LocalFileName,
                DownloadUrl,
                Sha256,
                SizeBytes);
        }
    }

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
        bool EnableMtp,
        int MtpDraftTokens,
        string ModelFileName,
        string Endpoint,
        string GrpcHost,
        int GrpcPort);
}

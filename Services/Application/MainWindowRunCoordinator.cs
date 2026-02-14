using System;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.Settings.FeatureSettings;

namespace Hotkey_Translator.Services.Application;

internal sealed class MainWindowRunCoordinator : IDisposable
{
    private const int SceneSemanticPayloadTtlMs = 500;

    private readonly SettingsService _settingsService;
    private readonly IMainWindowViewBridge _viewBridge;
    private readonly Func<PipelineOrchestrator?> _pipelineAccessor;
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Func<SceneTextSnapshot?> _consumePendingSemanticPayload;
    private readonly Func<bool> _tryDrainPendingSceneAutoTranslate;
    private readonly FeatureSettingsProvider _featureSettingsProvider = new();

    private CancellationTokenSource? _runCts;
    private int _runInProgress;
    private int _showCenterBusyForCurrentRun;
    private bool _hasRunOnce;

    public MainWindowRunCoordinator(
        SettingsService settingsService,
        IMainWindowViewBridge viewBridge,
        Func<PipelineOrchestrator?> pipelineAccessor,
        Func<AppLogger?> loggerAccessor,
        Func<SceneTextSnapshot?> consumePendingSemanticPayload,
        Func<bool> tryDrainPendingSceneAutoTranslate)
    {
        _settingsService = settingsService;
        _viewBridge = viewBridge;
        _pipelineAccessor = pipelineAccessor;
        _loggerAccessor = loggerAccessor;
        _consumePendingSemanticPayload = consumePendingSemanticPayload;
        _tryDrainPendingSceneAutoTranslate = tryDrainPendingSceneAutoTranslate;
    }

    public bool HasRunOnce => _hasRunOnce;

    public bool IsRunning => Interlocked.CompareExchange(ref _runInProgress, 1, 1) == 1;

    public bool ShouldShowCenterBusyForCurrentRun =>
        Interlocked.CompareExchange(ref _showCenterBusyForCurrentRun, 0, 0) == 1;

    public async Task RunOnceAsync(ForceRunOptions options, SceneTextSnapshot? semanticPayload)
    {
        var pipeline = _pipelineAccessor();
        if (pipeline == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _runInProgress, 1) == 1)
        {
            _viewBridge.AppendLog("Run skipped: OCR already running.");
            return;
        }

        var settings = _settingsService.Settings;
        var isFirstRun = !_hasRunOnce;
        var showCenterBusyForCurrentRun = options.Trigger != RunTrigger.AutoSceneChange;
        _hasRunOnce = true;
        _viewBridge.EnableOverlay();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _showCenterBusyForCurrentRun, showCenterBusyForCurrentRun ? 1 : 0);
        if (showCenterBusyForCurrentRun)
        {
            _viewBridge.SetBusyOverlay(true, isFirstRun ? "Initializing OCR..." : "OCR running...");
        }
        if (!options.SuppressTransientUiFeedback)
        {
            _viewBridge.ShowLoadingSpinnerForRun(settings);
        }

        try
        {
            var sceneSettings = _featureSettingsProvider.GetScene(settings);
            var payloadCandidate = semanticPayload ?? _consumePendingSemanticPayload();
            if (TryResolveSceneSemanticPayload(
                    settings,
                    sceneSettings,
                    options,
                    payloadCandidate,
                    out var reusablePayload,
                    out var reason))
            {
                _loggerAccessor()?.Info(
                    $"Scene semantic payload reused for auto-translate (units={reusablePayload.ReadingUnits.Count}, age={(DateTime.UtcNow - reusablePayload.CapturedAtUtc).TotalMilliseconds:0}ms).");
                await pipeline.RunWithReadingUnitsAsync(
                        reusablePayload.ReadingUnits,
                        reusablePayload.RoiScreen,
                        reusablePayload.OverlayClipScreen,
                        _runCts.Token,
                        options)
                    .ConfigureAwait(true);
            }
            else
            {
                if (payloadCandidate != null && !string.IsNullOrWhiteSpace(reason))
                {
                    _loggerAccessor()?.Info($"Scene semantic payload fallback to full OCR: {reason}.");
                }

                await pipeline.RunOnceAsync(_runCts.Token, options).ConfigureAwait(true);
            }
        }
        finally
        {
            if (!options.SuppressTransientUiFeedback)
            {
                _viewBridge.HideLoadingSpinnerForRun();
            }

            if (showCenterBusyForCurrentRun)
            {
                _viewBridge.SetBusyOverlay(false, null);
            }

            Interlocked.Exchange(ref _showCenterBusyForCurrentRun, 0);
            Interlocked.Exchange(ref _runInProgress, 0);
            _viewBridge.CancelTranslationOverlay();
            _tryDrainPendingSceneAutoTranslate();
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }

    private static bool TryResolveSceneSemanticPayload(
        AppSettings settings,
        SceneFeatureSettings sceneSettings,
        ForceRunOptions options,
        SceneTextSnapshot? payload,
        out SceneTextSnapshot reusablePayload,
        out string? reason)
    {
        reusablePayload = null!;
        reason = null;

        if (options.Trigger != RunTrigger.AutoSceneChange)
        {
            reason = "non auto-scene run";
            return false;
        }

        if (payload == null)
        {
            reason = "no semantic payload";
            return false;
        }

        if (!sceneSettings.EnableSceneChangeSemanticGate)
        {
            reason = "semantic gate disabled";
            return false;
        }

        if (payload.ReadingUnits.Count == 0)
        {
            reason = "payload has no reading units";
            return false;
        }

        var ageMs = (DateTime.UtcNow - payload.CapturedAtUtc).TotalMilliseconds;
        // WHY: Quiet-window auto-scene runs intentionally wait for text stabilization, so strict payload TTL would drop useful snapshots.
        if (ageMs > SceneSemanticPayloadTtlMs &&
            !(options.Trigger == RunTrigger.AutoSceneChange && settings.EnableSceneChangeQuietWindow))
        {
            reason = $"payload stale ({ageMs:0} ms)";
            return false;
        }

        var expectedSignature = SceneTextSnapshotService.BuildSnapshotSignature(settings);
        if (!string.Equals(expectedSignature, payload.SnapshotSignature, StringComparison.Ordinal))
        {
            reason = "payload signature mismatch";
            return false;
        }

        reusablePayload = payload;
        return true;
    }
}

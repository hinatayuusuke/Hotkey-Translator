using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Application;

internal sealed class SceneChangeController : IDisposable
{
    private const int OverlayBaselineDelayMs = 150;
    private const int SceneChangePendingLogSuppressionMs = 2000;

    // WHY: Keep watcher-triggered runs on the same option profile used before extraction for behavior compatibility.
    private static readonly ForceRunOptions AutoSceneChangeRunOptions = new(
        SkipPhash: false,
        SkipOcrDiff: false,
        SkipTranslationCache: false,
        SkipTranslation: false,
        ForceGeminiStrict: false,
        Trigger: RunTrigger.AutoSceneChange,
        SuppressTransientUiFeedback: true);

    private readonly Dispatcher _dispatcher;
    private readonly SettingsService _settingsService;
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Func<CaptureManager?> _captureManagerAccessor;
    private readonly Func<PhashService?> _phashServiceAccessor;
    private readonly Func<SceneTextSnapshotService?> _snapshotServiceAccessor;
    private readonly Func<OverlayPresenter?> _overlayPresenterAccessor;
    private readonly Func<AppSettings, Rect, Rect> _resolveRoiBounds;
    private readonly Func<ForceRunOptions, SceneTextSnapshot?, Task> _runOnceAsync;
    private readonly Func<bool> _isRunInProgress;
    private readonly Action<string> _appendLog;
    private readonly Action<bool> _setOverlayEnabledState;

    private DispatcherTimer? _autoHideTimer;
    private bool _autoHideTickInProgress;
    private ulong? _autoHideLastHash;
    private CancellationTokenSource? _autoHideBaselineCts;
    private bool _autoHideBaselinePending;
    private int _autoHideBaselineVersion;
    private DateTime _lastSceneChangeAutoTranslateRequestUtc = DateTime.MinValue;
    private bool _sceneChangeAutoTranslatePending;
    private int _sceneChangeAutoTranslatePendingDiff;
    private int _sceneChangeAutoTranslatePendingThreshold;
    private string? _sceneChangeAutoTranslatePendingReason;
    private DateTime _lastSceneChangeAutoTranslatePendingLogUtc = DateTime.MinValue;
    private SceneTextSnapshot? _sceneChangeAutoTranslatePendingPayload;
    private SceneTextSnapshot? _lastSceneTextSnapshot;
    private int _semanticCandidateStreak;
    private bool _overlayVisible;

    public SceneChangeController(
        Dispatcher dispatcher,
        SettingsService settingsService,
        Func<AppLogger?> loggerAccessor,
        Func<CaptureManager?> captureManagerAccessor,
        Func<PhashService?> phashServiceAccessor,
        Func<SceneTextSnapshotService?> snapshotServiceAccessor,
        Func<OverlayPresenter?> overlayPresenterAccessor,
        Func<AppSettings, Rect, Rect> resolveRoiBounds,
        Func<ForceRunOptions, SceneTextSnapshot?, Task> runOnceAsync,
        Func<bool> isRunInProgress,
        Action<string> appendLog,
        Action<bool> setOverlayEnabledState)
    {
        _dispatcher = dispatcher;
        _settingsService = settingsService;
        _loggerAccessor = loggerAccessor;
        _captureManagerAccessor = captureManagerAccessor;
        _phashServiceAccessor = phashServiceAccessor;
        _snapshotServiceAccessor = snapshotServiceAccessor;
        _overlayPresenterAccessor = overlayPresenterAccessor;
        _resolveRoiBounds = resolveRoiBounds;
        _runOnceAsync = runOnceAsync;
        _isRunInProgress = isRunInProgress;
        _appendLog = appendLog;
        _setOverlayEnabledState = setOverlayEnabledState;
    }

    public void Initialize(AppSettings settings)
    {
        _autoHideTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _autoHideTimer.Tick -= OnAutoHideTick;
        _autoHideTimer.Tick += OnAutoHideTick;
        UpdateWatcher(settings);
    }

    public void UpdateWatcher(AppSettings settings)
    {
        if (_autoHideTimer == null)
        {
            return;
        }

        if (!ShouldWatchSceneChanges(settings))
        {
            StopWatcher();
            return;
        }

        var intervalMs = Math.Clamp(settings.SceneChangeWatchIntervalMs, 200, 10000);
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        if (!_autoHideTimer.IsEnabled)
        {
            _autoHideTimer.Start();
        }

        if (!_autoHideBaselinePending && !_autoHideLastHash.HasValue)
        {
            ScheduleBaselineReset();
        }
    }

    public void OnOverlayShown()
    {
        _overlayVisible = true;
        UpdateWatcher(_settingsService.Settings);
        ScheduleBaselineReset();
    }

    public void OnOverlayHidden()
    {
        _overlayVisible = false;
        UpdateWatcher(_settingsService.Settings);
    }

    public void OnOverlayUpdated()
    {
        ScheduleBaselineReset();
    }

    public SceneTextSnapshot? ConsumePendingAutoTranslatePayload()
    {
        var payload = _sceneChangeAutoTranslatePendingPayload;
        _sceneChangeAutoTranslatePendingPayload = null;
        return payload;
    }

    public bool TryDrainPendingAutoTranslate()
    {
        var settings = _settingsService.Settings;
        if (!settings.EnableSceneChangeAutoTranslate)
        {
            ClearPendingAutoTranslate("auto-translate disabled");
            return false;
        }

        if (!_sceneChangeAutoTranslatePending)
        {
            return false;
        }

        if (_isRunInProgress())
        {
            return false;
        }

        var cooldownMs = Math.Clamp(settings.SceneChangeWatchIntervalMs, 200, 10000);
        var now = DateTime.UtcNow;
        if (_lastSceneChangeAutoTranslateRequestUtc != DateTime.MinValue &&
            (now - _lastSceneChangeAutoTranslateRequestUtc).TotalMilliseconds < cooldownMs)
        {
            return false;
        }

        var diff = _sceneChangeAutoTranslatePendingDiff;
        var threshold = _sceneChangeAutoTranslatePendingThreshold;
        var payload = _sceneChangeAutoTranslatePendingPayload;
        ResetPendingAutoTranslate();
        _lastSceneChangeAutoTranslateRequestUtc = now;
        _appendLog($"Scene change auto-translate pending drained: triggered run (diff {diff}, threshold {threshold}).");
        _ = _runOnceAsync(AutoSceneChangeRunOptions, payload);
        return true;
    }

    public void ClearPendingAutoTranslate(string reason)
    {
        if (!_sceneChangeAutoTranslatePending)
        {
            ResetPendingAutoTranslate();
            return;
        }

        _appendLog($"Scene change auto-translate pending cleared: {reason}.");
        ResetPendingAutoTranslate();
    }

    public void Dispose()
    {
        _autoHideBaselineCts?.Cancel();
        _autoHideBaselineCts?.Dispose();
        _autoHideBaselineCts = null;

        if (_autoHideTimer != null)
        {
            _autoHideTimer.Stop();
            _autoHideTimer.Tick -= OnAutoHideTick;
            _autoHideTimer = null;
        }
    }

    private void StopWatcher()
    {
        if (_autoHideTimer != null && _autoHideTimer.IsEnabled)
        {
            _autoHideTimer.Stop();
        }

        _lastSceneChangeAutoTranslateRequestUtc = DateTime.MinValue;
        ClearPendingAutoTranslate("watcher stopped");
        ClearBaseline();
        ResetSceneSemanticState();
    }

    private void ClearBaseline()
    {
        _autoHideBaselineVersion++;
        _autoHideLastHash = null;
        _autoHideBaselinePending = false;
        if (_autoHideBaselineCts != null)
        {
            _autoHideBaselineCts.Cancel();
            _autoHideBaselineCts.Dispose();
            _autoHideBaselineCts = null;
        }

        ResetSceneSemanticState();
    }

    private void ScheduleBaselineReset()
    {
        var settings = _settingsService.Settings;
        var captureManager = _captureManagerAccessor();
        var phashService = _phashServiceAccessor();
        if (!ShouldWatchSceneChanges(settings) || captureManager == null || phashService == null)
        {
            return;
        }

        var perfEnabled = settings.EnableOcrPerfLog && settings.EnableLogging;
        var perfThresholdMs = Math.Max(0, settings.OcrPerfLogThresholdMs);
        _autoHideBaselineVersion++;
        _autoHideBaselinePending = true;
        _autoHideLastHash = null;
        _autoHideBaselineCts?.Cancel();
        _autoHideBaselineCts?.Dispose();
        _autoHideBaselineCts = new CancellationTokenSource();
        var token = _autoHideBaselineCts.Token;

        _ = Task.Run(async () =>
        {
            Stopwatch? baselineStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
            try
            {
                // WHY: Delay a bit so the overlay frame is fully composed before hashing.
                await Task.Delay(OverlayBaselineDelayMs, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                using var frame = captureManager.Capture(settings);
                if (frame.IsBlack)
                {
                    return;
                }

                var roiScreen = _resolveRoiBounds(settings, frame.Bounds);
                if (roiScreen.IsEmpty)
                {
                    return;
                }

                var roiInFrame = new Rect(
                    roiScreen.X - frame.Bounds.X,
                    roiScreen.Y - frame.Bounds.Y,
                    roiScreen.Width,
                    roiScreen.Height);

                using var roiBitmap = BitmapHelper.Crop(frame.Bitmap, roiInFrame);
                _autoHideLastHash = phashService.ComputeHash(roiBitmap);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _loggerAccessor()?.Error(ex, "Auto-hide baseline reset failed.");
            }
            finally
            {
                if (baselineStopwatch != null)
                {
                    baselineStopwatch.Stop();
                    if (baselineStopwatch.ElapsedMilliseconds >= perfThresholdMs)
                    {
                        _loggerAccessor()?.Info($"[Perf] AutoHideBaselineReset={baselineStopwatch.ElapsedMilliseconds}ms.");
                    }
                }

                _autoHideBaselinePending = false;
            }
        }, token);
    }

    private async void OnAutoHideTick(object? sender, EventArgs e)
    {
        var captureManager = _captureManagerAccessor();
        var phashService = _phashServiceAccessor();
        if (_autoHideTickInProgress || captureManager == null || phashService == null)
        {
            return;
        }

        var settings = _settingsService.Settings;
        if (!ShouldWatchSceneChanges(settings))
        {
            return;
        }

        if (settings.EnableSceneChangeAutoTranslate && TryDrainPendingAutoTranslate())
        {
            // WHY: A drain already scheduled a run for this tick; skip duplicate scene-change processing.
            return;
        }

        if (_autoHideBaselinePending || !_autoHideLastHash.HasValue)
        {
            return;
        }

        var baselineHash = _autoHideLastHash.Value;
        var baselineVersion = _autoHideBaselineVersion;
        var perfEnabled = settings.EnableOcrPerfLog && settings.EnableLogging;
        var perfThresholdMs = Math.Max(0, settings.OcrPerfLogThresholdMs);
        var visualDiff = -1;
        var visualThreshold = Math.Clamp(settings.SceneChangeWatchPhashThreshold, 0, 64);
        var visualHash = 0UL;
        var visualCandidateReady = false;
        _autoHideTickInProgress = true;
        try
        {
            await Task.Run(() =>
            {
                Stopwatch? watcherStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
                try
                {
                    using var frame = captureManager.Capture(settings);
                    if (frame.IsBlack)
                    {
                        return;
                    }

                    var roiScreen = _resolveRoiBounds(settings, frame.Bounds);
                    if (roiScreen.IsEmpty)
                    {
                        return;
                    }

                    var roiInFrame = new Rect(
                        roiScreen.X - frame.Bounds.X,
                        roiScreen.Y - frame.Bounds.Y,
                        roiScreen.Width,
                        roiScreen.Height);

                    using var roiBitmap = BitmapHelper.Crop(frame.Bitmap, roiInFrame);
                    var hash = phashService.ComputeHash(roiBitmap);
                    if (baselineVersion != _autoHideBaselineVersion)
                    {
                        return;
                    }

                    var diff = phashService.HammingDistance(hash, baselineHash);
                    var threshold = Math.Clamp(settings.SceneChangeWatchPhashThreshold, 0, 64);
                    if (baselineVersion != _autoHideBaselineVersion)
                    {
                        return;
                    }

                    visualDiff = diff;
                    visualThreshold = threshold;
                    visualHash = hash;
                    visualCandidateReady = true;
                    if (baselineVersion == _autoHideBaselineVersion)
                    {
                        _autoHideLastHash = visualHash;
                    }
                }
                finally
                {
                    if (watcherStopwatch != null)
                    {
                        watcherStopwatch.Stop();
                        if (watcherStopwatch.ElapsedMilliseconds >= perfThresholdMs)
                        {
                            _loggerAccessor()?.Info($"[Perf] AutoHideWatcherTick={watcherStopwatch.ElapsedMilliseconds}ms.");
                        }
                    }
                }
            }).ConfigureAwait(true);

            if (!visualCandidateReady)
            {
                return;
            }

            if (visualDiff < visualThreshold)
            {
                if (settings.EnableSceneChangeSemanticGate)
                {
                    _semanticCandidateStreak = 0;
                    _sceneChangeAutoTranslatePendingPayload = null;
                }

                return;
            }

            _loggerAccessor()?.Info($"Scene change Stage A passed (diff {visualDiff}, threshold {visualThreshold}).");

            if (!settings.EnableSceneChangeSemanticGate || _snapshotServiceAccessor() == null)
            {
                TriggerSceneChangeAction(settings, visualDiff, visualThreshold, semanticPayload: null);
                return;
            }

            await HandleSceneChangeWithSemanticGateAsync(settings, visualDiff, visualThreshold).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Scene-change watcher failed.");
        }
        finally
        {
            _autoHideTickInProgress = false;
        }
    }

    private async Task HandleSceneChangeWithSemanticGateAsync(AppSettings settings, int diff, int threshold)
    {
        var snapshotService = _snapshotServiceAccessor();
        if (snapshotService == null)
        {
            return;
        }

        var snapshot = await snapshotService.CaptureSnapshotAsync(settings, CancellationToken.None).ConfigureAwait(true);
        if (snapshot == null)
        {
            _semanticCandidateStreak = 0;
            _sceneChangeAutoTranslatePendingPayload = null;
            _loggerAccessor()?.Info("Scene change Stage B skipped (no OCR snapshot).");
            return;
        }

        if (_lastSceneTextSnapshot == null)
        {
            // WHY: Initialize semantic baseline first so watcher start does not trigger auto actions.
            _lastSceneTextSnapshot = snapshot;
            _semanticCandidateStreak = 0;
            _sceneChangeAutoTranslatePendingPayload = null;
            _loggerAccessor()?.Info("Scene change Stage B baseline initialized.");
            return;
        }

        var comparison = snapshotService.CompareSnapshots(_lastSceneTextSnapshot, snapshot, settings);
        if (!comparison.SemanticChanged)
        {
            _semanticCandidateStreak = 0;
            _sceneChangeAutoTranslatePendingPayload = null;
            _lastSceneTextSnapshot = snapshot;
            _loggerAccessor()?.Info($"Scene change Stage B blocked: {comparison.Reason}.");
            return;
        }

        var requiredTicks = Math.Max(1, settings.SceneSemanticRequireConfirmTicks);
        _semanticCandidateStreak++;
        _sceneChangeAutoTranslatePendingPayload = snapshot;
        _loggerAccessor()?.Info(
            $"Scene change Stage B passed: {comparison.Reason}, streak={_semanticCandidateStreak}/{requiredTicks}.");
        if (_semanticCandidateStreak < requiredTicks)
        {
            return;
        }

        _semanticCandidateStreak = 0;
        _lastSceneTextSnapshot = snapshot;
        TriggerSceneChangeAction(settings, diff, threshold, semanticPayload: snapshot);
    }

    private void TriggerSceneChangeAction(AppSettings settings, int diff, int threshold, SceneTextSnapshot? semanticPayload)
    {
        if (settings.EnableSceneChangeAutoHide)
        {
            _setOverlayEnabledState(false);
            _overlayPresenterAccessor()?.SetEnabled(false);
            _appendLog($"Overlay auto-hidden (watcher diff {diff}).");
            _sceneChangeAutoTranslatePendingPayload = null;
            return;
        }

        if (settings.EnableSceneChangeAutoTranslate)
        {
            QueueSceneChangeAutoTranslate(diff, threshold, semanticPayload);
        }
    }

    private void ResetSceneSemanticState()
    {
        _lastSceneTextSnapshot = null;
        _sceneChangeAutoTranslatePendingPayload = null;
        _semanticCandidateStreak = 0;
    }

    private bool ShouldWatchSceneChanges(AppSettings settings)
    {
        if (settings.EnableSceneChangeAutoTranslate)
        {
            return true;
        }

        return settings.EnableSceneChangeAutoHide && _overlayVisible;
    }

    private void QueueSceneChangeAutoTranslate(int diff, int threshold, SceneTextSnapshot? semanticPayload)
    {
        var settings = _settingsService.Settings;
        if (!settings.EnableSceneChangeAutoTranslate)
        {
            ClearPendingAutoTranslate("auto-translate disabled");
            return;
        }

        var cooldownMs = Math.Clamp(settings.SceneChangeWatchIntervalMs, 200, 10000);
        var now = DateTime.UtcNow;
        if (_lastSceneChangeAutoTranslateRequestUtc != DateTime.MinValue &&
            (now - _lastSceneChangeAutoTranslateRequestUtc).TotalMilliseconds < cooldownMs)
        {
            MarkPendingAutoTranslate(diff, threshold, $"cooldown ({cooldownMs} ms)", semanticPayload);
            return;
        }

        if (_isRunInProgress())
        {
            MarkPendingAutoTranslate(diff, threshold, "OCR already running", semanticPayload);
            return;
        }

        if (_sceneChangeAutoTranslatePending)
        {
            ClearPendingAutoTranslate("coalesced by immediate trigger");
        }

        _lastSceneChangeAutoTranslateRequestUtc = now;
        _appendLog($"Scene change detected: auto-translate triggered (diff {diff}, threshold {threshold}).");
        _ = _runOnceAsync(AutoSceneChangeRunOptions, semanticPayload);
    }

    private void MarkPendingAutoTranslate(int diff, int threshold, string reason, SceneTextSnapshot? semanticPayload)
    {
        _sceneChangeAutoTranslatePending = true;
        _sceneChangeAutoTranslatePendingDiff = Math.Max(_sceneChangeAutoTranslatePendingDiff, diff);
        _sceneChangeAutoTranslatePendingThreshold = Math.Max(0, threshold);
        if (semanticPayload != null)
        {
            _sceneChangeAutoTranslatePendingPayload = semanticPayload;
        }

        var now = DateTime.UtcNow;
        var shouldLog = !string.Equals(_sceneChangeAutoTranslatePendingReason, reason, StringComparison.Ordinal) ||
                        _lastSceneChangeAutoTranslatePendingLogUtc == DateTime.MinValue ||
                        (now - _lastSceneChangeAutoTranslatePendingLogUtc).TotalMilliseconds >=
                        SceneChangePendingLogSuppressionMs;
        _sceneChangeAutoTranslatePendingReason = reason;
        if (!shouldLog)
        {
            return;
        }

        _lastSceneChangeAutoTranslatePendingLogUtc = now;
        _appendLog(
            $"Scene change auto-translate pending: {reason} (diff {_sceneChangeAutoTranslatePendingDiff}, threshold {_sceneChangeAutoTranslatePendingThreshold}).");
    }

    private void ResetPendingAutoTranslate()
    {
        _sceneChangeAutoTranslatePending = false;
        _sceneChangeAutoTranslatePendingDiff = 0;
        _sceneChangeAutoTranslatePendingThreshold = 0;
        _sceneChangeAutoTranslatePendingReason = null;
        _sceneChangeAutoTranslatePendingPayload = null;
        _lastSceneChangeAutoTranslatePendingLogUtc = DateTime.MinValue;
    }
}

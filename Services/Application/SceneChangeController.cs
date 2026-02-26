using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.Settings.FeatureSettings;

namespace Hotkey_Translator.Services.Application;

internal sealed class SceneChangeController : IDisposable
{
    private const int OverlayBaselineDelayMs = 150;
    private const int SceneChangePendingLogSuppressionMs = 2000;
    private const int QuietWindowDefaultMs = 450;
    private const int QuietWindowMinMs = 100;
    private const int QuietWindowMaxMs = 3000;
    private const int StageABlockPaddingPx = 3;
    private const int StageABlockMaxCount = 24;
    private const double StageABlockMinSizePx = 8.0;
    private const double StageABlockMinCoveredAreaRatio = 0.02;

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
    private readonly FeatureSettingsProvider _featureSettingsProvider = new();

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
    private bool _quietWindowPending;
    private DateTime _quietWindowLastChangeUtc = DateTime.MinValue;
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

        var scene = _featureSettingsProvider.GetScene(settings);
        var intervalMs = Math.Clamp(scene.SceneChangeWatchIntervalMs, 200, 10000);
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
        var scene = _featureSettingsProvider.GetScene(settings);
        if (!scene.EnableSceneChangeAutoTranslate)
        {
            ClearPendingAutoTranslate("auto-translate disabled");
            return false;
        }

        if (!_sceneChangeAutoTranslatePending)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (scene.EnableSceneChangeQuietWindow && _quietWindowPending)
        {
            var quietWindowMs = ResolveQuietWindowMs(scene);
            if (_quietWindowLastChangeUtc == DateTime.MinValue ||
                (now - _quietWindowLastChangeUtc).TotalMilliseconds < quietWindowMs)
            {
                return false;
            }

            _loggerAccessor()?.Info(
                $"stage=scene_change event=quiet_ready quiet_ms={quietWindowMs} waited_ms={(now - _quietWindowLastChangeUtc).TotalMilliseconds:0}.");
        }

        if (_isRunInProgress())
        {
            return false;
        }

        var cooldownMs = Math.Clamp(scene.SceneChangeWatchIntervalMs, 200, 10000);
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
        var scene = _featureSettingsProvider.GetScene(settings);
        if (!ShouldWatchSceneChanges(scene))
        {
            return;
        }

        if (scene.EnableSceneChangeAutoTranslate && TryDrainPendingAutoTranslate())
        {
            // WHY: A drain already scheduled a run for this tick; skip duplicate scene-change processing.
            return;
        }

        if (_autoHideBaselinePending || !_autoHideLastHash.HasValue)
        {
            // WHY: Block-scoped Stage A can run without ROI baseline hash when semantic snapshot is available.
            if (_autoHideBaselinePending || !HasUsableBlockScopedSnapshot(settings))
            {
                return;
            }
        }

        var baselineHash = _autoHideLastHash;
        var blockScopedSnapshot = GetUsableBlockScopedSnapshot(settings);
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
                    var usedBlockScoped = false;
                    if (blockScopedSnapshot != null)
                    {
                        if (TryComputeBlockScopedVisualDiff(
                                roiBitmap,
                                roiScreen,
                                blockScopedSnapshot,
                                phashService,
                                out var blockDiff,
                                out var coveredAreaRatio,
                                out var matchedBlocks,
                                out var skipReason))
                        {
                            var threshold = Math.Clamp(settings.SceneChangeWatchPhashThreshold, 0, 64);
                            visualDiff = blockDiff;
                            visualThreshold = threshold;
                            visualCandidateReady = true;
                            usedBlockScoped = true;
                            _loggerAccessor()?.Info(
                                $"stage=scene_change event=stage_a_blocks blocks={matchedBlocks} coveredAreaRatio={coveredAreaRatio:0.###} diff={blockDiff} threshold={threshold}.");
                        }
                        else
                        {
                            _loggerAccessor()?.Info(
                                $"stage=scene_change event=stage_a_fallback reason={skipReason ?? "unknown"} mode=roi.");
                        }
                    }

                    if (usedBlockScoped)
                    {
                        return;
                    }

                    if (!baselineHash.HasValue)
                    {
                        return;
                    }

                    var hash = phashService.ComputeHash(roiBitmap);
                    if (baselineVersion != _autoHideBaselineVersion)
                    {
                        return;
                    }

                    var diff = phashService.HammingDistance(hash, baselineHash.Value);
                    var thresholdRoi = Math.Clamp(settings.SceneChangeWatchPhashThreshold, 0, 64);
                    if (baselineVersion != _autoHideBaselineVersion)
                    {
                        return;
                    }

                    visualDiff = diff;
                    visualThreshold = thresholdRoi;
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
                    if (!_sceneChangeAutoTranslatePending)
                    {
                        _sceneChangeAutoTranslatePendingPayload = null;
                    }
                }

                return;
            }

            _loggerAccessor()?.Info($"Scene change Stage A passed (diff {visualDiff}, threshold {visualThreshold}).");

            if (!scene.EnableSceneChangeSemanticGate || _snapshotServiceAccessor() == null)
            {
                TriggerSceneChangeAction(settings, visualDiff, visualThreshold, semanticPayload: null);
                return;
            }

            await HandleSceneChangeWithSemanticGateAsync(settings, scene, visualDiff, visualThreshold).ConfigureAwait(true);
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

    private async Task HandleSceneChangeWithSemanticGateAsync(
        AppSettings settings,
        SceneFeatureSettings scene,
        int diff,
        int threshold)
    {
        if (!scene.EnableSceneChangeSemanticGate)
        {
            return;
        }

        var snapshotService = _snapshotServiceAccessor();
        if (snapshotService == null)
        {
            return;
        }

        var snapshot = await snapshotService.CaptureSnapshotAsync(settings, CancellationToken.None).ConfigureAwait(true);
        if (snapshot == null)
        {
            _semanticCandidateStreak = 0;
            if (!_sceneChangeAutoTranslatePending)
            {
                _sceneChangeAutoTranslatePendingPayload = null;
            }

            _loggerAccessor()?.Info("Scene change Stage B skipped (no OCR snapshot).");
            return;
        }

        if (_lastSceneTextSnapshot == null)
        {
            // WHY: Initialize semantic baseline first so watcher start does not trigger auto actions.
            _lastSceneTextSnapshot = snapshot;
            _semanticCandidateStreak = 0;
            if (!_sceneChangeAutoTranslatePending)
            {
                _sceneChangeAutoTranslatePendingPayload = null;
            }

            _loggerAccessor()?.Info("Scene change Stage B baseline initialized.");
            return;
        }

        var comparison = snapshotService.CompareSnapshots(_lastSceneTextSnapshot, snapshot, settings);
        if (!comparison.SemanticChanged)
        {
            _semanticCandidateStreak = 0;
            if (!_sceneChangeAutoTranslatePending)
            {
                _sceneChangeAutoTranslatePendingPayload = null;
            }

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
        return ShouldWatchSceneChanges(_featureSettingsProvider.GetScene(settings));
    }

    private bool ShouldWatchSceneChanges(SceneFeatureSettings scene)
    {
        if (scene.EnableSceneChangeAutoTranslate)
        {
            return true;
        }

        return scene.EnableSceneChangeAutoHide && _overlayVisible;
    }

    private void QueueSceneChangeAutoTranslate(int diff, int threshold, SceneTextSnapshot? semanticPayload)
    {
        var settings = _settingsService.Settings;
        var scene = _featureSettingsProvider.GetScene(settings);
        if (!scene.EnableSceneChangeAutoTranslate)
        {
            ClearPendingAutoTranslate("auto-translate disabled");
            return;
        }

        var now = DateTime.UtcNow;
        if (scene.EnableSceneChangeQuietWindow)
        {
            MarkQuietWindowPending(scene, diff, threshold, semanticPayload, now);
            return;
        }

        var cooldownMs = Math.Clamp(scene.SceneChangeWatchIntervalMs, 200, 10000);
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

    private void MarkQuietWindowPending(
        SceneFeatureSettings scene,
        int diff,
        int threshold,
        SceneTextSnapshot? semanticPayload,
        DateTime now)
    {
        var wasPending = _quietWindowPending;
        _quietWindowPending = true;
        _quietWindowLastChangeUtc = now;
        MarkPendingAutoTranslate(diff, threshold, "quiet window", semanticPayload);
        var quietWindowMs = ResolveQuietWindowMs(scene);
        var quietEvent = wasPending ? "quiet_extended" : "quiet_pending";
        _loggerAccessor()?.Info(
            $"stage=scene_change event={quietEvent} quiet_ms={quietWindowMs} diff={diff} threshold={threshold}.");
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
        _quietWindowPending = false;
        _quietWindowLastChangeUtc = DateTime.MinValue;
    }

    private static int ResolveQuietWindowMs(SceneFeatureSettings scene)
    {
        var quietWindowMs = scene.SceneChangeQuietWindowMs <= 0
            ? QuietWindowDefaultMs
            : scene.SceneChangeQuietWindowMs;
        return Math.Clamp(quietWindowMs, QuietWindowMinMs, QuietWindowMaxMs);
    }

    private bool HasUsableBlockScopedSnapshot(AppSettings settings)
    {
        return GetUsableBlockScopedSnapshot(settings) != null;
    }

    private SceneTextSnapshot? GetUsableBlockScopedSnapshot(AppSettings settings)
    {
        var snapshot = _lastSceneTextSnapshot;
        if (snapshot == null)
        {
            return null;
        }

        if (snapshot.VisualBlocks.Count == 0)
        {
            return null;
        }

        // WHY: Block coordinates depend on OCR/ROI settings; avoid mixing snapshots from incompatible config.
        var signature = SceneTextSnapshotService.BuildSnapshotSignature(settings);
        if (!string.Equals(snapshot.SnapshotSignature, signature, StringComparison.Ordinal))
        {
            return null;
        }

        return snapshot;
    }

    private static bool TryComputeBlockScopedVisualDiff(
        Bitmap roiBitmap,
        Rect roiScreen,
        SceneTextSnapshot snapshot,
        PhashService phashService,
        out int diff,
        out double coveredAreaRatio,
        out int matchedBlocks,
        out string? skipReason)
    {
        diff = 0;
        coveredAreaRatio = 0.0;
        matchedBlocks = 0;
        skipReason = null;

        if (snapshot.VisualBlocks.Count == 0)
        {
            skipReason = "no_visual_blocks";
            return false;
        }

        var roiLocal = new Rect(0, 0, roiBitmap.Width, roiBitmap.Height);
        var weightedDiffSum = 0.0;
        var weightSum = 0.0;
        var coveredArea = 0.0;

        foreach (var block in snapshot.VisualBlocks
                     .OrderByDescending(item => item.Area)
                     .Take(StageABlockMaxCount))
        {
            var local = new Rect(
                block.Rect.X - roiScreen.X - StageABlockPaddingPx,
                block.Rect.Y - roiScreen.Y - StageABlockPaddingPx,
                block.Rect.Width + (StageABlockPaddingPx * 2),
                block.Rect.Height + (StageABlockPaddingPx * 2));
            var clipped = Rect.Intersect(local, roiLocal);
            if (clipped.IsEmpty || clipped.Width < StageABlockMinSizePx || clipped.Height < StageABlockMinSizePx)
            {
                continue;
            }

            using var crop = BitmapHelper.Crop(roiBitmap, clipped);
            var currentHash = phashService.ComputeHash(crop);
            var delta = phashService.HammingDistance(currentHash, block.Hash);
            var area = clipped.Width * clipped.Height;
            weightedDiffSum += delta * area;
            weightSum += area;
            coveredArea += area;
            matchedBlocks++;
        }

        if (matchedBlocks == 0 || weightSum <= 0)
        {
            skipReason = "no_valid_block_intersections";
            return false;
        }

        var roiArea = Math.Max(1.0, roiBitmap.Width * roiBitmap.Height);
        coveredAreaRatio = coveredArea / roiArea;
        if (coveredAreaRatio < StageABlockMinCoveredAreaRatio)
        {
            skipReason = $"low_covered_area_ratio({coveredAreaRatio:0.###})";
            return false;
        }

        diff = (int)Math.Round(weightedDiffSum / weightSum);
        return true;
    }
}

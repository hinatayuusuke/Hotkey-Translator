using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services.Orchestration;
using Hotkey_Translator.Services.Orchestration.Stages;
using Hotkey_Translator.Services.Hook;
using Hotkey_Translator.Services.Hook.Contracts;
using Hotkey_Translator.Services.Settings.FeatureSettings;

namespace Hotkey_Translator.Services;

public enum RunTrigger
{
    Manual = 0,
    AutoSceneChange = 1,
}

public readonly record struct ForceRunOptions(
    bool SkipPhash,
    bool SkipOcrDiff,
    bool SkipTranslationCache,
    bool SkipTranslation,
    bool ForceGeminiStrict = false,
    RunTrigger Trigger = RunTrigger.Manual,
    bool SuppressTransientUiFeedback = false)
{
    public static ForceRunOptions None => new(false, false, false, false);

    public bool IsEnabled => SkipPhash || SkipOcrDiff || SkipTranslationCache || SkipTranslation || ForceGeminiStrict;
}

public sealed class PipelineOrchestrator
{
    private readonly CaptureManager _captureManager;
    private readonly GraphicsHookClientService? _graphicsHookClientService;
    private readonly OcrDiffService _ocrDiffService;
    private readonly PhashService _phashService;
    private readonly OcrAndGroupStage _ocrAndGroupStage;
    private readonly DiffStage _diffStage;
    private readonly TranslateStage _translateStage;
    private readonly OverlayPresenter _overlayPresenter;
    private readonly OverlayStage _overlayStage;
    private readonly SettingsService _settingsService;
    private readonly FeatureSettingsProvider _featureSettingsProvider = new();
    private readonly AppLogger _logger;
    private readonly bool _overlayV2WriteDebugEnabled =
        string.Equals(Environment.GetEnvironmentVariable("HT_HOOK_OVL_WRITE_DEBUG"), "1", StringComparison.Ordinal);
    private readonly bool _overlayV2TraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("HT_HOOK_OVL_TRACE"), "1", StringComparison.Ordinal);
    private readonly bool _hookV2StatusTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("HT_HOOK_V2_STATUS_TRACE"), "1", StringComparison.Ordinal);
    private readonly bool _hookRoiTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("HT_HOOK_ROI_TRACE"), "1", StringComparison.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _hookRoiPreviewSync = new();
    private OverlayTextMode _overlayTextMode = OverlayTextMode.Translated;
    private IReadOnlyList<ReadingUnit>? _lastReadingUnits;
    private Dictionary<int, string> _lastOverlayTranslations = new();
    private Rect _lastOverlayRoiScreen;
    private Rect? _lastOverlayClipScreen;
    private ulong? _lastHash;
    private Bitmap? _lastRoiSnapshot;
    private ulong _overlayV2WriteAttemptSeq;
    private CaptureProviderKind? _lastCaptureProviderKind;
    private Rect? _lastCaptureFrameBounds;
    private uint _lastCaptureCanvasW;
    private uint _lastCaptureCanvasH;
    private Rect? _hookRoiPreviewRectScreen;
    private string? _lastHookRoiSkipReason;
    private bool? _lastWpfOverlaySuppressed;

    public event Action<Bitmap>? OcrPreprocessPreviewReady;
    public event Action? TranslationStarted;
    public event Action? TranslationCompleted;

    public bool TryGetLastRoiHash(out ulong hash)
    {
        if (_lastRoiSnapshot == null)
        {
            hash = 0;
            return false;
        }

        hash = _phashService.ComputeHash(_lastRoiSnapshot);
        return true;
    }

    internal PipelineOrchestrator(
        CaptureManager captureManager,
        GraphicsHookClientService? graphicsHookClientService,
        OcrEngine ocrEngine,
        OcrDiffService ocrDiffService,
        PhashService phashService,
        NormalizationService normalizationService,
        OcrPreprocessService ocrPreprocessService,
        OcrLineGrouper lineGrouper,
        CacheRepository cacheRepository,
        CacheKeyBuilder cacheKeyBuilder,
        TranslationFallbackService translationService,
        OverlayPresenter overlayPresenter,
        SettingsService settingsService,
        AppLogger logger)
    {
        _captureManager = captureManager;
        _graphicsHookClientService = graphicsHookClientService;
        _ocrDiffService = ocrDiffService;
        _phashService = phashService;
        _logger = logger;
        var preprocessCoordinator = new OcrPreprocessCoordinator(ocrEngine, ocrPreprocessService, new OcrCandidateScorer(), _logger);
        _ocrAndGroupStage = new OcrAndGroupStage(preprocessCoordinator, lineGrouper, new ReadingUnitBuilder(), _logger);
        _diffStage = new DiffStage(_ocrDiffService);
        _translateStage = new TranslateStage(
            normalizationService,
            new TranslationTextNormalizer(),
            cacheRepository,
            cacheKeyBuilder,
            translationService,
            _logger);
        _overlayPresenter = overlayPresenter;
        _overlayStage = new OverlayStage(_overlayPresenter);
        _settingsService = settingsService;
    }

    public Task RunOnceAsync(CancellationToken cancellationToken)
    {
        return RunOnceAsync(cancellationToken, ForceRunOptions.None);
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken, ForceRunOptions options)
    {
        var waitStopwatch = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        waitStopwatch.Stop();
        Bitmap? roiSnapshot = null;
        PipelinePerfProbe? perfProbe = null;
        var suppressWpfOverlay = false;
        try
        {
            var settings = _settingsService.Settings;
            var ocrFeatureSettings = _featureSettingsProvider.GetOcr(settings);
            var context = new PipelineExecutionContext(settings, options, DateTimeOffset.UtcNow);
            context.FinalStageResult = PipelineStageResult.ContinueExecution();
            var perfEnabled = settings.EnableOcrPerfLog && settings.EnableLogging;
            perfProbe = new PipelinePerfProbe(perfEnabled, settings.OcrPerfLogThresholdMs);
            _logger.Info(
                $"Run context: trigger={options.Trigger}, suppress transient UI={options.SuppressTransientUiFeedback}.");
            perfProbe.RecordQueueWait(waitStopwatch);
            _ocrDiffService.IouThreshold = settings.OcrIouThreshold;

            if (options.IsEnabled)
            {
                if (options.SkipTranslation && !options.SkipPhash && !options.SkipOcrDiff && !options.SkipTranslationCache)
                {
                    _logger.Info("OCR-only run: translation skipped.");
                }
                else
                {
                    _logger.Info($"Force run: skip pHash={options.SkipPhash}, skip OCR diff={options.SkipOcrDiff}, " +
                                 $"skip translation cache={options.SkipTranslationCache}, skip translation={options.SkipTranslation}, " +
                                 $"force gemini strict={options.ForceGeminiStrict}.");
                }
            }

            var captureStopwatch = perfProbe.BeginStep();
            using var frame = _captureManager.Capture(settings);
            context.Frame = frame;
            perfProbe.RecordCapture(captureStopwatch);
            _lastCaptureProviderKind = frame.ProviderKind;
            _lastCaptureFrameBounds = frame.Bounds;
            _lastCaptureCanvasW = (uint)Math.Max(0, frame.Bitmap.Width);
            _lastCaptureCanvasH = (uint)Math.Max(0, frame.Bitmap.Height);
            suppressWpfOverlay = ShouldSuppressWpfOverlay(settings, frame.ProviderKind);
            LogOverlayRouteIfChanged(suppressWpfOverlay, frame.ProviderKind);

            if (frame.IsBlack)
            {
                _logger.Info($"Black frame detected from {frame.ProviderKind}. Keeping last overlay.");
                if (ApplyStopResult(
                        context,
                        PipelineStageResult.Stop(
                            PipelineStopReason.BlackFrame,
                            PipelineOverlayAction.ShowLast,
                            "Black frame detected."),
                        frame.Bounds,
                        suppressWpfOverlay: suppressWpfOverlay))
                {
                    return;
                }
            }

            RememberPreferredProvider(settings, frame.ProviderKind);
            var roiScreen = GetRoiBounds(settings, frame.Bounds);
            context.RoiScreen = roiScreen;
            if (roiScreen.IsEmpty)
            {
                _logger.Info("ROI is outside capture bounds.");
                if (ApplyStopResult(
                        context,
                        PipelineStageResult.Stop(
                            PipelineStopReason.RoiOutOfBounds,
                            PipelineOverlayAction.ShowLast,
                            "ROI is outside capture bounds."),
                        frame.Bounds,
                        suppressWpfOverlay: suppressWpfOverlay))
                {
                    return;
                }
            }
            var overlayClipScreen = ResolveOverlayClipScreenRect(settings, frame.Bounds);
            context.OverlayClipScreen = overlayClipScreen;

            var roiInFrame = new Rect(
                roiScreen.X - frame.Bounds.X,
                roiScreen.Y - frame.Bounds.Y,
                roiScreen.Width,
                roiScreen.Height);

            var cropStopwatch = perfProbe.BeginStep();
            using var roiBitmap = BitmapHelper.Crop(frame.Bitmap, roiInFrame);
            perfProbe.RecordCrop(cropStopwatch);
            roiSnapshot = (Bitmap)roiBitmap.Clone();
            context.RoiSnapshot = roiSnapshot;
            context.RoiSnapshotBounds = roiScreen;

            if (settings.PhashThreshold >= 0)
            {
                var hash = _phashService.ComputeHash(roiBitmap);
                if (!options.SkipPhash && _lastHash.HasValue && _phashService.IsSimilar(hash, _lastHash.Value, settings.PhashThreshold))
                {
                    _logger.Info("pHash unchanged; keeping last overlay.");
                    if (ApplyStopResult(
                            context,
                            PipelineStageResult.Stop(
                            PipelineStopReason.UnchangedHash,
                            PipelineOverlayAction.ShowLast,
                            "pHash unchanged."),
                            frame.Bounds,
                            suppressWpfOverlay: suppressWpfOverlay))
                    {
                        return;
                    }
                }

                _lastHash = hash;
                context.RoiHash = hash;
            }
            else
            {
                _lastHash = null;
            }

            Bitmap? ocrInput = null;
            try
            {
                var ocrStageOutput = await _ocrAndGroupStage.ExecuteAsync(roiBitmap, roiScreen, settings, cancellationToken)
                    .ConfigureAwait(false);
                context.OcrResult = ocrStageOutput.OcrResult;
                context.GroupedLines = ocrStageOutput.GroupedLines;
                context.ReadingUnits = ocrStageOutput.ReadingUnits;
                ocrInput = ocrStageOutput.OcrInput;

                NotifyOcrPreprocessPreview(ocrInput);

                if (perfProbe.Enabled)
                {
                    perfProbe.RecordOcr(ocrStageOutput.OcrElapsedMs);
                    perfProbe.RecordGroup(ocrStageOutput.GroupElapsedMs);
                }
                _logger.Info($"OCR completed: {ocrStageOutput.RawLineCount} lines in {ocrStageOutput.OcrElapsedMs} ms.");
                if ((ocrFeatureSettings.OcrEngine == OcrEngineKind.Paddle ||
                     ocrFeatureSettings.OcrEngine == OcrEngineKind.PaddleVllm) &&
                    ocrStageOutput.PaddleConfidenceThreshold.HasValue &&
                    ocrStageOutput.FilteredLineCount != ocrStageOutput.RawLineCount)
                {
                    var threshold = ocrStageOutput.PaddleConfidenceThreshold.Value;
                    _logger.Info($"Paddle confidence filter: {ocrStageOutput.FilteredLineCount}/{ocrStageOutput.RawLineCount} lines kept (threshold={threshold:0.00}).");
                }

                if (ocrStageOutput.FilteredLineCount == 0)
                {
                    _logger.Info("OCR returned no lines after confidence filtering.");
                    if (ApplyStopResult(
                            context,
                            PipelineStageResult.Stop(
                            PipelineStopReason.NoTextDetected,
                            PipelineOverlayAction.Clear,
                            "OCR returned no lines after confidence filtering."),
                            frame.Bounds,
                            showNoTextToast: true,
                            suppressWpfOverlay: suppressWpfOverlay))
                    {
                        return;
                    }
                }

                var groupedLines = ocrStageOutput.GroupedLines;
                var readingUnits = ocrStageOutput.ReadingUnits;
                _logger.Info(
                    $"OCR grouped: {groupedLines.Count} lines, {readingUnits.Count} reading units in {ocrStageOutput.GroupElapsedMs} ms.");
                if (readingUnits.Count == 0)
                {
                    _logger.Info("OCR returned no lines.");
                    if (ApplyStopResult(
                            context,
                            PipelineStageResult.Stop(
                            PipelineStopReason.NoTextDetected,
                            PipelineOverlayAction.Clear,
                            "OCR returned no reading units."),
                            frame.Bounds,
                            showNoTextToast: true,
                            suppressWpfOverlay: suppressWpfOverlay))
                    {
                        return;
                    }
                }

                var diffOutput = _diffStage.Execute(readingUnits, groupedLines, options.SkipOcrDiff);
                context.ChangedUnitIds = diffOutput.ChangedUnitIds;
                context.IsDiffUnchanged = diffOutput.ChangedUnitIds.Count == 0;
                if (perfProbe.Enabled)
                {
                    perfProbe.RecordDiff(diffOutput.ElapsedMs);
                }
                _logger.Info(
                    $"OCR diff: {diffOutput.ChangedLines.Count} changed lines, {diffOutput.ChangedUnitIds.Count} changed units of {readingUnits.Count} total.");
                var translations = await _translateStage.ExecuteAsync(
                        readingUnits,
                        diffOutput.ChangedUnitIds,
                        settings,
                        options,
                        cancellationToken,
                        () => TranslationStarted?.Invoke(),
                        () => TranslationCompleted?.Invoke())
                    .ConfigureAwait(false);
                context.Translations.Clear();
                foreach (var pair in translations)
                {
                    context.Translations[pair.Key] = pair.Value;
                }

                var overlayItems = _overlayStage.BuildItems(readingUnits, translations, roiScreen, settings, _overlayTextMode);
                context.OverlayItems = overlayItems;
                CommitOverlayState(readingUnits, translations, roiScreen, overlayClipScreen);
                var overlayStopwatch = perfProbe.BeginStep();
                UpdateWpfOverlayRouting(overlayItems, overlayClipScreen, suppressWpfOverlay);
                // WHY: V1 rectangle command publishing is paused during phased removal. Keep HookAgent reader for compatibility.
                TryUpdateGraphicsHookOverlayV2(frame, overlayItems, settings);
                context.FinalStageResult = PipelineStageResult.ContinueExecution();
                perfProbe.RecordOverlay(overlayStopwatch);
            }
            finally
            {
                if (ocrInput != null && !ReferenceEquals(ocrInput, roiBitmap))
                {
                    ocrInput.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Pipeline canceled.");
            if (suppressWpfOverlay)
            {
                _overlayPresenter.ClearOverlay();
            }
            else
            {
                _overlayPresenter.ShowLast();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline failed.");
            if (suppressWpfOverlay)
            {
                _overlayPresenter.ClearOverlay();
            }
            else
            {
                _overlayPresenter.ShowLast();
            }
        }
        finally
        {
            perfProbe?.LogIfThresholdExceeded(_logger);

            if (roiSnapshot != null)
            {
                UpdateLastRoiSnapshot(roiSnapshot);
            }

            _gate.Release();
        }
    }

    public bool TrySetOverlayTextMode(
        OverlayTextMode mode,
        out string? reason,
        bool allowModeUpdateWithoutData = false)
    {
        reason = null;
        if (!_gate.Wait(0))
        {
            reason = "F11: toggle ignored (OCR/translation running).";
            return false;
        }

        try
        {
            if (_lastReadingUnits == null || _lastReadingUnits.Count == 0)
            {
                if (allowModeUpdateWithoutData)
                {
                    // WHY: F8/F10 runs should always use translated text once a new overlay is rendered.
                    _overlayTextMode = mode;
                    return true;
                }

                reason = "F11: toggle ignored (no overlay data).";
                return false;
            }

            var overlayItems = _overlayStage.BuildItems(
                _lastReadingUnits,
                _lastOverlayTranslations,
                _lastOverlayRoiScreen,
                _settingsService.Settings,
                mode);
            var suppressWpfOverlay = ShouldSuppressWpfOverlayForLastProvider(_settingsService.Settings);
            UpdateWpfOverlayRouting(overlayItems, _lastOverlayClipScreen, suppressWpfOverlay);
            if (suppressWpfOverlay)
            {
                if (!TryRepublishGraphicsHookOverlayForTextToggle(overlayItems, _settingsService.Settings, out reason))
                {
                    return false;
                }
            }

            _overlayTextMode = mode;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void UpdateHookRoiPreview(Rect? roiRectScreen)
    {
        var hasRect = roiRectScreen is { } rect && !rect.IsEmpty;
        if (_hookRoiTraceEnabled)
        {
            _logger.Info(
                $"stage=hook_roi_preview event=update hasRect={(hasRect ? 1 : 0)} " +
                $"provider={_lastCaptureProviderKind?.ToString() ?? "none"}.");
        }

        lock (_hookRoiPreviewSync)
        {
            _hookRoiPreviewRectScreen = roiRectScreen;
        }

        if (!_gate.Wait(0))
        {
            LogHookRoiSkipIfChanged("gate_busy");
            return;
        }

        try
        {
            var settings = _settingsService.Settings;
            if (!ShouldAllowHookRoiPreview(settings))
            {
                LogHookRoiSkipIfChanged("hook_preview_disabled");
                return;
            }
            _lastHookRoiSkipReason = null;

            // WHY: During ROI preview we must not republish cached OCR/translation blocks.
            // Otherwise hidden overlay text is resurrected while dragging F6 ROI.
            IReadOnlyList<OverlayItem> overlayItems = Array.Empty<OverlayItem>();
            if (_hookRoiTraceEnabled)
            {
                _logger.Info(
                    $"stage=hook_roi_preview event=republish_attempt hasRect={(hasRect ? 1 : 0)} overlayItems={overlayItems.Count}.");
            }
            _ = TryRepublishGraphicsHookOverlayFromCachedFrame(overlayItems, settings, "roi_preview_update", out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LogHookRoiSkipIfChanged(string reason)
    {
        if (!_hookRoiTraceEnabled)
        {
            return;
        }

        if (string.Equals(_lastHookRoiSkipReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        _lastHookRoiSkipReason = reason;
        _logger.Info($"stage=hook_roi_preview event=skip reason={reason}.");
    }

    private bool TryRepublishGraphicsHookOverlayForTextToggle(
        IReadOnlyList<OverlayItem> overlayItems,
        AppSettings settings,
        out string? reason)
    {
        return TryRepublishGraphicsHookOverlayFromCachedFrame(overlayItems, settings, "f11_toggle", out reason);
    }

    private bool TryRepublishGraphicsHookOverlayFromCachedFrame(
        IReadOnlyList<OverlayItem> overlayItems,
        AppSettings settings,
        string phase,
        out string? reason)
    {
        reason = null;
        if (!IsHookOverlaySupportedApi(settings.GraphicsHookApi))
        {
            reason = "Hook overlay is not available for the current graphics hook API.";
            return false;
        }

        var isRoiPreviewPhase = string.Equals(phase, "roi_preview_update", StringComparison.Ordinal);
        if (!isRoiPreviewPhase && _lastCaptureProviderKind != CaptureProviderKind.GraphicsHook)
        {
            reason = phase == "f11_toggle"
                ? "F11: toggle ignored (hook frame cache missing)."
                : null;
            _logger.Info(
                $"stage=hook_v2_write event=skip phase={phase} reason=no_cached_hook_frame " +
                $"provider={_lastCaptureProviderKind?.ToString() ?? "none"} canvas={_lastCaptureCanvasW}x{_lastCaptureCanvasH}.");
            return false;
        }

        var frameBounds = _lastCaptureFrameBounds ?? Rect.Empty;
        var canvasW = _lastCaptureCanvasW;
        var canvasH = _lastCaptureCanvasH;
        if (frameBounds.IsEmpty || canvasW == 0 || canvasH == 0)
        {
            if (isRoiPreviewPhase)
            {
                // WHY: ROI selection can start before first RunOnce() initializes frame cache.
                // Use current capture bounds as a bootstrap frame for hook-only ROI preview updates.
                var fallbackBounds = _captureManager.GetCaptureBounds(settings);
                if (!fallbackBounds.IsEmpty && fallbackBounds.Width > 0 && fallbackBounds.Height > 0)
                {
                    frameBounds = fallbackBounds;
                    canvasW = (uint)Math.Max(0, Math.Round(fallbackBounds.Width));
                    canvasH = (uint)Math.Max(0, Math.Round(fallbackBounds.Height));
                    _lastCaptureFrameBounds = frameBounds;
                    _lastCaptureCanvasW = canvasW;
                    _lastCaptureCanvasH = canvasH;
                }
            }
        }

        if (frameBounds.IsEmpty || canvasW == 0 || canvasH == 0)
        {
            reason = phase == "f11_toggle"
                ? "F11: toggle ignored (hook frame cache missing)."
                : null;
            _logger.Info(
                $"stage=hook_v2_write event=skip phase={phase} reason=no_cached_hook_frame " +
                $"provider={_lastCaptureProviderKind?.ToString() ?? "none"} canvas={canvasW}x{canvasH}.");
            return false;
        }

        // WHY: Reuse the existing hook v2 mapping/write pipeline for F11 mode toggles without running OCR again.
        // The bitmap content is not read in this path; only size and frame bounds are used for coordinate mapping.
        using var dummyFrame = new CaptureFrame(
            new Bitmap((int)canvasW, (int)canvasH),
            frameBounds,
            CaptureProviderKind.GraphicsHook,
            DateTimeOffset.UtcNow);
        TryUpdateGraphicsHookOverlayV2(dummyFrame, overlayItems, settings);
        return true;
    }

    public async Task RunWithReadingUnitsAsync(
        IReadOnlyList<ReadingUnit> readingUnits,
        Rect roiScreen,
        Rect? overlayClipScreen,
        CancellationToken cancellationToken,
        ForceRunOptions options)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settingsService.Settings;
            var context = new PipelineExecutionContext(settings, options, DateTimeOffset.UtcNow)
            {
                RoiScreen = roiScreen,
                OverlayClipScreen = overlayClipScreen,
                FinalStageResult = PipelineStageResult.ContinueExecution()
            };
            _logger.Info(
                $"Run context: trigger={options.Trigger}, suppress transient UI={options.SuppressTransientUiFeedback}, precomputed payload=true.");
            _ocrDiffService.IouThreshold = settings.OcrIouThreshold;

            if (readingUnits.Count == 0)
            {
                _logger.Info("Precomputed scene payload returned no reading units.");
                if (ApplyStopResult(
                        context,
                        PipelineStageResult.Stop(
                            PipelineStopReason.NoTextDetected,
                            PipelineOverlayAction.Clear,
                            "Precomputed scene payload returned no reading units.")))
                {
                    return;
                }
            }

            var groupedLines = readingUnits
                .Select(unit => new OcrLine(unit.Text, unit.Rect, 1.0f, Math.Max(1, unit.LineCount), unit.LineHeight))
                .ToList();
            var diffOutput = _diffStage.Execute(readingUnits, groupedLines, options.SkipOcrDiff);
            context.GroupedLines = groupedLines;
            context.ReadingUnits = readingUnits;
            context.ChangedUnitIds = diffOutput.ChangedUnitIds;
            context.IsDiffUnchanged = diffOutput.ChangedUnitIds.Count == 0;
            _logger.Info(
                $"Precomputed payload diff: {diffOutput.ChangedLines.Count} changed lines, {diffOutput.ChangedUnitIds.Count} changed units of {readingUnits.Count} total.");

            var translations = await _translateStage.ExecuteAsync(
                    readingUnits,
                    diffOutput.ChangedUnitIds,
                    settings,
                    options,
                    cancellationToken,
                    () => TranslationStarted?.Invoke(),
                    () => TranslationCompleted?.Invoke())
                .ConfigureAwait(false);
            foreach (var pair in translations)
            {
                context.Translations[pair.Key] = pair.Value;
            }

            var overlayItems = _overlayStage.BuildItems(readingUnits, translations, roiScreen, settings, _overlayTextMode);
            context.OverlayItems = overlayItems;
            CommitOverlayState(readingUnits, translations, roiScreen, overlayClipScreen);
            _overlayStage.Update(overlayItems, overlayClipScreen);
            context.FinalStageResult = PipelineStageResult.ContinueExecution();
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Pipeline canceled.");
            _overlayPresenter.ShowLast();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline failed.");
            _overlayPresenter.ShowLast();
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ApplyStopResult(
        PipelineExecutionContext context,
        PipelineStageResult stageResult,
        Rect? frameBounds = null,
        bool showNoTextToast = false,
        bool suppressWpfOverlay = false)
    {
        context.FinalStageResult = stageResult;
        if (stageResult.Continue)
        {
            return false;
        }

        _logger.Info(
            $"Pipeline stop: reason={stageResult.StopReason}, overlayAction={stageResult.OverlayAction}, message={stageResult.Message ?? "-"}.");
        if (suppressWpfOverlay)
        {
            // WHY: When hook overlay is authoritative, avoid reviving stale WPF text via ShowLast on stop paths.
            _overlayPresenter.ClearOverlay();
        }
        else
        {
            switch (stageResult.OverlayAction)
            {
                case PipelineOverlayAction.ShowLast:
                    _overlayPresenter.ShowLast();
                    break;
                case PipelineOverlayAction.Clear:
                    _overlayPresenter.ClearOverlay();
                    break;
                case PipelineOverlayAction.Update:
                case PipelineOverlayAction.None:
                default:
                    break;
            }
        }

        if (showNoTextToast && stageResult.StopReason == PipelineStopReason.NoTextDetected && frameBounds.HasValue)
        {
            if (suppressWpfOverlay)
            {
                _logger.Info("No text detected (toast suppressed: hook-only overlay route).");
            }
            else if (!context.Options.SuppressTransientUiFeedback)
            {
                _overlayPresenter.ShowToast("No text detected", frameBounds.Value);
            }
            else
            {
                _logger.Info("No text detected (toast suppressed).");
            }
        }

        return true;
    }

    private bool ShouldSuppressWpfOverlay(AppSettings settings, CaptureProviderKind providerKind)
    {
        if (_graphicsHookClientService == null)
        {
            return false;
        }

        if (!settings.EnableGraphicsHookPipeline || !settings.GraphicsHookOverlayEnabled)
        {
            return false;
        }

        if (!IsHookOverlaySupportedApi(settings.GraphicsHookApi))
        {
            return false;
        }

        if (!settings.EnableFixedCaptureWindow || settings.FixedCaptureWindowProcessId <= 0)
        {
            return false;
        }

        return providerKind == CaptureProviderKind.GraphicsHook;
    }

    private bool ShouldAllowHookRoiPreview(AppSettings settings)
    {
        if (_graphicsHookClientService == null)
        {
            return false;
        }

        if (!settings.EnableGraphicsHookPipeline || !settings.GraphicsHookOverlayEnabled)
        {
            return false;
        }

        if (!IsHookOverlaySupportedApi(settings.GraphicsHookApi))
        {
            return false;
        }

        if (!settings.EnableFixedCaptureWindow || settings.FixedCaptureWindowProcessId <= 0)
        {
            return false;
        }

        return true;
    }

    private bool ShouldSuppressWpfOverlayForLastProvider(AppSettings settings)
    {
        return _lastCaptureProviderKind is { } providerKind && ShouldSuppressWpfOverlay(settings, providerKind);
    }

    private void UpdateWpfOverlayRouting(
        IReadOnlyList<OverlayItem> overlayItems,
        Rect? overlayClipScreen,
        bool suppressWpfOverlay)
    {
        if (suppressWpfOverlay)
        {
            _overlayPresenter.ClearOverlay();
            return;
        }

        _overlayStage.Update(overlayItems, overlayClipScreen);
    }

    private void LogOverlayRouteIfChanged(bool suppressWpfOverlay, CaptureProviderKind providerKind)
    {
        if (_lastWpfOverlaySuppressed.HasValue && _lastWpfOverlaySuppressed.Value == suppressWpfOverlay)
        {
            return;
        }

        _lastWpfOverlaySuppressed = suppressWpfOverlay;
        var mode = suppressWpfOverlay ? "hook_only" : "wpf_allowed";
        _logger.Info($"stage=overlay_route event=mode_changed mode={mode} provider={providerKind}.");
    }

    private void CommitOverlayState(
        IReadOnlyList<ReadingUnit> readingUnits,
        Dictionary<int, string> translations,
        Rect roiScreen,
        Rect? overlayClipScreen)
    {
        // WHY: Commit overlay-related _last* state in a single point to keep update order deterministic.
        _lastReadingUnits = readingUnits.ToList();
        _lastOverlayTranslations = new Dictionary<int, string>(translations);
        _lastOverlayRoiScreen = roiScreen;
        _lastOverlayClipScreen = overlayClipScreen;
    }

    private static Rect? ResolveOverlayClipScreenRect(AppSettings settings, Rect captureBounds)
    {
        if (settings.CaptureMode != CaptureMode.ActiveWindow)
        {
            return null;
        }

        if (captureBounds.IsEmpty || captureBounds.Width <= 0 || captureBounds.Height <= 0)
        {
            return null;
        }

        return captureBounds;
    }

    private Rect GetRoiBounds(AppSettings settings, Rect frameBounds)
    {
        if (!settings.EnableRoi)
        {
            return frameBounds;
        }

        if (settings.NormalizedRoi is { } normalized && !normalized.IsEmpty)
        {
            return normalized.ToAbsolute(frameBounds);
        }

        if (settings.Roi is null || settings.Roi.Value.IsEmpty)
        {
            return frameBounds;
        }

        var absolute = Rect.Intersect(frameBounds, settings.Roi.Value.ToRect());
        if (!absolute.IsEmpty)
        {
            settings.NormalizedRoi = NormalizedRect.FromAbsolute(absolute, frameBounds);
            // NOTE: Fire-and-forget migration to normalized ROI for DPI-safe persistence.
            _ = _settingsService.SaveAsync();
        }

        return absolute;
    }

    private void RememberPreferredProvider(AppSettings settings, CaptureProviderKind providerKind)
    {
        if (providerKind == CaptureProviderKind.GraphicsHook)
        {
            // WHY: Hook capture is controlled by a dedicated feature flag and may be transient (fallback-heavy).
            // Persisting it as "preferred" would confuse the legacy provider UI and saved defaults.
            return;
        }

        if (settings.EnableGraphicsHookPipeline &&
            settings.CaptureMode == CaptureMode.ActiveWindow &&
            settings.EnableFixedCaptureWindow)
        {
            // WHY: While hook pipeline is active, transient fallback captures (e.g. WGC during hook writer race)
            // must not rewrite the preferred provider, or subsequent runs drift away from hook-first behavior.
            return;
        }

        if (settings.PreferredCaptureProvider == providerKind)
        {
            return;
        }

        settings.PreferredCaptureProvider = providerKind;
        _logger.Info($"Preferred capture provider set to {providerKind}.");
        _ = _settingsService.SaveAsync();
    }

    private void NotifyOcrPreprocessPreview(Bitmap ocrInput)
    {
        if (OcrPreprocessPreviewReady == null)
        {
            return;
        }

        // WHY: Use a clone so OCR can continue using the original bitmap safely.
        using var preview = (Bitmap)ocrInput.Clone();
        OcrPreprocessPreviewReady.Invoke(preview);
    }

    private void UpdateLastRoiSnapshot(Bitmap snapshot)
    {
        _lastRoiSnapshot?.Dispose();
        _lastRoiSnapshot = snapshot;
    }

    private void TryUpdateGraphicsHookOverlayV2(
        CaptureFrame frame,
        IReadOnlyList<OverlayItem> overlayItems,
        AppSettings settings)
    {
        if (_graphicsHookClientService == null)
        {
            return;
        }

        if (!IsHookOverlaySupportedApi(settings.GraphicsHookApi))
        {
            return;
        }

        var pid = settings.FixedCaptureWindowProcessId;
        if (pid <= 0)
        {
            return;
        }

        var canvasW = (uint)frame.Bitmap.Width;
        var canvasH = (uint)frame.Bitmap.Height;
        if (canvasW == 0 || canvasH == 0)
        {
            return;
        }

        Rect? roiPreviewScreen;
        lock (_hookRoiPreviewSync)
        {
            roiPreviewScreen = _hookRoiPreviewRectScreen;
        }
        var hasRoiPreview = roiPreviewScreen is { } previewRect && !previewRect.IsEmpty;

        var traceEnabled = _overlayV2TraceEnabled || _overlayV2WriteDebugEnabled;

        if (!settings.GraphicsHookOverlayEnabled || (overlayItems.Count == 0 && !hasRoiPreview))
        {
            var attemptSeq = NextOverlayV2WriteAttempt();
            var wrote = _graphicsHookClientService.TryWriteOverlayV2(
                pid,
                canvasW,
                canvasH,
                ReadOnlySpan<GraphicsHookOverlayV2CommandWriter.TextBlockV2>.Empty,
                Array.Empty<byte>(),
                0,
                out var failureReason);
            LogOverlayV2WriteResult(
                wrote,
                attemptSeq,
                "clear_disabled_or_empty",
                pid,
                canvasW,
                canvasH,
                blockCount: 0,
                textBytes: 0,
                failureReason);
            LogHookV2StatusSnapshot(pid, attemptSeq, "clear_disabled_or_empty", canvasW, canvasH);
            return;
        }

        // Keep this conservative until we introduce a settings surface (font/alpha/max bytes).
        const uint fgArgb = 0xFFFFFFFF;
        const uint bgArgb = 0xAA0A0A0A;
        const float paddingPx = 3.0f;
        const float roundingPx = 6.0f;
        const uint roiPreviewFgArgb = 0xFF3CF05A;
        const float roiPreviewStrokePx = 2.0f;
        const float roiPreviewRoundingPx = 0.0f;
        const int maxBlocks = 64;
        const int maxTextBytes = 64 * 1024;
        const int traceSampleLimit = 4;
        if (traceEnabled)
        {
            _logger.Info(
                $"stage=hook_v2_map event=start pid={pid} canvas={canvasW}x{canvasH} frameBounds={FormatRect(frame.Bounds)} " +
                $"items={overlayItems.Count}.");
        }

        var reserveRoiSlot = hasRoiPreview ? 1 : 0;
        var maxTextBlocks = Math.Max(0, maxBlocks - reserveRoiSlot);
        var blocks = new List<GraphicsHookOverlayV2CommandWriter.TextBlockV2>(Math.Min(overlayItems.Count + reserveRoiSlot, maxBlocks));
        var textBlob = new List<byte>(Math.Min(maxTextBytes, 4096));
        var skippedWhitespace = 0;
        var skippedMap = 0;
        var skippedUtf8 = 0;
        var skippedRoiPreview = 0;
        var mapSampled = 0;
        foreach (var item in overlayItems)
        {
            if (blocks.Count >= maxTextBlocks)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(item.Text))
            {
                skippedWhitespace++;
                continue;
            }

            if (!TryBuildHookCanvasRect(
                    item.Rect,
                    frame.Bounds,
                    (int)canvasW,
                    (int)canvasH,
                    out var x,
                    out var y,
                    out var w,
                    out var h,
                    out var mapFailureReason))
            {
                skippedMap++;
                if (traceEnabled && mapSampled < traceSampleLimit)
                {
                    _logger.Info(
                        $"stage=hook_v2_map event=skip seq={_overlayV2WriteAttemptSeq + 1} reason={mapFailureReason} " +
                        $"src={FormatRect(item.Rect)} frameBounds={FormatRect(frame.Bounds)} canvas={canvasW}x{canvasH}.");
                    mapSampled++;
                }
                continue;
            }

            var remaining = maxTextBytes - textBlob.Count;
            if (remaining <= 0)
            {
                break;
            }

            var utf8 = Encoding.UTF8.GetBytes(item.Text);
            var textLen = TrimUtf8Length(utf8, Math.Min(utf8.Length, remaining));
            if (textLen <= 0)
            {
                skippedUtf8++;
                continue;
            }

            var textOffset = textBlob.Count;
            for (var i = 0; i < textLen; i++)
            {
                textBlob.Add(utf8[i]);
            }

            var fontPx = ResolveHookOverlayFontPx(item, frame.Bounds, canvasH);
            if (traceEnabled && mapSampled < traceSampleLimit)
            {
                _logger.Info(
                    $"stage=hook_v2_map event=ok seq={_overlayV2WriteAttemptSeq + 1} src={FormatRect(item.Rect)} " +
                    $"dst=[{x:0.##},{y:0.##},{w:0.##},{h:0.##}] fontPx={fontPx:0.##} textLen={textLen}.");
                mapSampled++;
            }

            blocks.Add(new GraphicsHookOverlayV2CommandWriter.TextBlockV2
            {
                X = x,
                Y = y,
                W = w,
                H = h,
                PaddingPx = paddingPx,
                RoundingPx = roundingPx,
                FontPx = fontPx,
                FgArgb = fgArgb,
                BgArgb = bgArgb,
                Wrap = 1,
                TextOffset = unchecked((uint)textOffset),
                TextLen = unchecked((uint)textLen),
                ZOrder = blocks.Count
            });
        }

        if (traceEnabled)
        {
            _logger.Info(
                $"stage=hook_v2_map event=summary seq={_overlayV2WriteAttemptSeq + 1} pid={pid} canvas={canvasW}x{canvasH} " +
                $"inputItems={overlayItems.Count} validBlocks={blocks.Count} skipWhitespace={skippedWhitespace} " +
                $"skipMap={skippedMap} skipUtf8={skippedUtf8} skipRoiPreview={skippedRoiPreview} textBytes={textBlob.Count}.");
        }

        if (hasRoiPreview && roiPreviewScreen is { } roiRect)
        {
            if (TryBuildHookCanvasRect(
                    roiRect,
                    frame.Bounds,
                    (int)canvasW,
                    (int)canvasH,
                    out var x,
                    out var y,
                    out var w,
                    out var h,
                    out _))
            {
                blocks.Add(new GraphicsHookOverlayV2CommandWriter.TextBlockV2
                {
                    X = x,
                    Y = y,
                    W = w,
                    H = h,
                    PaddingPx = roiPreviewStrokePx,
                    RoundingPx = roiPreviewRoundingPx,
                    FontPx = 0,
                    FgArgb = roiPreviewFgArgb,
                    BgArgb = 0,
                    // NOTE: Wrap=2 is reserved for "ROI preview border" in HookAgentDx11.
                    Wrap = 2,
                    TextOffset = 0,
                    TextLen = 0,
                    ZOrder = int.MaxValue
                });
            }
            else
            {
                skippedRoiPreview = 1;
            }
        }

        if (blocks.Count == 0)
        {
            var attemptSeq = NextOverlayV2WriteAttempt();
            var wrote = _graphicsHookClientService.TryWriteOverlayV2(
                pid,
                canvasW,
                canvasH,
                ReadOnlySpan<GraphicsHookOverlayV2CommandWriter.TextBlockV2>.Empty,
                Array.Empty<byte>(),
                0,
                out var failureReason);
            LogOverlayV2WriteResult(
                wrote,
                attemptSeq,
                "clear_no_valid_blocks",
                pid,
                canvasW,
                canvasH,
                blockCount: 0,
                textBytes: 0,
                failureReason);
            LogHookV2StatusSnapshot(pid, attemptSeq, "clear_no_valid_blocks", canvasW, canvasH);
            return;
        }

        var publishAttempt = NextOverlayV2WriteAttempt();
        var blobArray = textBlob.ToArray();
        var publishOk = _graphicsHookClientService.TryWriteOverlayV2(
            pid,
            canvasW,
            canvasH,
            blocks.ToArray(),
            blobArray,
            blobArray.Length,
            out var publishFailure);
        LogOverlayV2WriteResult(
            publishOk,
            publishAttempt,
            "publish_text_blocks",
            pid,
            canvasW,
            canvasH,
            blockCount: blocks.Count,
            textBytes: blobArray.Length,
            publishFailure);
        LogHookV2StatusSnapshot(pid, publishAttempt, "publish_text_blocks", canvasW, canvasH);
    }

    private float ResolveHookOverlayFontPx(OverlayItem item, Rect frameBounds, uint canvasH)
    {
        const float fallbackFontPx = 24.0f;
        if (_overlayPresenter.TryResolveHookFontPx(item, frameBounds, canvasH, out var fittedFontPx) &&
            fittedFontPx > 0)
        {
            return fittedFontPx;
        }

        // WHY: Keep v2 readable even when UI thread metrics are temporarily unavailable.
        return fallbackFontPx;
    }

    private ulong NextOverlayV2WriteAttempt()
    {
        _overlayV2WriteAttemptSeq++;
        return _overlayV2WriteAttemptSeq;
    }

    private void LogOverlayV2WriteResult(
        bool success,
        ulong attemptSeq,
        string phase,
        int pid,
        uint canvasW,
        uint canvasH,
        int blockCount,
        int textBytes,
        string? failureReason,
        string? extra = null)
    {
        if (success && !_overlayV2WriteDebugEnabled)
        {
            return;
        }

        var result = success ? "ok" : "failed";
        var reason = success ? "none" : (failureReason ?? "unknown");
        var suffix = string.IsNullOrWhiteSpace(extra) ? string.Empty : $" {extra}";
        _logger.Info(
            $"stage=hook_v2_write event={result} seq={attemptSeq} phase={phase} pid={pid} canvas={canvasW}x{canvasH} " +
            $"blocks={blockCount} textBytes={textBytes} reason={reason}.{suffix}");
    }

    private static int TrimUtf8Length(byte[] bytes, int maxBytes)
    {
        if (bytes.Length <= maxBytes)
        {
            return bytes.Length;
        }

        var len = Math.Max(0, maxBytes);
        // WHY: Don't split a UTF-8 multi-byte sequence when truncating to mapping size.
        while (len > 0 && (bytes[len - 1] & 0b1100_0000) == 0b1000_0000)
        {
            len--;
        }

        return len;
    }

    private static bool TryBuildHookCanvasRect(
        Rect screenRect,
        Rect frameBounds,
        int pixelW,
        int pixelH,
        out float x,
        out float y,
        out float w,
        out float h,
        out string? failureReason)
    {
        x = y = w = h = 0;
        failureReason = null;
        if (screenRect.IsEmpty || screenRect.Width <= 0 || screenRect.Height <= 0)
        {
            failureReason = "invalid_source_rect";
            return false;
        }

        if (frameBounds.IsEmpty || frameBounds.Width <= 0 || frameBounds.Height <= 0)
        {
            failureReason = "invalid_frame_bounds";
            return false;
        }

        var scaleX = frameBounds.Width > 0 ? pixelW / frameBounds.Width : 1.0;
        var scaleY = frameBounds.Height > 0 ? pixelH / frameBounds.Height : 1.0;
        if (scaleX <= 0 || scaleY <= 0)
        {
            scaleX = 1.0;
            scaleY = 1.0;
        }

        var left = (screenRect.X - frameBounds.X) * scaleX;
        var top = (screenRect.Y - frameBounds.Y) * scaleY;
        var right = (screenRect.X - frameBounds.X + screenRect.Width) * scaleX;
        var bottom = (screenRect.Y - frameBounds.Y + screenRect.Height) * scaleY;

        left = Math.Max(0, Math.Min(pixelW, left));
        top = Math.Max(0, Math.Min(pixelH, top));
        right = Math.Max(0, Math.Min(pixelW, right));
        bottom = Math.Max(0, Math.Min(pixelH, bottom));

        var outW = right - left;
        var outH = bottom - top;
        if (outW <= 1 || outH <= 1)
        {
            failureReason = "collapsed_after_clamp";
            return false;
        }

        x = (float)left;
        y = (float)top;
        w = (float)outW;
        h = (float)outH;
        return true;
    }

    private void LogHookV2StatusSnapshot(int pid, ulong seq, string phase, uint canvasW, uint canvasH)
    {
        if (!_hookV2StatusTraceEnabled)
        {
            return;
        }

        if (pid <= 0)
        {
            return;
        }

        if (GraphicsHookStatusReader.TryReadAny(pid, out var status, out var statusApi))
        {
            var mismatch =
                status.BackBufferWidth > 0 &&
                status.BackBufferHeight > 0 &&
                (Math.Abs((int)status.BackBufferWidth - (int)canvasW) > 2 ||
                 Math.Abs((int)status.BackBufferHeight - (int)canvasH) > 2);
            _logger.Info(
                $"stage=hook_v2_status event=read seq={seq} phase={phase} pid={pid} presentCount={status.PresentCount} " +
                $"api={statusApi} presentKind={status.LastPresentKind} bb={status.BackBufferWidth}x{status.BackBufferHeight} " +
                $"canvas={canvasW}x{canvasH} mismatch={(mismatch ? "yes" : "no")} " +
                $"cmdQpc={status.LastCmdQpc} cmdCount={status.LastCmdCount} r0={status.Reserved0} r1={status.Reserved1}.");
            return;
        }

        _logger.Info($"stage=hook_v2_status event=missing seq={seq} phase={phase} pid={pid}.");
    }

    private static bool IsHookOverlaySupportedApi(GraphicsHookApiKind api)
    {
        return api is GraphicsHookApiKind.Dx9 or GraphicsHookApiKind.Dx11 or GraphicsHookApiKind.Vulkan;
    }

    private static string FormatRect(Rect rect)
    {
        return $"[{rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##},{rect.Height:0.##}]";
    }

}



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
    private readonly Dx11HookClientService? _dx11HookClientService;
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OverlayTextMode _overlayTextMode = OverlayTextMode.Translated;
    private IReadOnlyList<ReadingUnit>? _lastReadingUnits;
    private Dictionary<int, string> _lastOverlayTranslations = new();
    private Rect _lastOverlayRoiScreen;
    private Rect? _lastOverlayClipScreen;
    private ulong? _lastHash;
    private Bitmap? _lastRoiSnapshot;
    private ulong _overlayV2WriteAttemptSeq;

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
        Dx11HookClientService? dx11HookClientService,
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
        _dx11HookClientService = dx11HookClientService;
        _ocrDiffService = ocrDiffService;
        _phashService = phashService;
        _logger = logger;
        var preprocessCoordinator = new OcrPreprocessCoordinator(ocrEngine, ocrPreprocessService, new OcrCandidateScorer(), _logger);
        _ocrAndGroupStage = new OcrAndGroupStage(preprocessCoordinator, lineGrouper, new ReadingUnitBuilder());
        _diffStage = new DiffStage(_ocrDiffService);
        _translateStage = new TranslateStage(normalizationService, cacheRepository, cacheKeyBuilder, translationService, _logger);
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

            if (frame.IsBlack)
            {
                _logger.Info($"Black frame detected from {frame.ProviderKind}. Keeping last overlay.");
                if (ApplyStopResult(
                        context,
                        PipelineStageResult.Stop(
                            PipelineStopReason.BlackFrame,
                            PipelineOverlayAction.ShowLast,
                            "Black frame detected."),
                        frame.Bounds))
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
                        frame.Bounds))
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
                            frame.Bounds))
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
                            showNoTextToast: true))
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
                            showNoTextToast: true))
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
                _overlayStage.Update(overlayItems, overlayClipScreen);
                TryUpdateDx11HookOverlay(frame, roiScreen, overlayItems, settings);
                TryUpdateDx11HookOverlayV2(frame, overlayItems, settings);
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
            _overlayPresenter.ShowLast();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline failed.");
            _overlayPresenter.ShowLast();
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

            _overlayTextMode = mode;
            var overlayItems = _overlayStage.BuildItems(
                _lastReadingUnits,
                _lastOverlayTranslations,
                _lastOverlayRoiScreen,
                _settingsService.Settings,
                _overlayTextMode);
            _overlayStage.Update(overlayItems, _lastOverlayClipScreen);
            return true;
        }
        finally
        {
            _gate.Release();
        }
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
        bool showNoTextToast = false)
    {
        context.FinalStageResult = stageResult;
        if (stageResult.Continue)
        {
            return false;
        }

        _logger.Info(
            $"Pipeline stop: reason={stageResult.StopReason}, overlayAction={stageResult.OverlayAction}, message={stageResult.Message ?? "-"}.");
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

        if (showNoTextToast && stageResult.StopReason == PipelineStopReason.NoTextDetected && frameBounds.HasValue)
        {
            if (!context.Options.SuppressTransientUiFeedback)
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

    private void TryUpdateDx11HookOverlay(
        CaptureFrame frame,
        Rect roiScreen,
        IReadOnlyList<OverlayItem> overlayItems,
        AppSettings settings)
    {
        if (_dx11HookClientService == null)
        {
            return;
        }

        var pid = settings.FixedCaptureWindowProcessId;
        if (pid <= 0)
        {
            return;
        }

        if (!settings.Dx11HookOverlayEnabled)
        {
            _dx11HookClientService.TrySendOverlayUpdate(pid, Array.Empty<Dx11HookOverlayRect>());
            return;
        }

        var pixelW = frame.Bitmap.Width;
        var pixelH = frame.Bitmap.Height;
        if (pixelW <= 0 || pixelH <= 0)
        {
            return;
        }

        // WHY: Keep v1 simple: draw ROI + each overlay item bounding box. This is primarily a visibility/debug tool.
        const uint roiColor = 0xFF2C8CFF;
        const uint itemColor = 0xFFFFC400;
        const uint thickness = 3;

        var rects = new List<Dx11HookOverlayRect>(Math.Min(overlayItems.Count + 1, 128));
        if (TryBuildHookRect(roiScreen, frame.Bounds, pixelW, pixelH, roiColor, thickness, out var roiRect))
        {
            rects.Add(roiRect);
        }

        foreach (var item in overlayItems)
        {
            if (rects.Count >= 128)
            {
                break;
            }

            if (TryBuildHookRect(item.Rect, frame.Bounds, pixelW, pixelH, itemColor, thickness, out var itemRect))
            {
                rects.Add(itemRect);
            }
        }

        _dx11HookClientService.TrySendOverlayUpdate(pid, rects);
    }

    private void TryUpdateDx11HookOverlayV2(
        CaptureFrame frame,
        IReadOnlyList<OverlayItem> overlayItems,
        AppSettings settings)
    {
        if (_dx11HookClientService == null)
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

        if (!settings.Dx11HookOverlayEnabled || overlayItems.Count == 0)
        {
            var attemptSeq = NextOverlayV2WriteAttempt();
            var wrote = _dx11HookClientService.TryWriteOverlayV2(
                pid,
                canvasW,
                canvasH,
                ReadOnlySpan<Dx11HookOverlayV2CommandWriter.TextBlockV2>.Empty,
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
            return;
        }

        // Step 3 (v2): Render only one text block first for stability.
        var item = overlayItems[0];
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            var attemptSeq = NextOverlayV2WriteAttempt();
            var wrote = _dx11HookClientService.TryWriteOverlayV2(
                pid,
                canvasW,
                canvasH,
                ReadOnlySpan<Dx11HookOverlayV2CommandWriter.TextBlockV2>.Empty,
                Array.Empty<byte>(),
                0,
                out var failureReason);
            LogOverlayV2WriteResult(
                wrote,
                attemptSeq,
                "clear_blank_text",
                pid,
                canvasW,
                canvasH,
                blockCount: 0,
                textBytes: 0,
                failureReason);
            return;
        }

        if (!TryBuildHookCanvasRect(item.Rect, frame.Bounds, (int)canvasW, (int)canvasH, out var x, out var y, out var w, out var h))
        {
            return;
        }

        // Keep this conservative until we introduce a settings surface (font/alpha/max bytes).
        const uint fgArgb = 0xFFFFFFFF;
        const uint bgArgb = 0xAA0A0A0A;
        const float paddingPx = 14.0f;
        const float roundingPx = 12.0f;
        var fontPx = ResolveHookOverlayFontPx(item, frame.Bounds, canvasH);

        // PERF: Allocate once per update; v2 is "latest only" and typically runs at <= OCR/translation rate.
        var utf8 = Encoding.UTF8.GetBytes(item.Text);
        var maxBytes = Math.Min(utf8.Length, 64 * 1024);
        var textLen = TrimUtf8Length(utf8, maxBytes);

        var block = new Dx11HookOverlayV2CommandWriter.TextBlockV2
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
            TextOffset = 0,
            TextLen = unchecked((uint)textLen),
            ZOrder = 0
        };

        var publishAttempt = NextOverlayV2WriteAttempt();
        var publishOk = _dx11HookClientService.TryWriteOverlayV2(
            pid,
            canvasW,
            canvasH,
            new[] { block },
            utf8,
            textLen,
            out var publishFailure);
        LogOverlayV2WriteResult(
            publishOk,
            publishAttempt,
            "publish_text_block",
            pid,
            canvasW,
            canvasH,
            blockCount: 1,
            textBytes: textLen,
            publishFailure,
            extra: $"fontPx={fontPx:0.0}");
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
        out float h)
    {
        x = y = w = h = 0;
        if (screenRect.IsEmpty || screenRect.Width <= 0 || screenRect.Height <= 0)
        {
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
            return false;
        }

        x = (float)left;
        y = (float)top;
        w = (float)outW;
        h = (float)outH;
        return true;
    }

    private static bool TryBuildHookRect(
        Rect screenRect,
        Rect frameBounds,
        int pixelW,
        int pixelH,
        uint argb,
        uint thickness,
        out Dx11HookOverlayRect rect)
    {
        rect = default;
        if (screenRect.IsEmpty || screenRect.Width <= 0 || screenRect.Height <= 0)
        {
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

        var w = right - left;
        var h = bottom - top;
        if (w <= 1 || h <= 1)
        {
            return false;
        }

        rect = new Dx11HookOverlayRect((float)left, (float)top, (float)w, (float)h, argb, thickness);
        return true;
    }
}

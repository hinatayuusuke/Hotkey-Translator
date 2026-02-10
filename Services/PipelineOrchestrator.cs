using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public readonly record struct ForceRunOptions(
    bool SkipPhash,
    bool SkipOcrDiff,
    bool SkipTranslationCache,
    bool SkipTranslation,
    bool ForceGeminiStrict = false)
{
    public static ForceRunOptions None => new(false, false, false, false);

    public bool IsEnabled => SkipPhash || SkipOcrDiff || SkipTranslationCache || SkipTranslation || ForceGeminiStrict;
}

public sealed class PipelineOrchestrator
{
    private readonly CaptureManager _captureManager;
    private readonly OcrEngine _ocrEngine;
    private readonly OcrDiffService _ocrDiffService;
    private readonly PhashService _phashService;
    private readonly NormalizationService _normalizationService;
    private readonly OcrPreprocessService _ocrPreprocessService;
    private readonly OcrPreprocessCoordinator _ocrPreprocessCoordinator;
    private readonly OcrLineGrouper _lineGrouper;
    private readonly ReadingUnitBuilder _readingUnitBuilder;
    private readonly CacheRepository _cacheRepository;
    private readonly CacheKeyBuilder _cacheKeyBuilder;
    private readonly TranslationFallbackService _translationService;
    private readonly OverlayPresenter _overlayPresenter;
    private readonly SettingsService _settingsService;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _lastTranslations = new(StringComparer.Ordinal);
    private IReadOnlyList<OverlayItem> _lastOverlayItems = Array.Empty<OverlayItem>();
    private OverlayTextMode _overlayTextMode = OverlayTextMode.Translated;
    private IReadOnlyList<ReadingUnit>? _lastReadingUnits;
    private Dictionary<int, string> _lastOverlayTranslations = new();
    private Rect _lastOverlayRoiScreen;
    private ulong? _lastHash;
    private Bitmap? _lastRoiSnapshot;
    private Rect? _lastRoiBounds;

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

    public PipelineOrchestrator(
        CaptureManager captureManager,
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
        _ocrEngine = ocrEngine;
        _ocrDiffService = ocrDiffService;
        _phashService = phashService;
        _normalizationService = normalizationService;
        _ocrPreprocessService = ocrPreprocessService;
        _ocrPreprocessCoordinator = new OcrPreprocessCoordinator(_ocrEngine, _ocrPreprocessService, new OcrCandidateScorer(), _logger);
        _lineGrouper = lineGrouper;
        _readingUnitBuilder = new ReadingUnitBuilder();
        _cacheRepository = cacheRepository;
        _cacheKeyBuilder = cacheKeyBuilder;
        _translationService = translationService;
        _overlayPresenter = overlayPresenter;
        _settingsService = settingsService;
        _logger = logger;
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
        Rect roiSnapshotBounds = default;
        var perfActive = false;
        var queueWaitMs = 0L;
        long captureMs = 0;
        long cropMs = 0;
        long ocrMs = 0;
        long groupMs = 0;
        long diffMs = 0;
        long overlayMs = 0;
        Stopwatch? totalStopwatch = null;
        int perfThresholdMs = 0;
        try
        {
            var settings = _settingsService.Settings;
            var perfEnabled = settings.EnableOcrPerfLog && settings.EnableLogging;
            perfThresholdMs = Math.Max(0, settings.OcrPerfLogThresholdMs);
            totalStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
            if (perfEnabled)
            {
                queueWaitMs = waitStopwatch.ElapsedMilliseconds;
            }
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

            Stopwatch? captureStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
            using var frame = _captureManager.Capture(settings);
            if (perfEnabled && captureStopwatch != null)
            {
                captureStopwatch.Stop();
                captureMs = captureStopwatch.ElapsedMilliseconds;
            }

            if (frame.IsBlack)
            {
                _logger.Info($"Black frame detected from {frame.ProviderKind}. Keeping last overlay.");
                _overlayPresenter.ShowLast();
                return;
            }

            RememberPreferredProvider(settings, frame.ProviderKind);
            var roiScreen = GetRoiBounds(settings, frame.Bounds);
            if (roiScreen.IsEmpty)
            {
                _logger.Info("ROI is outside capture bounds.");
                _overlayPresenter.ShowLast();
                return;
            }

            var roiInFrame = new Rect(
                roiScreen.X - frame.Bounds.X,
                roiScreen.Y - frame.Bounds.Y,
                roiScreen.Width,
                roiScreen.Height);

            Stopwatch? cropStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
            using var roiBitmap = BitmapHelper.Crop(frame.Bitmap, roiInFrame);
            if (perfEnabled && cropStopwatch != null)
            {
                cropStopwatch.Stop();
                cropMs = cropStopwatch.ElapsedMilliseconds;
            }
            roiSnapshot = (Bitmap)roiBitmap.Clone();
            roiSnapshotBounds = roiScreen;

            if (settings.PhashThreshold >= 0)
            {
                var hash = _phashService.ComputeHash(roiBitmap);
                if (!options.SkipPhash && _lastHash.HasValue && _phashService.IsSimilar(hash, _lastHash.Value, settings.PhashThreshold))
                {
                    _logger.Info("pHash unchanged; keeping last overlay.");
                    _overlayPresenter.ShowLast();
                    return;
                }

                _lastHash = hash;
            }
            else
            {
                _lastHash = null;
            }

            var ocrStopwatch = Stopwatch.StartNew();
            perfActive = perfEnabled;
            OcrResultModel ocrResult;
            Bitmap? ocrInput = null;
            try
            {
                var passResult = await _ocrPreprocessCoordinator.RunAsync(roiBitmap, settings, cancellationToken)
                    .ConfigureAwait(false);
                ocrResult = passResult.Result;
                ocrInput = passResult.Input;

                NotifyOcrPreprocessPreview(ocrInput);

                ocrStopwatch.Stop();
                if (perfEnabled)
                {
                    ocrMs = ocrStopwatch.ElapsedMilliseconds;
                }
                _logger.Info($"OCR completed: {ocrResult.Lines.Count} lines in {ocrStopwatch.ElapsedMilliseconds} ms.");
                var rawLines = ocrResult.Lines;
                if (settings.OcrEngine == OcrEngineKind.Paddle && settings.EnablePaddleConfidenceFilter)
                {
                    var threshold = Math.Clamp(settings.PaddleConfidenceThreshold, 0.0, 1.0);
                    var filtered = rawLines
                        .Where(line => line.Confidence >= threshold)
                        .ToList();
                    if (filtered.Count != rawLines.Count)
                    {
                        _logger.Info($"Paddle confidence filter: {filtered.Count}/{rawLines.Count} lines kept (threshold={threshold:0.00}).");
                    }

                    rawLines = filtered;
                }

                if (rawLines.Count == 0)
                {
                    _logger.Info("OCR returned no lines after confidence filtering.");
                    _overlayPresenter.ClearOverlay();
                    _overlayPresenter.ShowToast("No text detected", frame.Bounds);
                    return;
                }

                var mappedLines = rawLines
                    .Select(line => line with
                    {
                        Rect = new Rect(
                            line.Rect.X + roiScreen.X,
                            line.Rect.Y + roiScreen.Y,
                            line.Rect.Width,
                            line.Rect.Height)
                    })
                    .ToList();

                var groupStopwatch = Stopwatch.StartNew();
                var groupedLines = _lineGrouper.MergeLines(mappedLines, settings).ToList();
                var readingUnits = _readingUnitBuilder.Build(groupedLines, settings).ToList();
                groupStopwatch.Stop();
                if (perfEnabled)
                {
                    groupMs = groupStopwatch.ElapsedMilliseconds;
                }
                _logger.Info($"OCR grouped: {groupedLines.Count} lines, {readingUnits.Count} reading units in {groupStopwatch.ElapsedMilliseconds} ms.");
                if (readingUnits.Count == 0)
                {
                    _logger.Info("OCR returned no lines.");
                    _overlayPresenter.ClearOverlay();
                    _overlayPresenter.ShowToast("No text detected", frame.Bounds);
                    return;
                }

                Stopwatch? diffStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
                var changedLines = options.SkipOcrDiff ? groupedLines : _ocrDiffService.FilterChangedLines(groupedLines);
                var changedUnitIds = ResolveChangedUnitIds(readingUnits, groupedLines, changedLines, options.SkipOcrDiff);
                if (perfEnabled && diffStopwatch != null)
                {
                    diffStopwatch.Stop();
                    diffMs = diffStopwatch.ElapsedMilliseconds;
                }
                _logger.Info($"OCR diff: {changedLines.Count} changed lines, {changedUnitIds.Count} changed units of {readingUnits.Count} total.");
                Dictionary<int, string> translations;
                if (options.SkipTranslation)
                {
                    translations = new Dictionary<int, string>();
                }
                else
                {
                    translations = await ResolveTranslationsAsync(
                            readingUnits,
                            changedUnitIds,
                            settings,
                            options,
                            options.SkipTranslationCache,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                _lastReadingUnits = readingUnits.ToList();
                _lastOverlayTranslations = new Dictionary<int, string>(translations);
                _lastOverlayRoiScreen = roiScreen;
                var overlayItems = BuildOverlayItems(readingUnits, translations, roiScreen, settings, _overlayTextMode);
                _lastOverlayItems = overlayItems;
                Stopwatch? overlayStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
                _overlayPresenter.Update(overlayItems);
                if (perfEnabled && overlayStopwatch != null)
                {
                    overlayStopwatch.Stop();
                    overlayMs = overlayStopwatch.ElapsedMilliseconds;
                }
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
            if (totalStopwatch != null)
            {
                totalStopwatch.Stop();
                if (perfActive && totalStopwatch.ElapsedMilliseconds >= perfThresholdMs)
                {
                    _logger.Info($"[Perf] QueueWait={queueWaitMs}ms, Capture={captureMs}ms, Crop={cropMs}ms, OCR={ocrMs}ms, " +
                                 $"Group={groupMs}ms, Diff={diffMs}ms, Overlay={overlayMs}ms (total={totalStopwatch.ElapsedMilliseconds}ms).");
                }
            }

            if (roiSnapshot != null)
            {
                UpdateLastRoiSnapshot(roiSnapshot, roiSnapshotBounds);
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
            var overlayItems = BuildOverlayItems(
                _lastReadingUnits,
                _lastOverlayTranslations,
                _lastOverlayRoiScreen,
                _settingsService.Settings,
                _overlayTextMode);
            _lastOverlayItems = overlayItems;
            _overlayPresenter.Update(overlayItems);
            return true;
        }
        finally
        {
            _gate.Release();
        }
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
        if (settings.PreferredCaptureProvider == providerKind)
        {
            return;
        }

        settings.PreferredCaptureProvider = providerKind;
        _logger.Info($"Preferred capture provider set to {providerKind}.");
        _ = _settingsService.SaveAsync();
    }

    private async Task<Dictionary<int, string>> ResolveTranslationsAsync(
        IReadOnlyList<ReadingUnit> units,
        IReadOnlySet<int> changedUnitIds,
        AppSettings settings,
        ForceRunOptions options,
        bool skipTranslationCache,
        CancellationToken cancellationToken)
    {
        var translations = new Dictionary<int, string>();
        var pending = new List<PendingTranslation>();
        var pendingNormalized = new HashSet<string>(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            var normalized = _normalizationService.Normalize(unit.Text);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var key = _cacheKeyBuilder.Build(settings, normalized);
            if (!skipTranslationCache)
            {
                var cached = await _cacheRepository.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    translations[unit.Id] = cached;
                    _lastTranslations[normalized] = cached;
                    continue;
                }

                if (_lastTranslations.TryGetValue(normalized, out var last))
                {
                    translations[unit.Id] = last;
                    continue;
                }
            }

            if (changedUnitIds.Contains(unit.Id) && pendingNormalized.Add(normalized))
            {
                pending.Add(new PendingTranslation(unit.Id, unit.Text, normalized, key));
            }
        }

        if (pending.Count == 0)
        {
            _logger.Info($"Translation skipped: no pending items (changed {changedUnitIds.Count}, total {units.Count}).");
            return translations;
        }

        _logger.Info($"Translation pending: {pending.Count} items.");
        var pendingTexts = pending.Select(item => item.SourceText).ToList();
        TranslationStarted?.Invoke();
        IReadOnlyDictionary<string, string> results;
        try
        {
            results = await _translationService.TranslateAsync(pendingTexts, settings, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TranslationCompleted?.Invoke();
        }
        foreach (var item in pending)
        {
            if (!results.TryGetValue(item.SourceText, out var translated) || string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }

            await _cacheRepository.SaveAsync(item.CacheKey, translated, cancellationToken).ConfigureAwait(false);
            _lastTranslations[item.Normalized] = translated;
            translations[item.UnitId] = translated;
        }

        if (skipTranslationCache)
        {
            return translations;
        }

        foreach (var unit in units)
        {
            var normalized = _normalizationService.Normalize(unit.Text);
            if (_lastTranslations.TryGetValue(normalized, out var translated))
            {
                translations[unit.Id] = translated;
            }
        }

        return translations;
    }

    private static string GetOverlayText(
        ReadingUnit unit,
        Dictionary<int, string> translations,
        OverlayTextMode mode)
    {
        var text = mode == OverlayTextMode.Translated && translations.TryGetValue(unit.Id, out var translated)
            ? translated
            : unit.Text;
        return NormalizeOverlayText(text, unit.LineCount);
    }

    private static IReadOnlyList<OverlayItem> BuildOverlayItems(
        IReadOnlyList<ReadingUnit> readingUnits,
        Dictionary<int, string> translations,
        Rect roiScreen,
        AppSettings settings,
        OverlayTextMode mode)
    {
        if (!settings.EnableFixedRoiOverlay)
        {
            return readingUnits
                .Select(unit => new OverlayItem(GetOverlayText(unit, translations, mode), unit.Rect, unit.LineCount, unit.LineHeight))
                .ToList();
        }

        if (readingUnits.Count == 0)
        {
            return Array.Empty<OverlayItem>();
        }

        var lines = new List<string>(readingUnits.Count);
        foreach (var unit in readingUnits)
        {
            var text = GetOverlayText(unit, translations, mode);
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(text);
            }
        }

        var combined = lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        var lineCount = Math.Max(1, readingUnits.Sum(unit => Math.Max(1, unit.LineCount)));
        var lineHeights = readingUnits.Select(unit => unit.LineHeight).Where(height => height > 0).ToList();
        var lineHeight = lineHeights.Count > 0 ? lineHeights.Average() : 0;

        return new[] { new OverlayItem(combined, roiScreen, lineCount, lineHeight) };
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

    private void UpdateLastRoiSnapshot(Bitmap snapshot, Rect bounds)
    {
        _lastRoiSnapshot?.Dispose();
        _lastRoiSnapshot = snapshot;
        _lastRoiBounds = bounds;
    }

    private static string NormalizeOverlayText(string text, int lineCount)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        if (lineCount <= 1)
        {
            return string.Join(" ", lines);
        }

        if (lines.Count <= lineCount)
        {
            return text;
        }

        // WHY: Constrain translated line breaks to the OCR line count to reduce overflow.
        var head = lines.Take(lineCount - 1);
        var tail = string.Join(" ", lines.Skip(lineCount - 1));
        return string.Join(Environment.NewLine, head.Append(tail));
    }

    private static HashSet<int> ResolveChangedUnitIds(
        IReadOnlyList<ReadingUnit> units,
        IReadOnlyList<OcrLine> groupedLines,
        IReadOnlyList<OcrLine> changedLines,
        bool skipOcrDiff)
    {
        if (skipOcrDiff)
        {
            return units.Select(unit => unit.Id).ToHashSet();
        }

        if (changedLines.Count == 0)
        {
            return new HashSet<int>();
        }

        var changedLineSet = new HashSet<OcrLine>(changedLines);
        var changedUnitIds = new HashSet<int>();
        foreach (var unit in units)
        {
            foreach (var sourceIndex in unit.SourceIndices)
            {
                if (sourceIndex < 0 || sourceIndex >= groupedLines.Count)
                {
                    continue;
                }

                if (changedLineSet.Contains(groupedLines[sourceIndex]))
                {
                    changedUnitIds.Add(unit.Id);
                    break;
                }
            }
        }

        return changedUnitIds;
    }

    private sealed record PendingTranslation(int UnitId, string SourceText, string Normalized, string CacheKey);
}

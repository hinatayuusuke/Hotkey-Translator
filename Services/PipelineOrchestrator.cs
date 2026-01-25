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

public readonly record struct ForceRunOptions(bool SkipPhash, bool SkipOcrDiff, bool SkipTranslationCache, bool SkipTranslation)
{
    public static ForceRunOptions None => new(false, false, false, false);

    public bool IsEnabled => SkipPhash || SkipOcrDiff || SkipTranslationCache || SkipTranslation;
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
    private readonly SceneChangeEvaluator _sceneChangeEvaluator;
    private readonly OcrLineGrouper _lineGrouper;
    private readonly CacheRepository _cacheRepository;
    private readonly CacheKeyBuilder _cacheKeyBuilder;
    private readonly TranslationFallbackService _translationService;
    private readonly OverlayPresenter _overlayPresenter;
    private readonly SettingsService _settingsService;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _lastTranslations = new(StringComparer.Ordinal);
    private IReadOnlyList<OverlayItem> _lastOverlayItems = Array.Empty<OverlayItem>();
    private ulong? _lastHash;
    private Bitmap? _lastRoiSnapshot;
    private Rect? _lastRoiBounds;

    public event Action<Bitmap>? OcrPreprocessPreviewReady;
    public event Action<double>? OverlayAutoHidden;

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
        _sceneChangeEvaluator = new SceneChangeEvaluator(_phashService);
        _lineGrouper = lineGrouper;
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
                                 $"skip translation cache={options.SkipTranslationCache}, skip translation={options.SkipTranslation}.");
                }
            }

            _overlayPresenter.Hide();
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

            if (settings.EnableSceneChangeAutoHide)
            {
                var evaluation = _sceneChangeEvaluator.Evaluate(
                    roiBitmap,
                    roiScreen,
                    _lastRoiSnapshot,
                    _lastRoiBounds,
                    _lastOverlayItems,
                    settings);
                if (!evaluation.CanEvaluate)
                {
                    if (!string.IsNullOrWhiteSpace(evaluation.SkipReason))
                    {
                        _logger.Info($"Scene change skipped: {evaluation.SkipReason}");
                    }
                }
                else
                {
                    var threshold = Math.Clamp(settings.SceneChangeThreshold, 0.0, 1.0);
                    _logger.Info($"Scene change score: {evaluation.Score:0.00} (threshold={threshold:0.00}, text-weighted={settings.EnableSceneChangeTextWeighted}).");
                    if (evaluation.Score >= threshold)
                    {
                        _overlayPresenter.SetEnabled(false);
                        OverlayAutoHidden?.Invoke(evaluation.Score);
                        return;
                    }
                }
            }

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
                var mappedLines = ocrResult.Lines
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
                groupStopwatch.Stop();
                if (perfEnabled)
                {
                    groupMs = groupStopwatch.ElapsedMilliseconds;
                }
                _logger.Info($"OCR grouped: {groupedLines.Count} lines in {groupStopwatch.ElapsedMilliseconds} ms.");
                if (groupedLines.Count == 0)
                {
                    _logger.Info("OCR returned no lines.");
                    _overlayPresenter.ShowLast();
                    return;
                }

                Stopwatch? diffStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
                var changedLines = options.SkipOcrDiff ? groupedLines : _ocrDiffService.FilterChangedLines(groupedLines);
                if (perfEnabled && diffStopwatch != null)
                {
                    diffStopwatch.Stop();
                    diffMs = diffStopwatch.ElapsedMilliseconds;
                }
                _logger.Info($"OCR diff: {changedLines.Count} changed of {groupedLines.Count} total.");
                Dictionary<string, string> translations;
                if (options.SkipTranslation)
                {
                    translations = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                else
                {
                    translations = await ResolveTranslationsAsync(
                            groupedLines,
                            changedLines,
                            settings,
                            options.SkipTranslationCache,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var overlayItems = BuildOverlayItems(groupedLines, translations, roiScreen, settings);

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

            _overlayPresenter.Show();
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

    private async Task<Dictionary<string, string>> ResolveTranslationsAsync(
        IReadOnlyList<OcrLine> lines,
        IReadOnlyList<OcrLine> changedLines,
        AppSettings settings,
        bool skipTranslationCache,
        CancellationToken cancellationToken)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var changedSet = skipTranslationCache ? new HashSet<OcrLine>(lines) : new HashSet<OcrLine>(changedLines);
        var pending = new List<PendingTranslation>();
        var pendingNormalized = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var normalized = _normalizationService.Normalize(line.Text);
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
                    translations[line.Text] = cached;
                    _lastTranslations[normalized] = cached;
                    continue;
                }

                if (_lastTranslations.TryGetValue(normalized, out var last))
                {
                    translations[line.Text] = last;
                    continue;
                }
            }

            if (changedSet.Contains(line) && pendingNormalized.Add(normalized))
            {
                pending.Add(new PendingTranslation(line.Text, normalized, key));
            }
        }

        if (pending.Count == 0)
        {
            _logger.Info($"Translation skipped: no pending items (changed {changedLines.Count}, total {lines.Count}).");
            return translations;
        }

        _logger.Info($"Translation pending: {pending.Count} items.");
        var pendingTexts = pending.Select(item => item.SourceText).ToList();
        var results = await _translationService.TranslateAsync(pendingTexts, settings, cancellationToken).ConfigureAwait(false);
        foreach (var item in pending)
        {
            if (!results.TryGetValue(item.SourceText, out var translated) || string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }

            await _cacheRepository.SaveAsync(item.CacheKey, translated, cancellationToken).ConfigureAwait(false);
            _lastTranslations[item.Normalized] = translated;
            translations[item.SourceText] = translated;
        }

        if (skipTranslationCache)
        {
            return translations;
        }

        foreach (var line in lines)
        {
            var normalized = _normalizationService.Normalize(line.Text);
            if (_lastTranslations.TryGetValue(normalized, out var translated))
            {
                translations[line.Text] = translated;
            }
        }

        return translations;
    }

    private static string GetOverlayText(string sourceText, Dictionary<string, string> translations, int lineCount)
    {
        var text = translations.TryGetValue(sourceText, out var translated) ? translated : sourceText;
        return NormalizeOverlayText(text, lineCount);
    }

    private static IReadOnlyList<OverlayItem> BuildOverlayItems(
        IReadOnlyList<OcrLine> groupedLines,
        Dictionary<string, string> translations,
        Rect roiScreen,
        AppSettings settings)
    {
        if (!settings.EnableFixedRoiOverlay)
        {
            return groupedLines
                .Select(line => new OverlayItem(GetOverlayText(line.Text, translations, line.LineCount), line.Rect, line.LineCount, line.LineHeight))
                .ToList();
        }

        if (groupedLines.Count == 0)
        {
            return Array.Empty<OverlayItem>();
        }

        var ordered = groupedLines
            .OrderBy(line => line.Rect.Y)
            .ThenBy(line => line.Rect.X)
            .ToList();

        var lines = new List<string>(ordered.Count);
        foreach (var line in ordered)
        {
            var text = GetOverlayText(line.Text, translations, line.LineCount);
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(text);
            }
        }

        var combined = lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        var lineCount = Math.Max(1, ordered.Sum(line => Math.Max(1, line.LineCount)));
        var lineHeights = ordered.Select(line => line.LineHeight).Where(height => height > 0).ToList();
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

    private sealed record PendingTranslation(string SourceText, string Normalized, string CacheKey);
}

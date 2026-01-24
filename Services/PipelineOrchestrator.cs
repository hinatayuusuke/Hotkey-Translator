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

public sealed class PipelineOrchestrator
{
    private readonly CaptureManager _captureManager;
    private readonly OcrEngine _ocrEngine;
    private readonly OcrDiffService _ocrDiffService;
    private readonly PhashService _phashService;
    private readonly NormalizationService _normalizationService;
    private readonly OcrPreprocessService _ocrPreprocessService;
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

    public event Action<Bitmap>? OcrPreprocessPreviewReady;

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
        _lineGrouper = lineGrouper;
        _cacheRepository = cacheRepository;
        _cacheKeyBuilder = cacheKeyBuilder;
        _translationService = translationService;
        _overlayPresenter = overlayPresenter;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settingsService.Settings;
            _ocrDiffService.IouThreshold = settings.OcrIouThreshold;

            _overlayPresenter.Hide();
            using var frame = _captureManager.Capture(settings);

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

            using var roiBitmap = BitmapHelper.Crop(frame.Bitmap, roiInFrame);

            if (settings.PhashThreshold >= 0)
            {
                var hash = _phashService.ComputeHash(roiBitmap);
                if (_lastHash.HasValue && _phashService.IsSimilar(hash, _lastHash.Value, settings.PhashThreshold))
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
            OcrResultModel ocrResult;
            Bitmap? ocrInput = null;
            try
            {
                if (settings.EnableOcrTwoPass && !settings.EnableOcrBinarization)
                {
                    _logger.Info("OCR two-pass requested without binarization; falling back to single pass.");
                }

                if (settings.EnableOcrTwoPass && settings.EnableOcrBinarization)
                {
                    var twoPassResult = await RunTwoPassOcrAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
                    ocrResult = twoPassResult.Result;
                    ocrInput = twoPassResult.Input;
                }
                else
                {
                    if (settings.EnableOcrBinarization)
                    {
                        ocrInput = _ocrPreprocessService.Apply(roiBitmap, settings, null, null);
                    }
                    else
                    {
                        ocrInput = roiBitmap;
                    }

                    ocrResult = await _ocrEngine.RecognizeAsync(ocrInput, settings, cancellationToken).ConfigureAwait(false);
                }

                NotifyOcrPreprocessPreview(ocrInput);

                ocrStopwatch.Stop();
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
                _logger.Info($"OCR grouped: {groupedLines.Count} lines in {groupStopwatch.ElapsedMilliseconds} ms.");
                if (groupedLines.Count == 0)
                {
                    _logger.Info("OCR returned no lines.");
                    _overlayPresenter.ShowLast();
                    return;
                }

                var changedLines = _ocrDiffService.FilterChangedLines(groupedLines);
                _logger.Info($"OCR diff: {changedLines.Count} changed of {groupedLines.Count} total.");
                var translations = await ResolveTranslationsAsync(groupedLines, changedLines, settings, cancellationToken).ConfigureAwait(false);

                var overlayItems = groupedLines
                    .Select(line => new OverlayItem(GetOverlayText(line.Text, translations, line.LineCount), line.Rect, line.LineCount, line.LineHeight))
                    .ToList();

                _lastOverlayItems = overlayItems;
                _overlayPresenter.Update(overlayItems);
            }
            finally
            {
                if (settings.EnableOcrBinarization && ocrInput != null && !ReferenceEquals(ocrInput, roiBitmap))
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
        CancellationToken cancellationToken)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var changedSet = new HashSet<OcrLine>(changedLines);
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

    private async Task<OcrPassResult> RunTwoPassOcrAsync(
        Bitmap roiBitmap,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var low = Math.Min(settings.OcrTwoPassLowThreshold, settings.OcrTwoPassHighThreshold);
        var high = Math.Max(settings.OcrTwoPassLowThreshold, settings.OcrTwoPassHighThreshold);
        var candidates = new List<OcrPassResult>();

        try
        {
            if (settings.EnableOcrAutoThreshold && settings.OcrTwoPassPreferAuto)
            {
                candidates.Add(await RunOcrPassAsync("Auto", roiBitmap, settings, settings.OcrBinarizationThreshold, true, cancellationToken).ConfigureAwait(false));
            }

            candidates.Add(await RunOcrPassAsync("Low", roiBitmap, settings, low, false, cancellationToken).ConfigureAwait(false));
            candidates.Add(await RunOcrPassAsync("High", roiBitmap, settings, high, false, cancellationToken).ConfigureAwait(false));

            var best = candidates[0];
            foreach (var candidate in candidates.Skip(1))
            {
                if (IsBetterCandidate(candidate, best))
                {
                    best = candidate;
                }
            }

            foreach (var candidate in candidates)
            {
                if (!ReferenceEquals(candidate, best))
                {
                    candidate.Input.Dispose();
                }
            }

            var lowStats = FindCandidate(candidates, "Low");
            var highStats = FindCandidate(candidates, "High");
            if (lowStats != null && highStats != null)
            {
                _logger.Info($"OCR two-pass: low={low} (lines={lowStats.LineCount}, chars={lowStats.CharCount}), " +
                             $"high={high} (lines={highStats.LineCount}, chars={highStats.CharCount}) => picked={best.Label}");
            }

            return best;
        }
        catch
        {
            foreach (var candidate in candidates)
            {
                candidate.Input.Dispose();
            }

            throw;
        }
    }

    private async Task<OcrPassResult> RunOcrPassAsync(
        string label,
        Bitmap roiBitmap,
        AppSettings settings,
        int threshold,
        bool useAutoThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preprocessStopwatch = Stopwatch.StartNew();
        var input = _ocrPreprocessService.Apply(roiBitmap, settings, threshold, useAutoThreshold);
        preprocessStopwatch.Stop();

        var ocrStopwatch = Stopwatch.StartNew();
        var result = await _ocrEngine.RecognizeAsync(input, settings, cancellationToken).ConfigureAwait(false);
        ocrStopwatch.Stop();

        var stats = GetCandidateStats(result);
        _logger.Info($"OCR pass {label}: threshold={threshold} auto={useAutoThreshold} " +
                     $"lines={stats.LineCount}, chars={stats.CharCount}, " +
                     $"preprocess={preprocessStopwatch.ElapsedMilliseconds} ms, ocr={ocrStopwatch.ElapsedMilliseconds} ms.");

        return new OcrPassResult(label, result, input, stats.LineCount, stats.CharCount);
    }

    private static bool IsBetterCandidate(OcrPassResult candidate, OcrPassResult current)
    {
        if (candidate.LineCount != current.LineCount)
        {
            return candidate.LineCount > current.LineCount;
        }

        return candidate.CharCount > current.CharCount;
    }

    private static CandidateStats GetCandidateStats(OcrResultModel result)
    {
        var lineCount = result.Lines.Count;
        var charCount = result.Lines.Sum(line => line.Text?.Length ?? 0);
        return new CandidateStats(lineCount, charCount);
    }

    private static OcrPassResult? FindCandidate(IEnumerable<OcrPassResult> candidates, string label)
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.Label, label, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
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

    private sealed record CandidateStats(int LineCount, int CharCount);

    private sealed record OcrPassResult(string Label, OcrResultModel Result, Bitmap Input, int LineCount, int CharCount);

    private sealed record PendingTranslation(string SourceText, string Normalized, string CacheKey);
}

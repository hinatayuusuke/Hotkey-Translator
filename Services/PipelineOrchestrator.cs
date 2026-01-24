using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
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
    private const double OcrCharWeight = 10.0;
    private const double OcrLineWeight = 1.0;
    private const double OcrSymbolPenaltyThreshold = 0.45;
    private const double OcrSymbolPenaltyScale = 0.7;
    private const double SceneAreaPower = 0.7;
    private const double SceneTextPower = 0.3;
    private const double SceneCharWeight = 10.0;
    private const double SceneLineWeight = 1.0;
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Bitmap? roiSnapshot = null;
        Rect roiSnapshotBounds = default;
        try
        {
            var settings = _settingsService.Settings;
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
            roiSnapshot = (Bitmap)roiBitmap.Clone();
            roiSnapshotBounds = roiScreen;

            if (settings.EnableSceneChangeAutoHide &&
                TryComputeSceneChangeScore(roiBitmap, roiScreen, settings, out var sceneScore))
            {
                var threshold = Math.Clamp(settings.SceneChangeThreshold, 0.0, 1.0);
                _logger.Info($"Scene change score: {sceneScore:0.00} (threshold={threshold:0.00}, text-weighted={settings.EnableSceneChangeTextWeighted}).");
                if (sceneScore >= threshold)
                {
                    _overlayPresenter.SetEnabled(false);
                    OverlayAutoHidden?.Invoke(sceneScore);
                    return;
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

                var changedLines = options.SkipOcrDiff ? groupedLines : _ocrDiffService.FilterChangedLines(groupedLines);
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

    private bool TryComputeSceneChangeScore(Bitmap currentRoi, Rect roiScreen, AppSettings settings, out double score)
    {
        score = 0.0;
        if (!settings.EnableSceneChangeAutoHide)
        {
            return false;
        }

        if (_lastRoiSnapshot == null || _lastRoiBounds == null)
        {
            _logger.Info("Scene change skipped: no previous ROI snapshot.");
            return false;
        }

        if (_lastOverlayItems.Count == 0)
        {
            _logger.Info("Scene change skipped: no previous overlay items.");
            return false;
        }

        if (!AreRoiBoundsCompatible(_lastRoiBounds.Value, roiScreen))
        {
            _logger.Info($"Scene change skipped: ROI bounds changed (prev={FormatRect(_lastRoiBounds.Value)} current={FormatRect(roiScreen)}).");
            return false;
        }

        var maxArea = 0.0;
        var maxTextScore = 0.0;
        var textScores = new double[_lastOverlayItems.Count];
        for (var i = 0; i < _lastOverlayItems.Count; i++)
        {
            var item = _lastOverlayItems[i];
            var area = Math.Max(0.0, item.Rect.Width * item.Rect.Height);
            maxArea = Math.Max(maxArea, area);
            var textScore = GetSceneTextScore(item);
            textScores[i] = textScore;
            maxTextScore = Math.Max(maxTextScore, textScore);
        }

        if (maxArea <= 0)
        {
            _logger.Info("Scene change skipped: overlay areas are empty.");
            return false;
        }

        var roiLocal = new Rect(0, 0, currentRoi.Width, currentRoi.Height);
        var weightedSum = 0.0;
        var weightSum = 0.0;
        for (var i = 0; i < _lastOverlayItems.Count; i++)
        {
            var item = _lastOverlayItems[i];
            var localRect = new Rect(
                item.Rect.X - roiScreen.X,
                item.Rect.Y - roiScreen.Y,
                item.Rect.Width,
                item.Rect.Height);
            var clip = Rect.Intersect(roiLocal, localRect);
            if (clip.IsEmpty || clip.Width <= 1 || clip.Height <= 1)
            {
                continue;
            }

            var area = clip.Width * clip.Height;
            var areaNorm = Math.Clamp(area / maxArea, 0.0, 1.0);
            var weight = Math.Pow(areaNorm, SceneAreaPower);
            if (settings.EnableSceneChangeTextWeighted)
            {
                var textNorm = maxTextScore > 0 ? Math.Clamp(textScores[i] / maxTextScore, 0.0, 1.0) : 0.0;
                weight *= Math.Pow(textNorm, SceneTextPower);
            }

            if (weight <= 0)
            {
                continue;
            }

            using var currentCrop = BitmapHelper.Crop(currentRoi, clip);
            using var lastCrop = BitmapHelper.Crop(_lastRoiSnapshot, clip);
            var currentHash = _phashService.ComputeHash(currentCrop);
            var lastHash = _phashService.ComputeHash(lastCrop);
            var delta = _phashService.HammingDistance(currentHash, lastHash) / 64.0;

            weightedSum += delta * weight;
            weightSum += weight;
        }

        if (weightSum <= 0)
        {
            _logger.Info("Scene change skipped: no weighted overlay regions inside ROI.");
            return false;
        }

        score = Math.Clamp(weightedSum / weightSum, 0.0, 1.0);
        return true;
    }

    private static double GetSceneTextScore(OverlayItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            return 0.0;
        }

        var charCount = 0;
        foreach (var ch in item.Text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                charCount++;
            }
        }

        return (charCount * SceneCharWeight) + (item.LineCount * SceneLineWeight);
    }

    private void UpdateLastRoiSnapshot(Bitmap snapshot, Rect bounds)
    {
        _lastRoiSnapshot?.Dispose();
        _lastRoiSnapshot = snapshot;
        _lastRoiBounds = bounds;
    }

    private static bool AreRoiBoundsCompatible(Rect a, Rect b)
    {
        const double tolerance = 0.5;
        // WHY: Capture bounds can vary by sub-pixel amounts; allow small drift.
        return Math.Abs(a.X - b.X) <= tolerance &&
               Math.Abs(a.Y - b.Y) <= tolerance &&
               Math.Abs(a.Width - b.Width) <= tolerance &&
               Math.Abs(a.Height - b.Height) <= tolerance;
    }

    private static string FormatRect(Rect rect)
    {
        return $"{rect.X:0.##},{rect.Y:0.##} {rect.Width:0.##}x{rect.Height:0.##}";
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
                     $"lines={stats.LineCount}, chars={stats.CharCount}, symbols={stats.SymbolCount}, " +
                     $"preprocess={preprocessStopwatch.ElapsedMilliseconds} ms, ocr={ocrStopwatch.ElapsedMilliseconds} ms.");

        return new OcrPassResult(label, result, input, stats.LineCount, stats.CharCount, stats);
    }

    private static bool IsBetterCandidate(OcrPassResult candidate, OcrPassResult current)
    {
        var candidateScore = candidate.Stats.GetScore();
        var currentScore = current.Stats.GetScore();
        if (Math.Abs(candidateScore - currentScore) > double.Epsilon)
        {
            return candidateScore > currentScore;
        }

        if (candidate.CharCount != current.CharCount)
        {
            return candidate.CharCount > current.CharCount;
        }

        return candidate.LineCount > current.LineCount;
    }

    private static CandidateStats GetCandidateStats(OcrResultModel result)
    {
        var lineCount = result.Lines.Count;
        var charCount = 0;
        var symbolCount = 0;
        foreach (var line in result.Lines)
        {
            if (string.IsNullOrEmpty(line.Text))
            {
                continue;
            }

            foreach (var ch in line.Text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                charCount++;
                if (IsSymbolOrPunctuation(ch))
                {
                    symbolCount++;
                }
            }
        }

        return new CandidateStats(lineCount, charCount, symbolCount);
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

    private static bool IsSymbolOrPunctuation(char ch)
    {
        var category = char.GetUnicodeCategory(ch);
        return category == UnicodeCategory.MathSymbol ||
               category == UnicodeCategory.CurrencySymbol ||
               category == UnicodeCategory.ModifierSymbol ||
               category == UnicodeCategory.OtherSymbol ||
               category == UnicodeCategory.ConnectorPunctuation ||
               category == UnicodeCategory.DashPunctuation ||
               category == UnicodeCategory.OpenPunctuation ||
               category == UnicodeCategory.ClosePunctuation ||
               category == UnicodeCategory.InitialQuotePunctuation ||
               category == UnicodeCategory.FinalQuotePunctuation ||
               category == UnicodeCategory.OtherPunctuation;
    }

    private sealed record CandidateStats(int LineCount, int CharCount, int SymbolCount)
    {
        public double GetScore()
        {
            var score = (CharCount * OcrCharWeight) + (LineCount * OcrLineWeight);
            var ratio = CharCount > 0 ? SymbolCount / (double)CharCount : 0.0;
            if (ratio >= OcrSymbolPenaltyThreshold)
            {
                // WHY: Penalize symbol-heavy OCR output to avoid picking noisy candidates.
                score *= OcrSymbolPenaltyScale;
            }

            return score;
        }
    }

    private sealed record OcrPassResult(string Label, OcrResultModel Result, Bitmap Input, int LineCount, int CharCount, CandidateStats Stats);

    private sealed record PendingTranslation(string SourceText, string Normalized, string CacheKey);
}

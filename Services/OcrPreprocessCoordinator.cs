using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OcrPreprocessCoordinator
{
    private readonly OcrEngine _ocrEngine;
    private readonly OcrPreprocessService _preprocessService;
    private readonly OcrCandidateScorer _scorer;
    private readonly AppLogger? _logger;

    public OcrPreprocessCoordinator(
        OcrEngine ocrEngine,
        OcrPreprocessService preprocessService,
        OcrCandidateScorer scorer,
        AppLogger? logger)
    {
        _ocrEngine = ocrEngine;
        _preprocessService = preprocessService;
        _scorer = scorer;
        _logger = logger;
    }

    public async Task<OcrPassResult> RunAsync(Bitmap roiBitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.EnableOcrTwoPass && !settings.EnableOcrBinarization)
        {
            _logger?.Info("OCR two-pass requested without binarization; falling back to single pass.");
        }

        if (settings.EnableOcrTwoPass && settings.EnableOcrBinarization)
        {
            return await RunTwoPassAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
        }

        return await RunSinglePassAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OcrPassResult> RunSinglePassAsync(
        Bitmap roiBitmap,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var input = _preprocessService.Apply(roiBitmap, settings, null, null);

        var result = await _ocrEngine.RecognizeAsync(input, settings, cancellationToken).ConfigureAwait(false);
        var stats = _scorer.GetStats(result);
        return new OcrPassResult("Single", result, input, stats);
    }

    private async Task<OcrPassResult> RunTwoPassAsync(
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
                candidates.Add(await RunOcrPassAsync("Auto", roiBitmap, settings, settings.OcrBinarizationThreshold, true, cancellationToken)
                    .ConfigureAwait(false));
            }

            candidates.Add(await RunOcrPassAsync("Low", roiBitmap, settings, low, false, cancellationToken).ConfigureAwait(false));
            candidates.Add(await RunOcrPassAsync("High", roiBitmap, settings, high, false, cancellationToken).ConfigureAwait(false));

            var best = candidates[0];
            foreach (var candidate in candidates.Skip(1))
            {
                if (_scorer.IsBetter(candidate.Stats, best.Stats))
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
            if (lowStats.HasValue && highStats.HasValue)
            {
                var lowInfo = lowStats.Value;
                var highInfo = highStats.Value;
                _logger?.Info($"OCR two-pass: low={low} (lines={lowInfo.LineCount}, chars={lowInfo.CharCount}), " +
                              $"high={high} (lines={highInfo.LineCount}, chars={highInfo.CharCount}) => picked={best.Label}");
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
        var input = _preprocessService.Apply(roiBitmap, settings, threshold, useAutoThreshold);
        preprocessStopwatch.Stop();

        var ocrStopwatch = Stopwatch.StartNew();
        var result = await _ocrEngine.RecognizeAsync(input, settings, cancellationToken).ConfigureAwait(false);
        ocrStopwatch.Stop();

        var stats = _scorer.GetStats(result);
        _logger?.Info($"OCR pass {label}: threshold={threshold} auto={useAutoThreshold} " +
                      $"lines={stats.LineCount}, chars={stats.CharCount}, symbols={stats.SymbolCount}, " +
                      $"preprocess={preprocessStopwatch.ElapsedMilliseconds} ms, ocr={ocrStopwatch.ElapsedMilliseconds} ms.");

        return new OcrPassResult(label, result, input, stats);
    }

    private static OcrCandidateStats? FindCandidate(IEnumerable<OcrPassResult> candidates, string label)
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.Label, label, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Stats;
            }
        }

        return null;
    }
}

public sealed record OcrPassResult(string Label, OcrResultModel Result, Bitmap Input, OcrCandidateStats Stats);

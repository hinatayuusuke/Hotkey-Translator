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

    public async Task<OcrPassResult> RunVisionTextAsync(Bitmap roiBitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        return await RunSinglePassOverrideAsync(
                "Single",
                roiBitmap,
                settings,
                (input, activeSettings, token) => _ocrEngine.RecognizeVisionTextAsync(input, activeSettings, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OcrPassResult?> RunVisionGeometryAsync(Bitmap roiBitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        return await RunSinglePassOptionalOverrideAsync(
                "Single",
                roiBitmap,
                settings,
                (input, activeSettings, token) => _ocrEngine.RecognizeVisionGeometryAsync(input, activeSettings, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OcrPassResult> RunWinRtAsync(Bitmap roiBitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        return await RunSinglePassOverrideAsync(
                "Single",
                roiBitmap,
                settings,
                (input, activeSettings, token) => _ocrEngine.RecognizeWinRtAsync(input, activeSettings, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OcrPassResult> RunSinglePassAsync(
        Bitmap roiBitmap,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var inputState = PrepareOcrInput(roiBitmap, settings);
        var pass = await RunOcrPassAsync("Single", inputState, roiBitmap.Width, roiBitmap.Height, settings, null, null, cancellationToken)
            .ConfigureAwait(false);

        if (inputState.ShouldDispose && !ReferenceEquals(pass.Input, inputState.Bitmap))
        {
            inputState.Bitmap.Dispose();
        }

        return pass;
    }

    private async Task<OcrPassResult> RunSinglePassOverrideAsync(
        string label,
        Bitmap roiBitmap,
        AppSettings settings,
        Func<Bitmap, AppSettings, CancellationToken, Task<OcrResultModel>> recognizeAsync,
        CancellationToken cancellationToken)
    {
        var inputState = PrepareOcrInput(roiBitmap, settings);
        var pass = await RunOcrPassOverrideAsync(
                label,
                inputState,
                roiBitmap.Width,
                roiBitmap.Height,
                settings,
                null,
                null,
                recognizeAsync,
                cancellationToken)
            .ConfigureAwait(false);

        if (inputState.ShouldDispose && !ReferenceEquals(pass.Input, inputState.Bitmap))
        {
            inputState.Bitmap.Dispose();
        }

        return pass;
    }

    private async Task<OcrPassResult?> RunSinglePassOptionalOverrideAsync(
        string label,
        Bitmap roiBitmap,
        AppSettings settings,
        Func<Bitmap, AppSettings, CancellationToken, Task<OcrResultModel?>> recognizeAsync,
        CancellationToken cancellationToken)
    {
        var inputState = PrepareOcrInput(roiBitmap, settings);
        var pass = await RunOcrPassOptionalOverrideAsync(
                label,
                inputState,
                roiBitmap.Width,
                roiBitmap.Height,
                settings,
                null,
                null,
                recognizeAsync,
                cancellationToken)
            .ConfigureAwait(false);

        if (pass is null)
        {
            if (inputState.ShouldDispose)
            {
                inputState.Bitmap.Dispose();
            }

            return null;
        }

        if (inputState.ShouldDispose && !ReferenceEquals(pass.Input, inputState.Bitmap))
        {
            inputState.Bitmap.Dispose();
        }

        return pass;
    }

    private async Task<OcrPassResult> RunTwoPassAsync(
        Bitmap roiBitmap,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var low = Math.Min(settings.OcrTwoPassLowThreshold, settings.OcrTwoPassHighThreshold);
        var high = Math.Max(settings.OcrTwoPassLowThreshold, settings.OcrTwoPassHighThreshold);
        var candidates = new List<OcrPassResult>();
        var inputState = PrepareOcrInput(roiBitmap, settings);

        try
        {
            if (settings.EnableOcrAutoThreshold && settings.OcrTwoPassPreferAuto)
            {
                candidates.Add(await RunOcrPassAsync("Auto", inputState, roiBitmap.Width, roiBitmap.Height, settings, settings.OcrBinarizationThreshold, true, cancellationToken)
                    .ConfigureAwait(false));
            }

            candidates.Add(await RunOcrPassAsync("Low", inputState, roiBitmap.Width, roiBitmap.Height, settings, low, false, cancellationToken)
                .ConfigureAwait(false));
            candidates.Add(await RunOcrPassAsync("High", inputState, roiBitmap.Width, roiBitmap.Height, settings, high, false, cancellationToken)
                .ConfigureAwait(false));

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

            if (inputState.ShouldDispose && candidates.All(candidate => !ReferenceEquals(candidate.Input, inputState.Bitmap)))
            {
                inputState.Bitmap.Dispose();
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
        OcrInputState inputState,
        int originalWidth,
        int originalHeight,
        AppSettings settings,
        int? threshold,
        bool? useAutoThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preprocessStopwatch = Stopwatch.StartNew();
        var input = _preprocessService.Apply(inputState.Bitmap, settings, threshold, useAutoThreshold);
        preprocessStopwatch.Stop();

        var ocrStopwatch = Stopwatch.StartNew();
        var result = await _ocrEngine.RecognizeAsync(input, settings, cancellationToken).ConfigureAwait(false);
        ocrStopwatch.Stop();

        result = ScaleOcrResult(result, originalWidth, originalHeight, inputState.ScaleX, inputState.ScaleY);
        var stats = _scorer.GetStats(result);
        _logger?.Info($"OCR pass {label}: threshold={threshold} auto={useAutoThreshold} " +
                      $"lines={stats.LineCount}, chars={stats.CharCount}, symbols={stats.SymbolCount}, " +
                      $"preprocess={preprocessStopwatch.ElapsedMilliseconds} ms, ocr={ocrStopwatch.ElapsedMilliseconds} ms.");

        return new OcrPassResult(label, result, input, stats);
    }

    private async Task<OcrPassResult> RunOcrPassOverrideAsync(
        string label,
        OcrInputState inputState,
        int originalWidth,
        int originalHeight,
        AppSettings settings,
        int? threshold,
        bool? useAutoThreshold,
        Func<Bitmap, AppSettings, CancellationToken, Task<OcrResultModel>> recognizeAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preprocessStopwatch = Stopwatch.StartNew();
        var input = _preprocessService.Apply(inputState.Bitmap, settings, threshold, useAutoThreshold);
        preprocessStopwatch.Stop();

        var ocrStopwatch = Stopwatch.StartNew();
        var result = await recognizeAsync(input, settings, cancellationToken).ConfigureAwait(false);
        ocrStopwatch.Stop();

        result = ScaleOcrResult(result, originalWidth, originalHeight, inputState.ScaleX, inputState.ScaleY);
        var stats = _scorer.GetStats(result);
        _logger?.Info($"OCR pass {label}: threshold={threshold} auto={useAutoThreshold} " +
                      $"lines={stats.LineCount}, chars={stats.CharCount}, symbols={stats.SymbolCount}, " +
                      $"preprocess={preprocessStopwatch.ElapsedMilliseconds} ms, ocr={ocrStopwatch.ElapsedMilliseconds} ms.");

        return new OcrPassResult(label, result, input, stats);
    }

    private async Task<OcrPassResult?> RunOcrPassOptionalOverrideAsync(
        string label,
        OcrInputState inputState,
        int originalWidth,
        int originalHeight,
        AppSettings settings,
        int? threshold,
        bool? useAutoThreshold,
        Func<Bitmap, AppSettings, CancellationToken, Task<OcrResultModel?>> recognizeAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preprocessStopwatch = Stopwatch.StartNew();
        var input = _preprocessService.Apply(inputState.Bitmap, settings, threshold, useAutoThreshold);
        preprocessStopwatch.Stop();

        var ocrStopwatch = Stopwatch.StartNew();
        var result = await recognizeAsync(input, settings, cancellationToken).ConfigureAwait(false);
        ocrStopwatch.Stop();
        if (result is null)
        {
            if (!ReferenceEquals(input, inputState.Bitmap))
            {
                input.Dispose();
            }

            return null;
        }

        result = ScaleOcrResult(result, originalWidth, originalHeight, inputState.ScaleX, inputState.ScaleY);
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

    private static OcrInputState PrepareOcrInput(Bitmap roiBitmap, AppSettings settings)
    {
        if (!settings.EnableOcrDownsampling)
        {
            return new OcrInputState(roiBitmap, 1.0, 1.0, false);
        }

        var scale = Math.Clamp(settings.OcrDownsampleScale, 0.3, 1.0);
        if (scale >= 1.0)
        {
            return new OcrInputState(roiBitmap, 1.0, 1.0, false);
        }

        var targetWidth = Math.Max(1, (int)Math.Round(roiBitmap.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(roiBitmap.Height * scale));
        if (targetWidth == roiBitmap.Width && targetHeight == roiBitmap.Height)
        {
            return new OcrInputState(roiBitmap, 1.0, 1.0, false);
        }

        var resized = BitmapHelper.Resize(roiBitmap, targetWidth, targetHeight);
        var scaleX = targetWidth / (double)roiBitmap.Width;
        var scaleY = targetHeight / (double)roiBitmap.Height;
        return new OcrInputState(resized, scaleX, scaleY, true);
    }

    private static OcrResultModel ScaleOcrResult(
        OcrResultModel result,
        int originalWidth,
        int originalHeight,
        double scaleX,
        double scaleY)
    {
        if (scaleX <= 0 || scaleY <= 0)
        {
            return result;
        }

        if (Math.Abs(scaleX - 1.0) < 0.0001 && Math.Abs(scaleY - 1.0) < 0.0001)
        {
            return result;
        }

        var lines = result.Lines
            .Select(line => line with
            {
                Rect = new Rect(
                    line.Rect.X / scaleX,
                    line.Rect.Y / scaleY,
                    line.Rect.Width / scaleX,
                    line.Rect.Height / scaleY),
                LineHeight = line.LineHeight > 0 ? line.LineHeight / scaleY : 0
            })
            .ToList();

        return new OcrResultModel(lines, originalWidth, originalHeight);
    }

    private readonly record struct OcrInputState(Bitmap Bitmap, double ScaleX, double ScaleY, bool ShouldDispose);
}

public sealed record OcrPassResult(string Label, OcrResultModel Result, Bitmap Input, OcrCandidateStats Stats);

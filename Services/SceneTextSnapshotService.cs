using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public readonly record struct SceneTextSnapshotComparison(
    bool SemanticChanged,
    int AddedBlocks,
    int RemovedBlocks,
    int TextChangedBlocks,
    int MatchedBlocks,
    string Reason);

public sealed class SceneTextSnapshotService
{
    private readonly CaptureManager _captureManager;
    private readonly OcrPreprocessCoordinator _ocrPreprocessCoordinator;
    private readonly OcrLineGrouper _lineGrouper;
    private readonly ReadingUnitBuilder _readingUnitBuilder = new();
    private readonly AppLogger? _logger;

    public SceneTextSnapshotService(
        CaptureManager captureManager,
        OcrEngine ocrEngine,
        OcrLineGrouper lineGrouper,
        AppLogger? logger = null)
    {
        _captureManager = captureManager;
        _ocrPreprocessCoordinator = new OcrPreprocessCoordinator(
            ocrEngine,
            new OcrPreprocessService(),
            new OcrCandidateScorer(),
            logger);
        _lineGrouper = lineGrouper;
        _logger = logger;
    }

    public async Task<SceneTextSnapshot?> CaptureSnapshotAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        using var frame = _captureManager.Capture(settings);
        if (frame.IsBlack)
        {
            return null;
        }

        var roiScreen = GetRoiBounds(settings, frame.Bounds);
        if (roiScreen.IsEmpty)
        {
            return null;
        }

        var overlayClipScreen = ResolveOverlayClipScreenRect(settings, frame.Bounds);
        var roiInFrame = new Rect(
            roiScreen.X - frame.Bounds.X,
            roiScreen.Y - frame.Bounds.Y,
            roiScreen.Width,
            roiScreen.Height);

        using var roiBitmap = BitmapHelper.Crop(frame.Bitmap, roiInFrame);
        OcrPassResult passResult;
        try
        {
            passResult = await _ocrPreprocessCoordinator.RunAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Scene semantic Stage B OCR capture failed.");
            throw;
        }

        var ocrInput = passResult.Input;
        try
        {
            var rawLines = passResult.Result.Lines;
            if (settings.OcrEngine == OcrEngineKind.Paddle && settings.EnablePaddleConfidenceFilter)
            {
                var threshold = Math.Clamp(settings.PaddleConfidenceThreshold, 0.0, 1.0);
                rawLines = rawLines
                    .Where(line => line.Confidence >= threshold)
                    .ToList();
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

            var groupedLines = _lineGrouper.MergeLines(mappedLines, settings).ToList();
            var readingUnits = _readingUnitBuilder.Build(groupedLines, settings).ToList();
            var blocks = BuildBlocks(readingUnits);
            return new SceneTextSnapshot(
                blocks,
                readingUnits,
                roiScreen,
                overlayClipScreen,
                DateTime.UtcNow,
                BuildSnapshotSignature(settings));
        }
        finally
        {
            if (!ReferenceEquals(ocrInput, roiBitmap))
            {
                ocrInput.Dispose();
            }
        }
    }

    public SceneTextSnapshotComparison CompareSnapshots(
        SceneTextSnapshot previous,
        SceneTextSnapshot current,
        AppSettings settings)
    {
        var iouThreshold = Math.Clamp(settings.SceneSemanticBlockIouThreshold, 0.1, 0.95);
        var minChars = Math.Max(0, settings.SceneSemanticMinChars);
        var previousBlocks = previous.Blocks.Where(block => block.CharCount >= minChars).ToList();
        var currentBlocks = current.Blocks.Where(block => block.CharCount >= minChars).ToList();

        var matches = MatchByIou(previousBlocks, currentBlocks, iouThreshold);
        var textChanged = 0;
        foreach (var (previousIndex, currentIndex) in matches)
        {
            if (!string.Equals(
                    previousBlocks[previousIndex].NormalizedText,
                    currentBlocks[currentIndex].NormalizedText,
                    StringComparison.Ordinal))
            {
                textChanged++;
            }
        }

        var matched = matches.Count;
        var added = Math.Max(0, currentBlocks.Count - matched);
        var removed = Math.Max(0, previousBlocks.Count - matched);
        var semanticChanged = added > 0 || removed > 0 || textChanged > 0;
        var reason = semanticChanged
            ? $"added={added}, removed={removed}, textChanged={textChanged}, matched={matched}"
            : $"no semantic change (matched={matched}, blocks={currentBlocks.Count})";

        return new SceneTextSnapshotComparison(
            semanticChanged,
            added,
            removed,
            textChanged,
            matched,
            reason);
    }

    public static string BuildSnapshotSignature(AppSettings settings)
    {
        return
            $"{settings.OcrEngine}|{settings.EnableRoi}|{settings.NormalizedRoi?.X:0.####},{settings.NormalizedRoi?.Y:0.####}," +
            $"{settings.NormalizedRoi?.Width:0.####},{settings.NormalizedRoi?.Height:0.####}|{settings.SourceLanguage}|{settings.TargetLanguage}|" +
            $"{settings.EnableOcrBinarization}|{settings.OcrBinarizationThreshold}|{settings.EnableOcrAutoThreshold}|{settings.EnableOcrAutoInvert}|" +
            $"{settings.EnableOcrGamma}|{settings.OcrGamma:0.####}|{settings.EnableOcrDownsampling}|{settings.OcrDownsampleScale:0.####}|" +
            $"{settings.EnableOcrTwoPass}|{settings.OcrTwoPassLowThreshold}|{settings.OcrTwoPassHighThreshold}|{settings.OcrTwoPassPreferAuto}|" +
            $"{settings.EnableLineMerge}|{settings.EnableTwoStageLineMerge}|{settings.VerticalModeOverride}|{settings.VerticalColumnOrder}|{settings.VerticalModeAutoDetect}|" +
            $"{settings.MergeOverlapRatioThreshold:0.####}|{settings.MergeVerticalWeight:0.####}|{settings.MergeThresholdRatio:0.####}|" +
            $"{settings.RowMergeYCenterToleranceRatio:0.####}|{settings.RowMergeHeightRatioMin:0.####}|{settings.RowMergeMaxGapRatio:0.####}|{settings.RowMergeHardBreakRatio:0.####}|" +
            $"{settings.EnableVerticalMerge}|{settings.VerticalGapRatio:0.####}|{settings.EnableVerticalColumnMerge}|{settings.VerticalColumnMergeOverlapRatioThreshold:0.####}|" +
            $"{settings.VerticalColumnMergeWeight:0.####}|{settings.VerticalColumnMergeThresholdRatio:0.####}|{settings.VerticalColumnMergeHardBreakRatio:0.####}|{settings.EnableEngineScaledLineMergeProfile}";
    }

    private static List<SceneTextBlock> BuildBlocks(IReadOnlyList<ReadingUnit> readingUnits)
    {
        var blocks = new List<SceneTextBlock>(readingUnits.Count);
        foreach (var unit in readingUnits)
        {
            var normalized = NormalizeForComparison(unit.Text);
            if (normalized.Length == 0)
            {
                continue;
            }

            blocks.Add(new SceneTextBlock(unit.Rect, normalized, CountNonWhitespace(normalized)));
        }

        return blocks;
    }

    private static string NormalizeForComparison(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(normalized.Length);
        var previousWasWhitespace = false;
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(ch);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static int CountNonWhitespace(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                count++;
            }
        }

        return count;
    }

    private static List<(int PreviousIndex, int CurrentIndex)> MatchByIou(
        IReadOnlyList<SceneTextBlock> previous,
        IReadOnlyList<SceneTextBlock> current,
        double iouThreshold)
    {
        var candidates = new List<(int PreviousIndex, int CurrentIndex, double Iou)>();
        for (var i = 0; i < previous.Count; i++)
        {
            for (var j = 0; j < current.Count; j++)
            {
                var iou = ComputeIou(previous[i].Rect, current[j].Rect);
                if (iou >= iouThreshold)
                {
                    candidates.Add((i, j, iou));
                }
            }
        }

        candidates.Sort((a, b) => b.Iou.CompareTo(a.Iou));
        var matchedPrevious = new HashSet<int>();
        var matchedCurrent = new HashSet<int>();
        var matches = new List<(int PreviousIndex, int CurrentIndex)>();
        foreach (var candidate in candidates)
        {
            if (matchedPrevious.Contains(candidate.PreviousIndex) || matchedCurrent.Contains(candidate.CurrentIndex))
            {
                continue;
            }

            matchedPrevious.Add(candidate.PreviousIndex);
            matchedCurrent.Add(candidate.CurrentIndex);
            matches.Add((candidate.PreviousIndex, candidate.CurrentIndex));
        }

        return matches;
    }

    private static double ComputeIou(Rect a, Rect b)
    {
        var intersection = Rect.Intersect(a, b);
        if (intersection.IsEmpty)
        {
            return 0;
        }

        var intersectionArea = intersection.Width * intersection.Height;
        var unionArea = (a.Width * a.Height) + (b.Width * b.Height) - intersectionArea;
        return unionArea <= 0 ? 0 : intersectionArea / unionArea;
    }

    private static Rect GetRoiBounds(AppSettings settings, Rect frameBounds)
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

        return Rect.Intersect(frameBounds, settings.Roi.Value.ToRect());
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
}

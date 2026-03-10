using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class VisionGeometryHybridAligner
{
    private const double SyntheticMergePenaltyThreshold = 0.18;
    private const double SyntheticMergeOverlapMin = 0.02;
    private const double SyntheticMergeMaxDistanceMultiplier = 3.50;
    private const double SplitGeometryRunMinTextSimilarity = 0.62;
    private const double SplitGeometrySegmentMinSimilarity = 0.38;
    private const int SplitGeometryRunMaxSegments = 3;
    private readonly NormalizationService _normalizationService = new();
    private readonly AppLogger? _logger;

    public VisionGeometryHybridAligner(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public HybridOcrAlignmentResult Align(
        IReadOnlyList<OcrLine> geometryLines,
        IReadOnlyList<OcrLine> visionLines,
        int imageWidth,
        int imageHeight,
        AppSettings settings)
    {
        if (visionLines.Count == 0)
        {
            return new HybridOcrAlignmentResult(geometryLines.ToList(), geometryLines.Count, 0, 0, 0);
        }

        if (geometryLines.Count == 0)
        {
            return new HybridOcrAlignmentResult(
                settings.VisionGeometryAllowSyntheticFallback ? visionLines.ToList() : new List<OcrLine>(),
                0,
                visionLines.Count,
                0,
                settings.VisionGeometryAllowSyntheticFallback ? visionLines.Count : 0);
        }

        var geometry = geometryLines
            .Select((line, index) => new GeometryCandidate(line, index, _normalizationService.Normalize(line.Text)))
            .ToList();
        var threshold = Math.Clamp(settings.VisionGeometryMatchMinScore, 0.0, 1.0);
        var matchedOutputs = new OcrLine?[visionLines.Count];
        var matchedGeometryByVisionIndex = new GeometryCandidate?[visionLines.Count];
        var matchedGeometryIndexes = new HashSet<int>();
        var matchedCount = 0;

        for (var visionIndex = 0; visionIndex < visionLines.Count; visionIndex++)
        {
            var visionLine = visionLines[visionIndex];
            var normalizedVision = _normalizationService.Normalize(visionLine.Text);
            GeometryCandidate? best = null;
            var bestScore = double.MinValue;

            foreach (var candidate in geometry)
            {
                if (matchedGeometryIndexes.Contains(candidate.Index))
                {
                    continue;
                }

                var score = ScoreCandidate(
                    normalizedVision,
                    candidate.NormalizedText,
                    visionLine.Text,
                    candidate.Line.Text,
                    visionIndex,
                    candidate.Index,
                    visionLines.Count,
                    geometry.Count);
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                best = candidate;
            }

            if (best is not null && bestScore >= threshold)
            {
                matchedGeometryIndexes.Add(best.Index);
                matchedCount++;
                matchedGeometryByVisionIndex[visionIndex] = best;
                matchedOutputs[visionIndex] = visionLine with
                {
                    Rect = best.Line.Rect,
                    Confidence = Math.Max(visionLine.Confidence, best.Line.Confidence),
                    LineHeight = best.Line.Rect.Height,
                    LineCount = Math.Max(visionLine.LineCount, best.Line.LineCount)
                };
            }
        }

        var splitOutputsByVisionIndex = new Dictionary<int, List<OcrLine>>();
        var splitCount = 0;
        for (var visionIndex = 0; visionIndex < visionLines.Count; visionIndex++)
        {
            if (matchedOutputs[visionIndex] is null || matchedGeometryByVisionIndex[visionIndex] is null)
            {
                continue;
            }

            if (TryBuildSplitOutputsForVision(
                    geometry,
                    visionLines[visionIndex],
                    matchedGeometryByVisionIndex[visionIndex]!,
                    matchedGeometryIndexes,
                    splitOutputsByVisionIndex,
                    out var splitOutputs))
            {
                splitOutputsByVisionIndex[visionIndex] = splitOutputs;
                splitCount++;
            }
        }

        var matchedSlots = new List<MatchedSlot>();
        foreach (var visionIndex in Enumerable.Range(0, visionLines.Count))
        {
            if (splitOutputsByVisionIndex.TryGetValue(visionIndex, out var splitOutputs))
            {
                matchedSlots.AddRange(splitOutputs.Select(line => new MatchedSlot(visionIndex, line.Rect)));
                continue;
            }

            if (matchedOutputs[visionIndex] is OcrLine matchedLine)
            {
                matchedSlots.Add(new MatchedSlot(visionIndex, matchedLine.Rect));
            }
        }
        var occupiedRects = matchedSlots.Select(slot => slot.Rect).ToList();
        var mergedTextByMatchedVisionIndex = new Dictionary<int, List<VisionTextContribution>>();
        foreach (var matchedSlot in matchedSlots)
        {
            if (splitOutputsByVisionIndex.ContainsKey(matchedSlot.VisionIndex))
            {
                continue;
            }

            var matchedLine = matchedOutputs[matchedSlot.VisionIndex]!;
            mergedTextByMatchedVisionIndex[matchedSlot.VisionIndex] =
            [
                new VisionTextContribution(matchedSlot.VisionIndex, matchedLine.Text)
            ];
        }

        var standaloneSyntheticByStartIndex = new Dictionary<int, OcrLine>();
        var syntheticCount = 0;
        var syntheticMergedCount = 0;

        for (var visionIndex = 0; visionIndex < visionLines.Count;)
        {
            if (matchedOutputs[visionIndex] is not null)
            {
                visionIndex++;
                continue;
            }

            if (!settings.VisionGeometryAllowSyntheticFallback)
            {
                visionIndex++;
                continue;
            }

            var groupStart = visionIndex;
            var groupEnd = visionIndex;
            while (groupEnd + 1 < visionLines.Count && matchedOutputs[groupEnd + 1] is null)
            {
                groupEnd++;
            }

            var syntheticLine = BuildSyntheticLineForGroup(
                visionLines,
                groupStart,
                groupEnd,
                matchedSlots,
                occupiedRects,
                imageWidth,
                imageHeight);
            var overlapPenalty = ComputeOverlapPenalty(syntheticLine.Rect, occupiedRects);
            // WHY: If the fallback panel still collides heavily after readability-first placement,
            // the safer choice is to fold the text back into the nearest matched geometry block.
            if (overlapPenalty >= SyntheticMergePenaltyThreshold &&
                TryFindMergeTargetVisionIndex(syntheticLine.Rect, matchedSlots, out var mergeTargetVisionIndex))
            {
                if (!mergedTextByMatchedVisionIndex.TryGetValue(mergeTargetVisionIndex, out var contributions))
                {
                    contributions = new List<VisionTextContribution>();
                    mergedTextByMatchedVisionIndex[mergeTargetVisionIndex] = contributions;
                }

                for (var groupIndex = groupStart; groupIndex <= groupEnd; groupIndex++)
                {
                    var text = visionLines[groupIndex].Text.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    contributions.Add(new VisionTextContribution(groupIndex, text));
                }

                syntheticMergedCount++;
            }
            else
            {
                standaloneSyntheticByStartIndex[groupStart] = syntheticLine;
                syntheticCount++;
                occupiedRects.Add(syntheticLine.Rect);
            }

            visionIndex = groupEnd + 1;
        }

        var lines = new List<OcrLine>(visionLines.Count);
        for (var visionIndex = 0; visionIndex < visionLines.Count; visionIndex++)
        {
            if (splitOutputsByVisionIndex.TryGetValue(visionIndex, out var splitOutputs))
            {
                if (mergedTextByMatchedVisionIndex.TryGetValue(visionIndex, out var extraContributions))
                {
                    var extraText = string.Join(
                        Environment.NewLine,
                        extraContributions
                            .OrderBy(item => item.VisionIndex)
                            .Select(item => item.Text.Trim())
                            .Where(text => !string.IsNullOrWhiteSpace(text)));
                    if (!string.IsNullOrWhiteSpace(extraText))
                    {
                        var lastIndex = splitOutputs.Count - 1;
                        var mergedSplitText = string.IsNullOrWhiteSpace(splitOutputs[lastIndex].Text)
                            ? extraText
                            : $"{splitOutputs[lastIndex].Text}{Environment.NewLine}{extraText}";
                        splitOutputs[lastIndex] = splitOutputs[lastIndex] with
                        {
                            // WHY: Preserve the Vision-side line structure here and let OverlayStage
                            // decide later whether the final box needs to collapse or clamp line breaks.
                            Text = mergedSplitText.Trim()
                        };
                    }
                }

                lines.AddRange(splitOutputs);
                continue;
            }

            if (matchedOutputs[visionIndex] is OcrLine matchedLine)
            {
                if (mergedTextByMatchedVisionIndex.TryGetValue(visionIndex, out var contributions))
                {
                    var mergedText = string.Join(
                        Environment.NewLine,
                        contributions
                            .OrderBy(item => item.VisionIndex)
                            .Select(item => item.Text.Trim())
                            .Where(text => !string.IsNullOrWhiteSpace(text)));
                    if (!string.IsNullOrWhiteSpace(mergedText))
                    {
                        matchedLine = matchedLine with { Text = mergedText };
                    }
                }

                lines.Add(matchedLine);
                continue;
            }

            if (standaloneSyntheticByStartIndex.TryGetValue(visionIndex, out var syntheticLine))
            {
                lines.Add(syntheticLine);
            }
        }

        _logger?.Info(
            $"stage=vision_geometry_hybrid event=summary geometryLines={geometryLines.Count} visionLines={visionLines.Count} matched={matchedCount} split={splitCount} synthetic={syntheticCount} merged={syntheticMergedCount} output={lines.Count}.");
        return new HybridOcrAlignmentResult(lines, geometryLines.Count, visionLines.Count, matchedCount, syntheticCount);
    }

    private bool TryBuildSplitOutputsForVision(
        IReadOnlyList<GeometryCandidate> geometry,
        OcrLine visionLine,
        GeometryCandidate anchor,
        IReadOnlySet<int> matchedGeometryIndexes,
        IReadOnlyDictionary<int, List<OcrLine>> existingSplitOutputs,
        out List<OcrLine> splitOutputs)
    {
        splitOutputs = [];
        if (string.IsNullOrWhiteSpace(visionLine.Text))
        {
            return false;
        }

        var normalizedVision = _normalizationService.Normalize(visionLine.Text);
        if (normalizedVision.Length < 6)
        {
            return false;
        }

        var anchorTextSimilarity = ComputeTextSimilarity(normalizedVision, anchor.NormalizedText, visionLine.Text, anchor.Line.Text);
        var anchorLengthScore = ComputeLengthScore(normalizedVision, anchor.NormalizedText, visionLine.Text, anchor.Line.Text);
        GeometryRunCandidate? bestRun = null;
        var bestRunScore = double.MinValue;

        foreach (var run in BuildSplitRunCandidates(geometry, anchor.Index, matchedGeometryIndexes, existingSplitOutputs))
        {
            if (!TryScoreGeometryRun(run, visionLine, normalizedVision, anchorTextSimilarity, anchorLengthScore, out var runScore))
            {
                continue;
            }

            if (runScore <= bestRunScore)
            {
                continue;
            }

            bestRunScore = runScore;
            bestRun = run;
        }

        if (bestRun is null)
        {
            return false;
        }

        return TrySplitVisionTextAcrossGeometryRun(visionLine, bestRun, out splitOutputs);
    }

    private IEnumerable<GeometryRunCandidate> BuildSplitRunCandidates(
        IReadOnlyList<GeometryCandidate> geometry,
        int anchorIndex,
        IReadOnlySet<int> matchedGeometryIndexes,
        IReadOnlyDictionary<int, List<OcrLine>> existingSplitOutputs)
    {
        var alreadySplitRects = existingSplitOutputs.Values.SelectMany(lines => lines).Select(line => line.Rect).ToList();
        for (var length = 2; length <= SplitGeometryRunMaxSegments; length++)
        {
            for (var start = anchorIndex - (length - 1); start <= anchorIndex; start++)
            {
                if (start < 0 || start + length > geometry.Count)
                {
                    continue;
                }

                if (anchorIndex < start || anchorIndex >= start + length)
                {
                    continue;
                }

                var segmentCandidates = new List<GeometryCandidate>(length);
                var valid = true;
                for (var index = start; index < start + length; index++)
                {
                    var candidate = geometry[index];
                    if (candidate.Index != anchorIndex && matchedGeometryIndexes.Contains(candidate.Index))
                    {
                        valid = false;
                        break;
                    }

                    if (alreadySplitRects.Any(rect => rect == candidate.Line.Rect))
                    {
                        valid = false;
                        break;
                    }

                    segmentCandidates.Add(candidate);
                }

                if (!valid || !IsGeometryRunCompatible(segmentCandidates))
                {
                    continue;
                }

                var combinedText = string.Join(" ", segmentCandidates.Select(item => item.Line.Text));
                yield return new GeometryRunCandidate(
                    segmentCandidates,
                    _normalizationService.Normalize(combinedText),
                    combinedText);
            }
        }
    }

    private static bool IsGeometryRunCompatible(IReadOnlyList<GeometryCandidate> run)
    {
        if (run.Count <= 1)
        {
            return false;
        }

        for (var index = 1; index < run.Count; index++)
        {
            if (!AreNeighborRectsCompatible(run[index - 1].Line.Rect, run[index].Line.Rect))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreNeighborRectsCompatible(Rect left, Rect right)
    {
        var horizontalOverlap = Math.Max(0.0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        var verticalOverlap = Math.Max(0.0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        var horizontalOverlapRatio = horizontalOverlap / Math.Max(1.0, Math.Min(left.Width, right.Width));
        var verticalOverlapRatio = verticalOverlap / Math.Max(1.0, Math.Min(left.Height, right.Height));
        var verticalGap = Math.Max(0.0, Math.Max(left.Top, right.Top) - Math.Min(left.Bottom, right.Bottom));
        var horizontalGap = Math.Max(0.0, Math.Max(left.Left, right.Left) - Math.Min(left.Right, right.Right));
        var maxHeight = Math.Max(left.Height, right.Height);
        var maxWidth = Math.Max(left.Width, right.Width);

        if (verticalGap <= maxHeight * 1.30 && horizontalOverlapRatio >= 0.18)
        {
            return true;
        }

        return horizontalGap <= maxWidth * 0.25 && verticalOverlapRatio >= 0.45;
    }

    private static bool TryScoreGeometryRun(
        GeometryRunCandidate run,
        OcrLine visionLine,
        string normalizedVision,
        double anchorTextSimilarity,
        double anchorLengthScore,
        out double runScore)
    {
        runScore = double.MinValue;
        if (run.NormalizedText.Length <= 0)
        {
            return false;
        }

        var runTextSimilarity = ComputeTextSimilarity(normalizedVision, run.NormalizedText, visionLine.Text, run.RawText);
        var runLengthScore = ComputeLengthScore(normalizedVision, run.NormalizedText, visionLine.Text, run.RawText);
        if (runTextSimilarity < SplitGeometryRunMinTextSimilarity)
        {
            return false;
        }

        // WHY: Split is only useful when the multi-box geometry covers meaningfully more of the Vision line
        // than the single anchor box. Otherwise the current 1:1 mapping is more stable.
        if (runTextSimilarity + 0.05 < anchorTextSimilarity || runLengthScore <= anchorLengthScore + 0.12)
        {
            return false;
        }

        runScore = (runTextSimilarity * 0.70) + (runLengthScore * 0.30);
        return true;
    }

    private bool TrySplitVisionTextAcrossGeometryRun(
        OcrLine visionLine,
        GeometryRunCandidate run,
        out List<OcrLine> splitOutputs)
    {
        splitOutputs = [];
        var projection = BuildNormalizedProjection(visionLine.Text);
        if (projection.NormalizedText.Length < run.Segments.Count)
        {
            return false;
        }

        if (!TryFindBestNormalizedSplit(projection, run, out var splitPoints))
        {
            return false;
        }

        var segmentOutputs = new List<OcrLine>(run.Segments.Count);
        var start = 0;
        for (var segmentIndex = 0; segmentIndex < run.Segments.Count; segmentIndex++)
        {
            var end = splitPoints[segmentIndex];
            var rawSegment = projection.ExtractRawSegment(start, end);
            if (string.IsNullOrWhiteSpace(rawSegment))
            {
                return false;
            }

            var geometrySegment = run.Segments[segmentIndex];
            segmentOutputs.Add(new OcrLine(
                rawSegment,
                geometrySegment.Line.Rect,
                Math.Max(visionLine.Confidence, geometrySegment.Line.Confidence),
                Math.Max(1, geometrySegment.Line.LineCount),
                geometrySegment.Line.Rect.Height));
            start = end;
        }

        splitOutputs = segmentOutputs;
        return true;
    }

    private bool TryFindBestNormalizedSplit(
        NormalizedProjection projection,
        GeometryRunCandidate run,
        out int[] splitPoints)
    {
        splitPoints = [];
        var normalizedLength = projection.NormalizedText.Length;
        var segmentCount = run.Segments.Count;
        var boundaries = new int[segmentCount];
        var bestAverageScore = double.MinValue;
        var bestBoundaries = Array.Empty<int>();

        void Search(int segmentIndex, int start)
        {
            if (segmentIndex == segmentCount - 1)
            {
                if (start >= normalizedLength)
                {
                    return;
                }

                boundaries[segmentIndex] = normalizedLength;
                if (TryScoreNormalizedSplit(projection, run, boundaries, out var averageScore) &&
                    averageScore > bestAverageScore)
                {
                    bestAverageScore = averageScore;
                    bestBoundaries = boundaries.ToArray();
                }

                return;
            }

            var remainingSegments = segmentCount - segmentIndex;
            var maxBoundary = normalizedLength - (remainingSegments - 1);
            for (var boundary = start + 1; boundary <= maxBoundary; boundary++)
            {
                boundaries[segmentIndex] = boundary;
                Search(segmentIndex + 1, boundary);
            }
        }

        Search(0, 0);
        if (bestBoundaries.Length != segmentCount)
        {
            return false;
        }

        splitPoints = bestBoundaries;
        return true;
    }

    private static bool TryScoreNormalizedSplit(
        NormalizedProjection projection,
        GeometryRunCandidate run,
        IReadOnlyList<int> boundaries,
        out double averageScore)
    {
        averageScore = 0.0;
        var start = 0;
        var totalScore = 0.0;
        for (var segmentIndex = 0; segmentIndex < run.Segments.Count; segmentIndex++)
        {
            var end = boundaries[segmentIndex];
            if (end <= start || end > projection.NormalizedText.Length)
            {
                return false;
            }

            var normalizedSegment = projection.NormalizedText[start..end];
            var rawSegment = projection.ExtractRawSegment(start, end);
            if (string.IsNullOrWhiteSpace(rawSegment))
            {
                return false;
            }

            var geometrySegment = run.Segments[segmentIndex];
            var textScore = ComputeTextSimilarity(normalizedSegment, geometrySegment.NormalizedText, rawSegment, geometrySegment.Line.Text);
            var lengthScore = ComputeLengthScore(normalizedSegment, geometrySegment.NormalizedText, rawSegment, geometrySegment.Line.Text);
            var segmentScore = (textScore * 0.70) + (lengthScore * 0.30);
            if (segmentScore < SplitGeometrySegmentMinSimilarity)
            {
                return false;
            }

            totalScore += segmentScore;
            start = end;
        }

        averageScore = totalScore / run.Segments.Count;
        return true;
    }

    private static double ScoreCandidate(
        string normalizedVision,
        string normalizedGeometry,
        string rawVision,
        string rawGeometry,
        int visionIndex,
        int geometryIndex,
        int visionCount,
        int geometryCount)
    {
        var orderScore = ComputeOrderScore(visionIndex, geometryIndex, visionCount, geometryCount);
        var textScore = ComputeTextSimilarity(normalizedVision, normalizedGeometry, rawVision, rawGeometry);
        var lengthScore = ComputeLengthScore(normalizedVision, normalizedGeometry, rawVision, rawGeometry);
        return (orderScore * 0.50) + (textScore * 0.35) + (lengthScore * 0.15);
    }

    private static double ComputeOrderScore(int visionIndex, int geometryIndex, int visionCount, int geometryCount)
    {
        if (visionCount <= 1 || geometryCount <= 1)
        {
            return visionIndex == geometryIndex ? 1.0 : 0.75;
        }

        var visionPosition = visionIndex / (double)(visionCount - 1);
        var geometryPosition = geometryIndex / (double)(geometryCount - 1);
        return Math.Max(0.0, 1.0 - Math.Abs(visionPosition - geometryPosition));
    }

    private static double ComputeTextSimilarity(
        string normalizedVision,
        string normalizedGeometry,
        string rawVision,
        string rawGeometry)
    {
        if (normalizedVision.Length == 0 || normalizedGeometry.Length == 0)
        {
            return 0.0;
        }

        if (string.Equals(normalizedVision, normalizedGeometry, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (normalizedVision.Contains(normalizedGeometry, StringComparison.Ordinal) ||
            normalizedGeometry.Contains(normalizedVision, StringComparison.Ordinal))
        {
            return 0.88;
        }

        var editSimilarity = ComputeEditSimilarity(normalizedVision, normalizedGeometry);
        var scriptSimilarity = ComputeScriptHintSimilarity(rawVision, rawGeometry);
        return Math.Max(editSimilarity, scriptSimilarity * 0.85);
    }

    private static double ComputeLengthScore(
        string normalizedVision,
        string normalizedGeometry,
        string rawVision,
        string rawGeometry)
    {
        var left = normalizedVision.Length > 0 ? normalizedVision.Length : rawVision.Trim().Length;
        var right = normalizedGeometry.Length > 0 ? normalizedGeometry.Length : rawGeometry.Trim().Length;
        if (left <= 0 || right <= 0)
        {
            return 0.0;
        }

        return Math.Min(left, right) / (double)Math.Max(left, right);
    }

    private static double ComputeEditSimilarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0.0;
        }

        var distances = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            distances[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            var previousDiagonal = distances[0];
            distances[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var temp = distances[j];
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                distances[j] = Math.Min(
                    Math.Min(distances[j] + 1, distances[j - 1] + 1),
                    previousDiagonal + cost);
                previousDiagonal = temp;
            }
        }

        var maxLength = Math.Max(left.Length, right.Length);
        return maxLength == 0 ? 1.0 : Math.Max(0.0, 1.0 - (distances[right.Length] / (double)maxLength));
    }

    private static double ComputeScriptHintSimilarity(string left, string right)
    {
        var leftProfile = BuildScriptProfile(left);
        var rightProfile = BuildScriptProfile(right);
        var leftTotal = leftProfile.Latin + leftProfile.Digit + leftProfile.Cjk;
        var rightTotal = rightProfile.Latin + rightProfile.Digit + rightProfile.Cjk;
        if (leftTotal == 0 || rightTotal == 0)
        {
            return 0.0;
        }

        var latin = Math.Min(leftProfile.Latin / (double)leftTotal, rightProfile.Latin / (double)rightTotal);
        var digit = Math.Min(leftProfile.Digit / (double)leftTotal, rightProfile.Digit / (double)rightTotal);
        var cjk = Math.Min(leftProfile.Cjk / (double)leftTotal, rightProfile.Cjk / (double)rightTotal);
        return latin + digit + cjk;
    }

    private static ScriptProfile BuildScriptProfile(string value)
    {
        var profile = new ScriptProfile();
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (char.IsDigit(ch))
            {
                profile.Digit++;
                continue;
            }

            if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
            {
                profile.Latin++;
                continue;
            }

            if (ch is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF')
            {
                profile.Cjk++;
            }
        }

        return profile;
    }

    private static OcrLine BuildSyntheticLineForGroup(
        IReadOnlyList<OcrLine> visionLines,
        int groupStart,
        int groupEnd,
        IReadOnlyList<MatchedSlot> matchedSlots,
        IReadOnlyList<Rect> occupiedRects,
        int imageWidth,
        int imageHeight)
    {
        var previous = matchedSlots.Where(slot => slot.VisionIndex < groupStart).OrderBy(slot => slot.VisionIndex).LastOrDefault();
        var next = matchedSlots.Where(slot => slot.VisionIndex > groupEnd).OrderBy(slot => slot.VisionIndex).FirstOrDefault();
        var groupLines = visionLines.Skip(groupStart).Take(groupEnd - groupStart + 1).ToList();
        var fallbackRect = groupLines[0].Rect;
        // WHY: Synthetic fallback still represents the same VisionLLM content, so line boundaries
        // should survive here. OverlayStage remains responsible for any later clamping or collapsing.
        var mergedText = string.Join(Environment.NewLine, groupLines.Select(line => line.Text.Trim()).Where(text => !string.IsNullOrWhiteSpace(text)));
        var confidence = groupLines.Max(line => line.Confidence);
        var lineCount = groupLines.Sum(line => Math.Max(1, line.LineCount));
        var syntheticRect = BuildReadableSyntheticRect(
            mergedText,
            lineCount,
            previous,
            next,
            occupiedRects,
            fallbackRect,
            imageWidth,
            imageHeight);
        return new OcrLine(mergedText, syntheticRect, confidence, lineCount, syntheticRect.Height / Math.Max(1, lineCount));
    }

    private static bool TryFindMergeTargetVisionIndex(
        Rect candidateRect,
        IReadOnlyList<MatchedSlot> matchedSlots,
        out int visionIndex)
    {
        visionIndex = -1;
        if (matchedSlots.Count == 0)
        {
            return false;
        }

        var bestOverlap = 0.0;
        var bestOverlapVisionIndex = -1;
        foreach (var slot in matchedSlots)
        {
            var overlap = ComputePairOverlapPenalty(candidateRect, slot.Rect);
            if (overlap <= bestOverlap)
            {
                continue;
            }

            bestOverlap = overlap;
            bestOverlapVisionIndex = slot.VisionIndex;
        }

        if (bestOverlapVisionIndex >= 0 && bestOverlap >= SyntheticMergeOverlapMin)
        {
            visionIndex = bestOverlapVisionIndex;
            return true;
        }

        var candidateCenterX = candidateRect.Left + (candidateRect.Width / 2.0);
        var candidateCenterY = candidateRect.Top + (candidateRect.Height / 2.0);
        var bestDistance = double.MaxValue;
        var bestDistanceVisionIndex = -1;
        foreach (var slot in matchedSlots)
        {
            var slotCenterX = slot.Rect.Left + (slot.Rect.Width / 2.0);
            var slotCenterY = slot.Rect.Top + (slot.Rect.Height / 2.0);
            var distance = Math.Sqrt(Math.Pow(candidateCenterX - slotCenterX, 2.0) + Math.Pow(candidateCenterY - slotCenterY, 2.0));
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestDistanceVisionIndex = slot.VisionIndex;
        }

        if (bestDistanceVisionIndex < 0)
        {
            return false;
        }

        var maxAllowedDistance = Math.Max(candidateRect.Width, candidateRect.Height) * SyntheticMergeMaxDistanceMultiplier;
        if (bestDistance > maxAllowedDistance)
        {
            return false;
        }

        visionIndex = bestDistanceVisionIndex;
        return true;
    }

    private static Rect BuildReadableSyntheticRect(
        string text,
        int lineCount,
        MatchedSlot? previous,
        MatchedSlot? next,
        IReadOnlyList<Rect> occupiedRects,
        Rect fallbackRect,
        int imageWidth,
        int imageHeight)
    {
        var normalizedText = string.IsNullOrWhiteSpace(text) ? "..." : text;
        var charCount = Math.Max(1, normalizedText.Length);
        var approxLineHeight = Math.Clamp(imageHeight * 0.055, 24.0, 54.0);
        var preferredWidthRatio = Math.Clamp(0.34 + (charCount / 220.0), 0.34, 0.72);
        var targetWidth = Math.Clamp(imageWidth * preferredWidthRatio, 180.0, imageWidth * 0.80);
        var charsPerLine = Math.Max(8, (int)Math.Floor(targetWidth / Math.Max(10.0, approxLineHeight * 0.62)));
        var estimatedLineCount = Math.Max(1, (int)Math.Ceiling(charCount / (double)charsPerLine));
        estimatedLineCount = Math.Max(estimatedLineCount, Math.Max(1, lineCount));
        var targetHeight = Math.Clamp((estimatedLineCount * approxLineHeight * 1.28) + 16.0, 40.0, imageHeight * 0.60);

        var preferredAnchor = previous?.Rect ?? next?.Rect ?? fallbackRect;
        var preferredRect = ClampRect(new Rect(preferredAnchor.X, preferredAnchor.Y, targetWidth, targetHeight), imageWidth, imageHeight);
        return ResolveReadablePlacement(preferredRect, occupiedRects, imageWidth, imageHeight);
    }

    private static Rect ResolveReadablePlacement(
        Rect preferredRect,
        IReadOnlyList<Rect> occupiedRects,
        int imageWidth,
        int imageHeight)
    {
        if (occupiedRects.Count == 0)
        {
            return preferredRect;
        }

        var candidates = BuildReadablePlacementCandidates(preferredRect, occupiedRects, imageWidth, imageHeight);

        Rect bestRect = preferredRect;
        var bestPenalty = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var penalty = ComputeOverlapPenalty(candidate, occupiedRects);
            if (penalty < 0.12)
            {
                return candidate;
            }

            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestRect = candidate;
            }
        }

        return bestRect;
    }

    private static List<Rect> BuildReadablePlacementCandidates(
        Rect preferredRect,
        IReadOnlyList<Rect> occupiedRects,
        int imageWidth,
        int imageHeight)
    {
        var margin = Math.Max(10.0, Math.Min(imageWidth, imageHeight) * 0.02);
        var candidates = new List<Rect>();
        var union = BuildUnionRect(occupiedRects);

        candidates.Add(preferredRect);

        if (union is not null)
        {
            candidates.Add(ClampRect(new Rect(union.Value.Left, union.Value.Bottom + margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
            candidates.Add(ClampRect(new Rect(union.Value.Right - preferredRect.Width, union.Value.Bottom + margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
            candidates.Add(ClampRect(new Rect(union.Value.Left, union.Value.Top - preferredRect.Height - margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
            candidates.Add(ClampRect(new Rect(union.Value.Right - preferredRect.Width, union.Value.Top - preferredRect.Height - margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
            candidates.Add(ClampRect(new Rect(union.Value.Right + margin, union.Value.Top, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
            candidates.Add(ClampRect(new Rect(union.Value.Left - preferredRect.Width - margin, union.Value.Top, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
        }

        candidates.Add(ClampRect(new Rect(margin, imageHeight - preferredRect.Height - margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
        candidates.Add(ClampRect(new Rect(imageWidth - preferredRect.Width - margin, imageHeight - preferredRect.Height - margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
        candidates.Add(ClampRect(new Rect(margin, margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
        candidates.Add(ClampRect(new Rect(imageWidth - preferredRect.Width - margin, margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
        candidates.Add(ClampRect(new Rect((imageWidth - preferredRect.Width) / 2.0, imageHeight - preferredRect.Height - margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));
        candidates.Add(ClampRect(new Rect((imageWidth - preferredRect.Width) / 2.0, margin, preferredRect.Width, preferredRect.Height), imageWidth, imageHeight));

        var stepX = Math.Max(24.0, preferredRect.Width * 0.35);
        var stepY = Math.Max(24.0, preferredRect.Height * 0.35);
        for (var y = margin; y <= imageHeight - preferredRect.Height - margin; y += stepY)
        {
            for (var x = margin; x <= imageWidth - preferredRect.Width - margin; x += stepX)
            {
                candidates.Add(new Rect(x, y, preferredRect.Width, preferredRect.Height));
            }
        }

        return candidates;
    }

    private static Rect? BuildUnionRect(IReadOnlyList<Rect> rects)
    {
        if (rects.Count == 0)
        {
            return null;
        }

        var left = rects.Min(rect => rect.Left);
        var top = rects.Min(rect => rect.Top);
        var right = rects.Max(rect => rect.Right);
        var bottom = rects.Max(rect => rect.Bottom);
        return new Rect(left, top, Math.Max(1.0, right - left), Math.Max(1.0, bottom - top));
    }

    private static double ComputeOverlapPenalty(Rect candidate, IReadOnlyList<Rect> occupiedRects)
    {
        var candidateArea = Math.Max(1.0, candidate.Width * candidate.Height);
        var worstPenalty = 0.0;
        foreach (var occupied in occupiedRects)
        {
            var left = Math.Max(candidate.Left, occupied.Left);
            var top = Math.Max(candidate.Top, occupied.Top);
            var right = Math.Min(candidate.Right, occupied.Right);
            var bottom = Math.Min(candidate.Bottom, occupied.Bottom);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            var intersectionArea = (right - left) * (bottom - top);
            var occupiedArea = Math.Max(1.0, occupied.Width * occupied.Height);
            var penalty = intersectionArea / Math.Min(candidateArea, occupiedArea);
            if (penalty > worstPenalty)
            {
                worstPenalty = penalty;
            }
        }

        return worstPenalty;
    }

    private static double ComputePairOverlapPenalty(Rect leftRect, Rect rightRect)
    {
        var left = Math.Max(leftRect.Left, rightRect.Left);
        var top = Math.Max(leftRect.Top, rightRect.Top);
        var right = Math.Min(leftRect.Right, rightRect.Right);
        var bottom = Math.Min(leftRect.Bottom, rightRect.Bottom);
        if (right <= left || bottom <= top)
        {
            return 0.0;
        }

        var intersectionArea = (right - left) * (bottom - top);
        var leftArea = Math.Max(1.0, leftRect.Width * leftRect.Height);
        var rightArea = Math.Max(1.0, rightRect.Width * rightRect.Height);
        return intersectionArea / Math.Min(leftArea, rightArea);
    }

    private static Rect ClampRect(Rect rect, int imageWidth, int imageHeight)
    {
        var width = Math.Max(1.0, Math.Min(rect.Width, imageWidth));
        var height = Math.Max(1.0, Math.Min(rect.Height, imageHeight));
        var x = Math.Max(0.0, Math.Min(rect.X, Math.Max(0.0, imageWidth - width)));
        var y = Math.Max(0.0, Math.Min(rect.Y, Math.Max(0.0, imageHeight - height)));
        return new Rect(x, y, width, height);
    }

    private static double Lerp(double start, double end, double ratio)
    {
        return start + ((end - start) * ratio);
    }

    private static NormalizedProjection BuildNormalizedProjection(string rawText)
    {
        var normalizedBuilder = new System.Text.StringBuilder(rawText.Length);
        var rawIndexes = new List<int>(rawText.Length);
        var lastWasSpace = false;
        for (var rawIndex = 0; rawIndex < rawText.Length; rawIndex++)
        {
            var normalizedChunk = rawText[rawIndex].ToString().Normalize(System.Text.NormalizationForm.FormKC);
            foreach (var normalizedChar in normalizedChunk)
            {
                if (char.IsWhiteSpace(normalizedChar))
                {
                    if (!lastWasSpace)
                    {
                        normalizedBuilder.Append(' ');
                        rawIndexes.Add(rawIndex);
                        lastWasSpace = true;
                    }

                    continue;
                }

                lastWasSpace = false;
                if (char.IsPunctuation(normalizedChar) || char.IsSymbol(normalizedChar))
                {
                    continue;
                }

                normalizedBuilder.Append(char.ToLowerInvariant(normalizedChar));
                rawIndexes.Add(rawIndex);
            }
        }

        var normalized = normalizedBuilder.ToString();
        var trimStart = 0;
        while (trimStart < normalized.Length && normalized[trimStart] == ' ')
        {
            trimStart++;
        }

        var trimEnd = normalized.Length;
        while (trimEnd > trimStart && normalized[trimEnd - 1] == ' ')
        {
            trimEnd--;
        }

        if (trimStart > 0 || trimEnd < normalized.Length)
        {
            normalized = normalized[trimStart..trimEnd];
            rawIndexes = rawIndexes.Skip(trimStart).Take(trimEnd - trimStart).ToList();
        }

        return new NormalizedProjection(rawText, normalized, rawIndexes);
    }

    private sealed record GeometryCandidate(OcrLine Line, int Index, string NormalizedText);

    private sealed record MatchedSlot(int VisionIndex, Rect Rect);

    private sealed record VisionTextContribution(int VisionIndex, string Text);

    private sealed record GeometryRunCandidate(
        IReadOnlyList<GeometryCandidate> Segments,
        string NormalizedText,
        string RawText);

    private sealed class NormalizedProjection(string rawText, string normalizedText, IReadOnlyList<int> normalizedCharRawIndexes)
    {
        public string RawText { get; } = rawText;
        public string NormalizedText { get; } = normalizedText;

        public string ExtractRawSegment(int start, int end)
        {
            if (start < 0 || end > NormalizedText.Length || end <= start || start >= normalizedCharRawIndexes.Count)
            {
                return string.Empty;
            }

            var rawStart = normalizedCharRawIndexes[start];
            var rawEnd = end < normalizedCharRawIndexes.Count ? normalizedCharRawIndexes[end] : RawText.Length;
            rawStart = Math.Clamp(rawStart, 0, RawText.Length);
            rawEnd = Math.Clamp(rawEnd, rawStart, RawText.Length);
            return RawText[rawStart..rawEnd].Trim();
        }
    }

    private sealed class ScriptProfile
    {
        public int Latin { get; set; }
        public int Digit { get; set; }
        public int Cjk { get; set; }
    }
}

public sealed record HybridOcrAlignmentResult(
    IReadOnlyList<OcrLine> Lines,
    int GeometryLineCount,
    int VisionLineCount,
    int MatchedCount,
    int SyntheticFallbackCount);

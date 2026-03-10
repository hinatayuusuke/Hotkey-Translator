using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class VisionGeometryHybridAligner
{
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
                matchedOutputs[visionIndex] = visionLine with
                {
                    Rect = best.Line.Rect,
                    Confidence = Math.Max(visionLine.Confidence, best.Line.Confidence),
                    LineHeight = best.Line.Rect.Height,
                    LineCount = Math.Max(visionLine.LineCount, best.Line.LineCount)
                };
            }
        }

        var matchedSlots = matchedOutputs
            .Select((line, index) => line is null ? null : new MatchedSlot(index, line.Rect))
            .Where(slot => slot is not null)
            .Select(slot => slot!)
            .ToList();
        var lines = new List<OcrLine>(visionLines.Count);
        var syntheticCount = 0;

        for (var visionIndex = 0; visionIndex < visionLines.Count;)
        {
            var matchedLine = matchedOutputs[visionIndex];
            if (matchedLine is not null)
            {
                lines.Add(matchedLine);
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

            lines.Add(BuildSyntheticLineForGroup(
                visionLines,
                groupStart,
                groupEnd,
                matchedSlots,
                geometryLines,
                imageWidth,
                imageHeight));
            syntheticCount++;
            visionIndex = groupEnd + 1;
        }

        _logger?.Info(
            $"stage=vision_geometry_hybrid event=summary geometryLines={geometryLines.Count} visionLines={visionLines.Count} matched={matchedCount} synthetic={syntheticCount} output={lines.Count}.");
        return new HybridOcrAlignmentResult(lines, geometryLines.Count, visionLines.Count, matchedCount, syntheticCount);
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
        IReadOnlyList<OcrLine> geometryLines,
        int imageWidth,
        int imageHeight)
    {
        var previous = matchedSlots.Where(slot => slot.VisionIndex < groupStart).OrderBy(slot => slot.VisionIndex).LastOrDefault();
        var next = matchedSlots.Where(slot => slot.VisionIndex > groupEnd).OrderBy(slot => slot.VisionIndex).FirstOrDefault();
        var groupLines = visionLines.Skip(groupStart).Take(groupEnd - groupStart + 1).ToList();
        var fallbackRect = groupLines[0].Rect;
        var mergedText = string.Join("\n", groupLines.Select(line => line.Text));
        var confidence = groupLines.Max(line => line.Confidence);
        var lineCount = groupLines.Sum(line => Math.Max(1, line.LineCount));
        var baseHeight = Math.Max(
            groupLines.Max(line => Math.Max(1.0, line.LineHeight > 0 ? line.LineHeight : line.Rect.Height)),
            Math.Max(1.0, fallbackRect.Height));
        var targetHeight = Math.Max(baseHeight, baseHeight * lineCount);

        Rect syntheticRect;

        if (previous != default && next != default)
        {
            syntheticRect = InterpolateBetweenGroup(
                previous.Rect,
                next.Rect,
                previous.VisionIndex,
                next.VisionIndex,
                groupStart,
                groupEnd,
                targetHeight,
                imageWidth,
                imageHeight);
        }
        else if (previous != default)
        {
            syntheticRect = AttachAfterGroup(previous.Rect, targetHeight, imageWidth, imageHeight);
        }
        else if (next != default)
        {
            syntheticRect = AttachBeforeGroup(next.Rect, targetHeight, imageWidth, imageHeight);
        }
        else if (geometryLines.Count > 0)
        {
            var anchor = geometryLines[Math.Min(groupStart, geometryLines.Count - 1)].Rect;
            syntheticRect = ClampRect(new Rect(anchor.X, anchor.Y, anchor.Width, targetHeight), imageWidth, imageHeight);
        }
        else
        {
            syntheticRect = ClampRect(new Rect(fallbackRect.X, fallbackRect.Y, fallbackRect.Width, targetHeight), imageWidth, imageHeight);
        }

        return new OcrLine(mergedText, syntheticRect, confidence, lineCount, syntheticRect.Height / Math.Max(1, lineCount));
    }

    private static Rect InterpolateBetweenGroup(
        Rect previous,
        Rect next,
        int previousIndex,
        int nextIndex,
        int groupStart,
        int groupEnd,
        double targetHeight,
        int imageWidth,
        int imageHeight)
    {
        var span = Math.Max(1, nextIndex - previousIndex);
        var centerIndex = (groupStart + groupEnd) / 2.0;
        var ratio = (centerIndex - previousIndex) / span;
        var x = Lerp(previous.X, next.X, ratio);
        var width = Lerp(previous.Width, next.Width, ratio);
        var height = Math.Max(targetHeight, Lerp(previous.Height, next.Height, ratio));
        var previousBottom = previous.Y + previous.Height;
        var nextTop = next.Y;
        var y = previousBottom + ((nextTop - previousBottom - height) * ratio);
        return ClampRect(new Rect(x, y, width, height), imageWidth, imageHeight);
    }

    private static Rect AttachAfterGroup(Rect anchor, double targetHeight, int imageWidth, int imageHeight)
    {
        var gap = Math.Max(2.0, anchor.Height * 0.20);
        return ClampRect(new Rect(anchor.X, anchor.Y + anchor.Height + gap, anchor.Width, targetHeight), imageWidth, imageHeight);
    }

    private static Rect AttachBeforeGroup(Rect anchor, double targetHeight, int imageWidth, int imageHeight)
    {
        var gap = Math.Max(2.0, anchor.Height * 0.20);
        return ClampRect(new Rect(anchor.X, anchor.Y - targetHeight - gap, anchor.Width, targetHeight), imageWidth, imageHeight);
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

    private sealed record GeometryCandidate(OcrLine Line, int Index, string NormalizedText);

    private sealed record MatchedSlot(int VisionIndex, Rect Rect);

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

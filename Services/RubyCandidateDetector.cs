using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed record RubyDetectionResult(
    IReadOnlyList<OcrLine> BodyLines,
    IReadOnlyList<OcrLine> RubyLines,
    IReadOnlyDictionary<int, IReadOnlyList<int>> RubyIndicesByBodyIndex,
    bool IsVerticalWriting);

public sealed class RubyCandidateDetector
{
    private const double HorizontalOverlapRatioMin = 0.55;
    private const double VerticalOverlapRatioMin = 0.55;
    private const double HorizontalRubyGapRatioMax = 0.90;
    private const double VerticalRubyGapRatioMax = 0.90;
    private const double RubyShortSideRatioMax = 0.72;
    private const double RubyAreaRatioMax = 0.60;
    private const double VerticalAutoAspectThreshold = 1.15;
    private const double VerticalAutoRatioThreshold = 0.60;
    private const int RubyTextLengthMax = 10;
    private const double RubyKanaRatioMin = 0.60;

    private enum WritingMode
    {
        Horizontal,
        Vertical
    }

    public RubyDetectionResult Detect(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        if (lines.Count <= 1 || !ShouldDetectForSettings(settings))
        {
            return CreatePassthrough(lines, isVerticalWriting: false);
        }

        var writingMode = ResolveWritingMode(lines, settings);
        var rubyByBodyIndex = new Dictionary<int, List<int>>();
        var rubyIndices = new HashSet<int>();

        for (var i = 0; i < lines.Count; i++)
        {
            if (TryFindRubyAnchor(lines, i, writingMode, out var bodyIndex))
            {
                rubyIndices.Add(i);
                if (!rubyByBodyIndex.TryGetValue(bodyIndex, out var rubyList))
                {
                    rubyList = new List<int>();
                    rubyByBodyIndex[bodyIndex] = rubyList;
                }

                rubyList.Add(i);
            }
        }

        if (rubyIndices.Count == 0 || rubyIndices.Count >= lines.Count)
        {
            return CreatePassthrough(lines, writingMode == WritingMode.Vertical);
        }

        var bodyLines = new List<OcrLine>(lines.Count - rubyIndices.Count);
        var rubyLines = new List<OcrLine>(rubyIndices.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (rubyIndices.Contains(i))
            {
                rubyLines.Add(lines[i]);
            }
            else
            {
                bodyLines.Add(lines[i]);
            }
        }

        if (bodyLines.Count == 0 || bodyLines.Count < rubyLines.Count)
        {
            return CreatePassthrough(lines, writingMode == WritingMode.Vertical);
        }

        return new RubyDetectionResult(
            bodyLines,
            rubyLines,
            rubyByBodyIndex.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<int>)pair.Value.OrderBy(index => index).ToList()),
            writingMode == WritingMode.Vertical);
    }

    private static RubyDetectionResult CreatePassthrough(IReadOnlyList<OcrLine> lines, bool isVerticalWriting)
    {
        return new RubyDetectionResult(
            lines.ToList(),
            Array.Empty<OcrLine>(),
            new Dictionary<int, IReadOnlyList<int>>(),
            isVerticalWriting);
    }

    private static bool ShouldDetectForSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SourceLanguage))
        {
            return false;
        }

        var normalized = settings.SourceLanguage.Trim().Replace('_', '-');
        // WHY: Ruby is a Japanese-specific concern here; applying the same heuristic broadly
        // would misclassify small UI labels and annotations in other scripts.
        return normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
    }

    private static WritingMode ResolveWritingMode(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        if (settings.VerticalModeOverride == VerticalModeOverride.Vertical)
        {
            return WritingMode.Vertical;
        }

        if (settings.VerticalModeOverride == VerticalModeOverride.Horizontal)
        {
            return WritingMode.Horizontal;
        }

        var verticalLikeCount = 0;
        foreach (var line in lines)
        {
            if (line.Rect.Height <= 0 || line.Rect.Width <= 0)
            {
                continue;
            }

            var aspect = line.Rect.Height / Math.Max(1.0, line.Rect.Width);
            if (aspect >= VerticalAutoAspectThreshold)
            {
                verticalLikeCount++;
            }
        }

        if (lines.Count == 0)
        {
            return WritingMode.Horizontal;
        }

        return ((double)verticalLikeCount / lines.Count) >= VerticalAutoRatioThreshold
            ? WritingMode.Vertical
            : WritingMode.Horizontal;
    }

    private static bool TryFindRubyAnchor(
        IReadOnlyList<OcrLine> lines,
        int candidateIndex,
        WritingMode writingMode,
        out int bodyIndex)
    {
        bodyIndex = -1;
        var candidate = lines[candidateIndex];
        if (!IsRubyTextCandidate(candidate.Text) || candidate.Rect.Width <= 0 || candidate.Rect.Height <= 0)
        {
            return false;
        }

        var candidateShortSide = Math.Min(candidate.Rect.Width, candidate.Rect.Height);
        var candidateArea = candidate.Rect.Width * candidate.Rect.Height;
        var bestScore = double.MinValue;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == candidateIndex)
            {
                continue;
            }

            var body = lines[i];
            if (!ContainsKanji(body.Text) || body.Rect.Width <= 0 || body.Rect.Height <= 0)
            {
                continue;
            }

            var bodyShortSide = Math.Min(body.Rect.Width, body.Rect.Height);
            var bodyArea = body.Rect.Width * body.Rect.Height;
            if (bodyShortSide <= 0 || bodyArea <= 0)
            {
                continue;
            }

            if (candidateShortSide > bodyShortSide * RubyShortSideRatioMax ||
                candidateArea > bodyArea * RubyAreaRatioMax)
            {
                continue;
            }

            if (!TryScoreRubyPair(candidate.Rect, body.Rect, writingMode, out var score))
            {
                continue;
            }

            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bodyIndex = i;
        }

        return bodyIndex >= 0;
    }

    private static bool TryScoreRubyPair(Rect candidate, Rect body, WritingMode writingMode, out double score)
    {
        score = 0;
        if (writingMode == WritingMode.Vertical)
        {
            if (candidate.Left < body.Left || candidate.Left + (candidate.Width * 0.35) < body.Right)
            {
                return false;
            }

            var overlap = ComputeAxisOverlap(candidate.Top, candidate.Bottom, body.Top, body.Bottom);
            var overlapRatio = overlap / Math.Max(1.0, Math.Min(candidate.Height, body.Height));
            if (overlapRatio < VerticalOverlapRatioMin)
            {
                return false;
            }

            var gap = Math.Max(0.0, candidate.Left - body.Right);
            var gapLimit = Math.Max(candidate.Width, body.Width) * VerticalRubyGapRatioMax;
            if (gap > gapLimit)
            {
                return false;
            }

            score = overlapRatio - (gap / Math.Max(1.0, gapLimit));
            return true;
        }

        if (candidate.Top > body.Top || candidate.Top + (candidate.Height * 0.35) > body.Bottom)
        {
            return false;
        }

        var horizontalOverlap = ComputeAxisOverlap(candidate.Left, candidate.Right, body.Left, body.Right);
        var horizontalOverlapRatio = horizontalOverlap / Math.Max(1.0, Math.Min(candidate.Width, body.Width));
        if (horizontalOverlapRatio < HorizontalOverlapRatioMin)
        {
            return false;
        }

        var verticalGap = Math.Max(0.0, body.Top - candidate.Bottom);
        var gapLimitHorizontal = Math.Max(candidate.Height, body.Height) * HorizontalRubyGapRatioMax;
        if (verticalGap > gapLimitHorizontal)
        {
            return false;
        }

        score = horizontalOverlapRatio - (verticalGap / Math.Max(1.0, gapLimitHorizontal));
        return true;
    }

    private static double ComputeAxisOverlap(double startA, double endA, double startB, double endB)
    {
        return Math.Max(0.0, Math.Min(endA, endB) - Math.Max(startA, startB));
    }

    private static bool IsRubyTextCandidate(string? text)
    {
        var normalized = NormalizeText(text);
        if (normalized.Length == 0 || normalized.Length > RubyTextLengthMax)
        {
            return false;
        }

        var kanaCount = 0;
        foreach (var ch in normalized)
        {
            if (IsKana(ch))
            {
                kanaCount++;
            }
            else if (!char.IsPunctuation(ch) && !char.IsDigit(ch) && !char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        if (kanaCount == 0)
        {
            return false;
        }

        return ((double)kanaCount / normalized.Length) >= RubyKanaRatioMin;
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    private static bool ContainsKanji(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (IsKanji(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKana(char ch)
    {
        return (ch >= '\u3040' && ch <= '\u309F') ||
               (ch >= '\u30A0' && ch <= '\u30FF') ||
               ch == '\u30FC';
    }

    private static bool IsKanji(char ch)
    {
        return (ch >= '\u3400' && ch <= '\u4DBF') ||
               (ch >= '\u4E00' && ch <= '\u9FFF') ||
               (ch >= '\uF900' && ch <= '\uFAFF');
    }
}

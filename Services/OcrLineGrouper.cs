using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OcrLineGrouper
{
    private const double ScoreWeightSingleChar = 0.35;
    private const double ScoreWeightAbnormalSpace = 0.25;
    private const double ScoreWeightRectVariance = 0.25;
    private const double ScoreWeightBlockAspect = 0.15;
    private const double AutoModeScoreEpsilon = 0.03;
    private const double AspectFilterMinArea = 16.0;
    private const double AspectFilterMedianAreaRatio = 0.15;
    private const double VerticalColumnCenterToleranceRatio = 0.55;
    private const double VerticalColumnWidthRatioMin = 0.55;
    private const double VerticalColumnOverlapRatioMin = 0.10;
    private const double VerticalHardBreakMultiplier = 1.5;
    private const double OverlapClampMin = 0.05;
    private const double OverlapClampMax = 0.95;
    private const double RatioClampMin = 0.10;
    private const double RatioClampMax = 5.0;
    private const double WeightClampMin = 0.0;
    private const double WeightClampMax = 5.0;
    private const double SimpleHorizontalOverlapMin = 0.05;
    private const double SimpleHorizontalOverlapMax = 0.20;
    private const double SimpleHorizontalThresholdMin = 0.75;
    private const double SimpleHorizontalThresholdMax = 1.20;
    private const double SimpleHorizontalRowMaxGapMin = 0.90;
    private const double SimpleHorizontalRowMaxGapMax = 2.40;
    private const double SimpleHorizontalRowHardBreakMin = 1.20;
    private const double SimpleHorizontalRowHardBreakMax = 3.00;
    private const double SimpleVerticalGapMin = 0.80;
    private const double SimpleVerticalGapMax = 2.00;
    private const double SimpleVerticalColumnOverlapMin = 0.08;
    private const double SimpleVerticalColumnOverlapMax = 0.35;
    private const double SimpleVerticalColumnThresholdMin = 0.70;
    private const double SimpleVerticalColumnThresholdMax = 1.30;
    private const double SimpleVerticalColumnHardBreakMin = 1.10;
    private const double SimpleVerticalColumnHardBreakMax = 2.60;
    private const double SimpleVerticalStageACenterToleranceMin = 0.40;
    private const double SimpleVerticalStageACenterToleranceMax = 0.85;
    private const double SimpleVerticalStageAWidthRatioMinMin = 0.35;
    private const double SimpleVerticalStageAWidthRatioMinMax = 0.80;
    private const double SimpleVerticalStageAOverlapRatioMinMin = 0.05;
    private const double SimpleVerticalStageAOverlapRatioMinMax = 0.30;
    private readonly AppLogger? _logger;
    private WritingMode _lastAutoSelectedMode = WritingMode.Horizontal;
    private bool _hasAutoSelectedMode;
    private string? _lastLoggedProfileKey;
    private string? _lastLoggedSimpleTuningKey;

    private enum WritingMode
    {
        Horizontal,
        Vertical
    }

    private readonly record struct DirectionScore(
        double SingleChar,
        double AbnormalSpace,
        double RectVariance,
        double BlockAspect,
        double Total);

    private readonly record struct EffectiveMergeThresholds(
        double MergeOverlapRatioThreshold,
        double MergeVerticalWeight,
        double MergeThresholdRatio,
        double RowMergeYCenterToleranceRatio,
        double RowMergeHeightRatioMin,
        double RowMergeMaxGapRatio,
        double RowMergeHardBreakRatio,
        double VerticalGapRatio,
        double VerticalStageACenterToleranceRatio,
        double VerticalStageAWidthRatioMin,
        double VerticalStageAOverlapRatioMin,
        double VerticalColumnMergeOverlapRatioThreshold,
        double VerticalColumnMergeWeight,
        double VerticalColumnMergeThresholdRatio,
        double VerticalColumnMergeHardBreakRatio,
        bool EngineScaledApplied,
        OcrEngineKind EngineKind);

    public OcrLineGrouper(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<OcrLine> MergeLines(IReadOnlyList<OcrLine> lines, AppSettings settings, OcrEngineKind? effectiveEngineOverride = null)
    {
        if (!settings.EnableLineMerge || lines.Count <= 1)
        {
            return lines;
        }

        var thresholds = ResolveEffectiveThresholds(settings, effectiveEngineOverride);
        LogEffectiveThresholdsIfChanged(thresholds, settings);
        LogSimpleMergeTuningIfChanged(thresholds, settings);

        if (settings.VerticalModeOverride == VerticalModeOverride.Horizontal)
        {
            return MergeAsHorizontal(lines, settings, thresholds);
        }

        if (settings.VerticalModeOverride == VerticalModeOverride.Vertical)
        {
            return MergeAsVertical(lines, settings, thresholds);
        }

        if (!ShouldUseBidirectionalScoring(settings))
        {
            // WHY: Outside scoped auto-detect languages, keep behavior deterministic and cheap with horizontal mode.
            _hasAutoSelectedMode = false;
            return MergeAsHorizontal(lines, settings, thresholds);
        }

        var horizontalMerged = MergeAsHorizontal(lines, settings, thresholds);
        var verticalMerged = MergeAsVertical(lines, settings, thresholds);
        var horizontalScore = ScoreMergedResult(horizontalMerged, WritingMode.Horizontal);
        var verticalScore = ScoreMergedResult(verticalMerged, WritingMode.Vertical);
        var selectedMode = SelectAutoMode(horizontalScore.Total, verticalScore.Total);

        _logger?.Info(
            $"OCR writing mode scored: H={horizontalScore.Total:0.000} " +
            $"(single={horizontalScore.SingleChar:0.000}, space={horizontalScore.AbnormalSpace:0.000}, rect={horizontalScore.RectVariance:0.000}, aspect={horizontalScore.BlockAspect:0.000}) " +
            $"V={verticalScore.Total:0.000} " +
            $"(single={verticalScore.SingleChar:0.000}, space={verticalScore.AbnormalSpace:0.000}, rect={verticalScore.RectVariance:0.000}, aspect={verticalScore.BlockAspect:0.000}) " +
            $"selected={selectedMode}.");

        return selectedMode == WritingMode.Vertical ? verticalMerged : horizontalMerged;
    }

    private static IReadOnlyList<OcrLine> MergeAsHorizontal(
        IReadOnlyList<OcrLine> lines,
        AppSettings settings,
        EffectiveMergeThresholds thresholds)
    {
        if (!settings.EnableTwoStageLineMerge)
        {
            var ordered = OrderLines(lines, WritingMode.Horizontal, settings);
            return MergeHorizontalLines(ordered, settings, thresholds);
        }

        var horizontalOrdered = OrderLines(lines, WritingMode.Horizontal, settings);
        var stageAResult = MergeSameRowTokens(horizontalOrdered, settings, thresholds);
        return MergeHorizontalLines(stageAResult, settings, thresholds);
    }

    private IReadOnlyList<OcrLine> MergeAsVertical(
        IReadOnlyList<OcrLine> lines,
        AppSettings settings,
        EffectiveMergeThresholds thresholds)
    {
        if (!settings.EnableTwoStageLineMerge)
        {
            return OrderLines(lines, WritingMode.Vertical, settings);
        }

        return MergeVerticalLinesTwoStage(lines, settings, thresholds);
    }

    private bool ShouldUseBidirectionalScoring(AppSettings settings)
    {
        if (!settings.EnableVerticalMerge || !settings.VerticalModeAutoDetect)
        {
            return false;
        }

        // WHY: Restrict bidirectional scoring to CJK auto mode to avoid unnecessary overhead on other scripts.
        return ShouldEnableVerticalDetectionForLanguage(settings.SourceLanguage);
    }

    private WritingMode SelectAutoMode(double horizontalScore, double verticalScore)
    {
        var delta = verticalScore - horizontalScore;
        WritingMode selected;
        if (Math.Abs(delta) < AutoModeScoreEpsilon)
        {
            // WHY: Preserve prior mode when confidence is ambiguous to reduce frame-to-frame direction flapping.
            selected = _hasAutoSelectedMode ? _lastAutoSelectedMode : WritingMode.Horizontal;
        }
        else
        {
            selected = delta > 0 ? WritingMode.Vertical : WritingMode.Horizontal;
        }

        _lastAutoSelectedMode = selected;
        _hasAutoSelectedMode = true;
        return selected;
    }

    private static bool ShouldEnableVerticalDetectionForLanguage(string? sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return false;
        }

        var normalized = sourceLanguage.Trim().Replace('_', '-');
        return normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<OcrLine> MergeSameRowTokens(
        IReadOnlyList<OcrLine> lines,
        AppSettings settings,
        EffectiveMergeThresholds thresholds)
    {
        if (lines.Count <= 1)
        {
            return lines;
        }

        var rowClusters = BuildRowClusters(lines, settings, thresholds);
        var rowGroups = BuildGroups(lines, rowClusters);
        var merged = new List<OcrLine>(lines.Count);
        foreach (var rowGroup in rowGroups)
        {
            var orderedRow = rowGroup
                .OrderBy(line => line.Rect.X)
                .ThenBy(line => line.Rect.Y)
                .ToList();

            if (orderedRow.Count == 1)
            {
                merged.Add(BuildMergedLineFromGroup(orderedRow, " ", forceLineCountOne: true, sortByXThenY: true));
                continue;
            }

            var adjacencyUnion = new UnionFind(orderedRow.Count);
            for (var i = 0; i < orderedRow.Count - 1; i++)
            {
                if (ShouldMergeAdjacentTokens(orderedRow[i].Rect, orderedRow[i + 1].Rect, thresholds))
                {
                    adjacencyUnion.Union(i, i + 1);
                }
            }

            var adjacencyGroups = BuildGroups(orderedRow, adjacencyUnion);
            foreach (var group in adjacencyGroups)
            {
                // WHY: Stage A merges tokens in the same visual row, so downstream must treat the result as a single line.
                merged.Add(BuildMergedLineFromGroup(group, " ", forceLineCountOne: true, sortByXThenY: true));
            }
        }

        return OrderLines(merged, WritingMode.Horizontal, settings);
    }

    private static IReadOnlyList<OcrLine> MergeHorizontalLines(
        IReadOnlyList<OcrLine> lines,
        AppSettings settings,
        EffectiveMergeThresholds thresholds)
    {
        if (lines.Count <= 1)
        {
            return lines;
        }

        var ordered = OrderLines(lines, WritingMode.Horizontal, settings);
        var unionFind = new UnionFind(ordered.Count);
        var neighborCount = Math.Max(1, settings.MergeNeighborCount);

        for (var i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            for (var offset = 1; offset <= neighborCount && i + offset < ordered.Count; offset++)
            {
                var b = ordered[i + offset];
                if (!PassesAlignmentGate(a.Rect, b.Rect, thresholds.MergeOverlapRatioThreshold))
                {
                    continue;
                }

                if (!IsMergeableByCost(a.Rect, b.Rect, thresholds))
                {
                    continue;
                }

                unionFind.Union(i, i + offset);
            }
        }

        var groups = new Dictionary<int, List<OcrLine>>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var root = unionFind.Find(i);
            if (!groups.TryGetValue(root, out var group))
            {
                group = new List<OcrLine>();
                groups[root] = group;
            }

            group.Add(ordered[i]);
        }

        var merged = new List<OcrLine>(groups.Count);
        foreach (var group in groups.Values)
        {
            var sortedGroup = group
                .OrderBy(line => line.Rect.Y)
                .ThenBy(line => line.Rect.X)
                .ToList();

            merged.Add(BuildMergedLineFromGroup(sortedGroup, Environment.NewLine, forceLineCountOne: false, sortByXThenY: false));
        }

        return OrderLines(merged, WritingMode.Horizontal, settings);
    }

    private IReadOnlyList<OcrLine> MergeVerticalLinesTwoStage(
        IReadOnlyList<OcrLine> lines,
        AppSettings settings,
        EffectiveMergeThresholds thresholds)
    {
        if (lines.Count <= 1)
        {
            return OrderLines(lines, WritingMode.Vertical, settings);
        }

        var ordered = OrderLines(lines, WritingMode.Vertical, settings);
        var columnClusters = BuildColumnClusters(ordered, settings, thresholds);
        var columnGroups = BuildColumnGroups(ordered, columnClusters, settings);
        var merged = new List<OcrLine>(ordered.Count);
        foreach (var columnGroup in columnGroups)
        {
            var orderedColumn = columnGroup
                .OrderBy(line => line.Rect.Y)
                .ThenBy(line => line.Rect.X)
                .ToList();

            if (orderedColumn.Count == 1)
            {
                merged.Add(BuildMergedLineFromGroup(orderedColumn, string.Empty, forceLineCountOne: true, sortByXThenY: false));
                continue;
            }

            var adjacencyUnion = new UnionFind(orderedColumn.Count);
            for (var i = 0; i < orderedColumn.Count - 1; i++)
            {
                if (ShouldMergeVerticalAdjacent(orderedColumn[i].Rect, orderedColumn[i + 1].Rect, thresholds))
                {
                    adjacencyUnion.Union(i, i + 1);
                }
            }

            var adjacencyGroups = BuildGroups(orderedColumn, adjacencyUnion);
            foreach (var group in adjacencyGroups)
            {
                // WHY: Vertical Stage A produces one merged token stream per visual column segment.
                merged.Add(BuildMergedLineFromGroup(group, string.Empty, forceLineCountOne: true, sortByXThenY: false));
            }
        }

        var stageAResult = OrderLines(merged, WritingMode.Vertical, settings);
        if (!settings.EnableVerticalColumnMerge || stageAResult.Count <= 1)
        {
            return stageAResult;
        }

        var stageBResult = MergeVerticalColumnUnits(stageAResult, settings, thresholds, out var mergedPairs, out var hardBreakSkips);
        _logger?.Info(
            $"Vertical column merge: before={stageAResult.Count}, after={stageBResult.Count}, mergedPairs={mergedPairs}, hardBreakSkips={hardBreakSkips}.");
        return OrderLines(stageBResult, WritingMode.Vertical, settings);
    }

    private static IReadOnlyList<OcrLine> MergeVerticalColumnUnits(
        IReadOnlyList<OcrLine> columnUnits,
        AppSettings settings,
        EffectiveMergeThresholds thresholds,
        out int mergedPairs,
        out int hardBreakSkips)
    {
        mergedPairs = 0;
        hardBreakSkips = 0;
        if (columnUnits.Count <= 1)
        {
            return columnUnits;
        }

        var orderedByX = columnUnits
            .OrderBy(line => line.Rect.X)
            .ThenBy(line => line.Rect.Y)
            .ToList();
        var unionFind = new UnionFind(orderedByX.Count);
        var neighborCount = Math.Max(1, settings.VerticalColumnMergeNeighborCount);
        var hardBreakRatio = Math.Max(0.1, thresholds.VerticalColumnMergeHardBreakRatio);

        for (var i = 0; i < orderedByX.Count; i++)
        {
            var a = orderedByX[i];
            for (var offset = 1; offset <= neighborCount && i + offset < orderedByX.Count; offset++)
            {
                var b = orderedByX[i + offset];
                if (!PassesVerticalAlignmentGate(a.Rect, b.Rect, thresholds.VerticalColumnMergeOverlapRatioThreshold))
                {
                    continue;
                }

                var minWidth = Math.Min(a.Rect.Width, b.Rect.Width);
                if (minWidth <= 0)
                {
                    continue;
                }

                var horizontalGap = Math.Max(0, b.Rect.Left - a.Rect.Right);
                if (horizontalGap > minWidth * hardBreakRatio)
                {
                    hardBreakSkips++;
                    continue;
                }

                if (!IsVerticalColumnMergeableByCost(a.Rect, b.Rect, horizontalGap, thresholds))
                {
                    continue;
                }

                if (unionFind.Union(i, i + offset))
                {
                    mergedPairs++;
                }
            }
        }

        var groups = BuildGroups(orderedByX, unionFind);
        var sortRightToLeft = settings.VerticalColumnOrder == VerticalColumnOrder.RightToLeft;
        var merged = new List<OcrLine>(groups.Count);
        foreach (var group in groups)
        {
            // WHY: Keep merged text order aligned with configured vertical column reading direction.
            merged.Add(BuildMergedLineFromGroup(
                group,
                string.Empty,
                forceLineCountOne: true,
                sortByXThenY: true,
                sortPrimaryDescending: sortRightToLeft));
        }

        return merged;
    }

    private static UnionFind BuildRowClusters(
        IReadOnlyList<OcrLine> lines,
        AppSettings settings,
        EffectiveMergeThresholds thresholds)
    {
        var unionFind = new UnionFind(lines.Count);
        var neighborCount = Math.Max(1, settings.RowMergeNeighborCount);
        for (var i = 0; i < lines.Count; i++)
        {
            var a = lines[i];
            for (var offset = 1; offset <= neighborCount && i + offset < lines.Count; offset++)
            {
                var b = lines[i + offset];
                if (!IsSameRowCandidate(a.Rect, b.Rect, thresholds))
                {
                    continue;
                }

                unionFind.Union(i, i + offset);
            }
        }

        return unionFind;
    }

    private static UnionFind BuildColumnClusters(
        IReadOnlyList<OcrLine> lines,
        AppSettings settings,
        EffectiveMergeThresholds thresholds)
    {
        var unionFind = new UnionFind(lines.Count);
        var neighborCount = Math.Max(1, settings.RowMergeNeighborCount);
        for (var i = 0; i < lines.Count; i++)
        {
            var a = lines[i];
            for (var offset = 1; offset <= neighborCount && i + offset < lines.Count; offset++)
            {
                var b = lines[i + offset];
                if (!IsSameColumnCandidate(a.Rect, b.Rect, thresholds))
                {
                    continue;
                }

                unionFind.Union(i, i + offset);
            }
        }

        return unionFind;
    }

    private static bool IsSameRowCandidate(Rect a, Rect b, EffectiveMergeThresholds thresholds)
    {
        var minHeight = Math.Min(a.Height, b.Height);
        var maxHeight = Math.Max(a.Height, b.Height);
        if (minHeight <= 0 || maxHeight <= 0)
        {
            return false;
        }

        var centerDiff = Math.Abs(GetCenterY(a) - GetCenterY(b));
        if (centerDiff > minHeight * thresholds.RowMergeYCenterToleranceRatio)
        {
            return false;
        }

        var heightRatio = minHeight / maxHeight;
        return heightRatio >= thresholds.RowMergeHeightRatioMin;
    }

    private static bool IsSameColumnCandidate(Rect a, Rect b, EffectiveMergeThresholds thresholds)
    {
        var minWidth = Math.Min(a.Width, b.Width);
        var maxWidth = Math.Max(a.Width, b.Width);
        if (minWidth <= 0 || maxWidth <= 0)
        {
            return false;
        }

        var centerDiff = Math.Abs(GetCenterX(a) - GetCenterX(b));
        if (centerDiff > minWidth * thresholds.VerticalStageACenterToleranceRatio)
        {
            return false;
        }

        var widthRatio = minWidth / maxWidth;
        if (widthRatio < thresholds.VerticalStageAWidthRatioMin)
        {
            return false;
        }

        var overlapWidth = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        return (overlapWidth / minWidth) >= thresholds.VerticalStageAOverlapRatioMin;
    }

    private static bool ShouldMergeAdjacentTokens(Rect left, Rect right, EffectiveMergeThresholds thresholds)
    {
        var minHeight = Math.Min(left.Height, right.Height);
        if (minHeight <= 0)
        {
            return false;
        }

        var gapX = Math.Max(0, right.Left - left.Right);
        if (gapX > minHeight * thresholds.RowMergeHardBreakRatio)
        {
            return false;
        }

        return gapX <= minHeight * thresholds.RowMergeMaxGapRatio;
    }

    private static bool ShouldMergeVerticalAdjacent(Rect top, Rect bottom, EffectiveMergeThresholds thresholds)
    {
        var minWidth = Math.Min(top.Width, bottom.Width);
        if (minWidth <= 0)
        {
            return false;
        }

        var gapRatio = Math.Max(0.1, thresholds.VerticalGapRatio);
        var gapY = Math.Max(0, bottom.Top - top.Bottom);
        if (gapY > minWidth * gapRatio * VerticalHardBreakMultiplier)
        {
            return false;
        }

        return gapY <= minWidth * gapRatio;
    }

    private static List<List<OcrLine>> BuildGroups(IReadOnlyList<OcrLine> lines, UnionFind unionFind)
    {
        var groups = new Dictionary<int, List<(int Index, OcrLine Line)>>();
        for (var i = 0; i < lines.Count; i++)
        {
            var root = unionFind.Find(i);
            if (!groups.TryGetValue(root, out var group))
            {
                group = new List<(int, OcrLine)>();
                groups[root] = group;
            }

            group.Add((i, lines[i]));
        }

        return groups.Values
            .Select(group => group.OrderBy(item => item.Index).Select(item => item.Line).ToList())
            .OrderBy(group => group[0].Rect.Y)
            .ThenBy(group => group[0].Rect.X)
            .ToList();
    }

    private static List<List<OcrLine>> BuildColumnGroups(IReadOnlyList<OcrLine> lines, UnionFind unionFind, AppSettings settings)
    {
        var groups = new Dictionary<int, List<(int Index, OcrLine Line)>>();
        for (var i = 0; i < lines.Count; i++)
        {
            var root = unionFind.Find(i);
            if (!groups.TryGetValue(root, out var group))
            {
                group = new List<(int, OcrLine)>();
                groups[root] = group;
            }

            group.Add((i, lines[i]));
        }

        var orderedGroups = groups.Values
            .Select(group => group.OrderBy(item => item.Index).Select(item => item.Line).ToList());

        if (settings.VerticalColumnOrder == VerticalColumnOrder.LeftToRight)
        {
            return orderedGroups
                .OrderBy(group => group[0].Rect.X)
                .ThenBy(group => group[0].Rect.Y)
                .ToList();
        }

        return orderedGroups
            .OrderByDescending(group => group[0].Rect.X)
            .ThenBy(group => group[0].Rect.Y)
            .ToList();
    }

    private static OcrLine BuildMergedLineFromGroup(
        IReadOnlyList<OcrLine> group,
        string separator,
        bool forceLineCountOne,
        bool sortByXThenY,
        bool sortPrimaryDescending = false)
    {
        var sortedGroup = sortByXThenY
            ? (sortPrimaryDescending
                ? group.OrderByDescending(line => line.Rect.X).ThenBy(line => line.Rect.Y).ToList()
                : group.OrderBy(line => line.Rect.X).ThenBy(line => line.Rect.Y).ToList())
            : (sortPrimaryDescending
                ? group.OrderByDescending(line => line.Rect.Y).ThenBy(line => line.Rect.X).ToList()
                : group.OrderBy(line => line.Rect.Y).ThenBy(line => line.Rect.X).ToList());

        var unionRect = sortedGroup[0].Rect;
        var minConfidence = sortedGroup[0].Confidence;
        for (var i = 1; i < sortedGroup.Count; i++)
        {
            unionRect = Rect.Union(unionRect, sortedGroup[i].Rect);
            minConfidence = Math.Min(minConfidence, sortedGroup[i].Confidence);
        }

        var lineCount = forceLineCountOne ? 1 : sortedGroup.Count;
        var lineHeight = GetMedianLineHeight(sortedGroup);
        if (lineHeight <= 0 && lineCount > 0 && unionRect.Height > 0)
        {
            lineHeight = unionRect.Height / lineCount;
        }

        var text = string.Join(separator, sortedGroup.Select(line => line.Text));
        return new OcrLine(text, unionRect, minConfidence, lineCount, lineHeight);
    }

    private static DirectionScore ScoreMergedResult(IReadOnlyList<OcrLine> merged, WritingMode mode)
    {
        var singleChar = ComputeSingleCharUnitScore(merged);
        var abnormalSpace = ComputeAbnormalSpaceScore(merged);
        var rectVariance = ComputeRectVarianceScore(merged, mode);
        var blockAspect = ComputeBlockAspectScore(merged, mode);
        var total =
            (ScoreWeightSingleChar * singleChar) +
            (ScoreWeightAbnormalSpace * abnormalSpace) +
            (ScoreWeightRectVariance * rectVariance) +
            (ScoreWeightBlockAspect * blockAspect);
        return new DirectionScore(singleChar, abnormalSpace, rectVariance, blockAspect, total);
    }

    private static double ComputeSingleCharUnitScore(IReadOnlyList<OcrLine> lines)
    {
        var totalUnits = 0;
        var singleCharUnits = 0;
        foreach (var line in lines)
        {
            var charCount = CountNonWhitespaceCharacters(line.Text);
            if (charCount == 0)
            {
                continue;
            }

            totalUnits++;
            if (charCount == 1)
            {
                singleCharUnits++;
            }
        }

        if (totalUnits == 0)
        {
            return 0.5;
        }

        return Clamp01(1.0 - (singleCharUnits / (double)totalUnits));
    }

    private static double ComputeAbnormalSpaceScore(IReadOnlyList<OcrLine> lines)
    {
        var totalChars = 0;
        var abnormalSpaces = 0;
        foreach (var line in lines)
        {
            var text = line.Text ?? string.Empty;
            totalChars += text.Length;

            for (var i = 0; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    continue;
                }

                if (i > 0 && i < text.Length - 1 &&
                    IsCjkCharacter(text[i - 1]) &&
                    IsCjkCharacter(text[i + 1]))
                {
                    abnormalSpaces++;
                }

                if (i > 0 && char.IsWhiteSpace(text[i - 1]))
                {
                    abnormalSpaces += 2;
                }
            }
        }

        if (totalChars == 0)
        {
            return 0.5;
        }

        return Clamp01(1.0 - (abnormalSpaces / (double)totalChars));
    }

    private static double ComputeRectVarianceScore(IReadOnlyList<OcrLine> lines, WritingMode mode)
    {
        if (lines.Count <= 1)
        {
            return 0.5;
        }

        var values = new List<double>(lines.Count);
        foreach (var line in lines)
        {
            var value = mode == WritingMode.Vertical ? line.Rect.Width : line.Rect.Height;
            if (value > 0)
            {
                values.Add(value);
            }
        }

        if (values.Count <= 1)
        {
            return 0.5;
        }

        var mean = values.Average();
        if (mean <= 0)
        {
            return 0.5;
        }

        var variance = values.Sum(value => Math.Pow(value - mean, 2)) / values.Count;
        var coefficientOfVariation = Math.Sqrt(variance) / mean;
        return Clamp01(1.0 - Math.Min(1.0, coefficientOfVariation));
    }

    private static double ComputeBlockAspectScore(IReadOnlyList<OcrLine> lines, WritingMode mode)
    {
        if (lines.Count == 0)
        {
            return 0.5;
        }

        var areas = lines
            .Select(line => line.Rect.Width * line.Rect.Height)
            .Where(area => area > 0)
            .OrderBy(area => area)
            .ToList();
        if (areas.Count == 0)
        {
            return 0.5;
        }

        var medianArea = areas[areas.Count / 2];
        var minArea = Math.Max(AspectFilterMinArea, medianArea * AspectFilterMedianAreaRatio);
        var scoreSum = 0.0;
        var scoreCount = 0;

        foreach (var line in lines)
        {
            var area = line.Rect.Width * line.Rect.Height;
            if (area < minArea)
            {
                continue;
            }

            var width = Math.Max(0.001, line.Rect.Width);
            var height = Math.Max(0.001, line.Rect.Height);
            var aspect = height / width;
            var verticalLikelihood = aspect / (aspect + 1.0);
            var horizontalLikelihood = 1.0 / (aspect + 1.0);
            scoreSum += mode == WritingMode.Vertical ? verticalLikelihood : horizontalLikelihood;
            scoreCount++;
        }

        if (scoreCount == 0)
        {
            return 0.5;
        }

        return Clamp01(scoreSum / scoreCount);
    }

    private static int CountNonWhitespaceCharacters(string text)
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

    private static bool IsCjkCharacter(char value)
    {
        return value is >= '\u3040' and <= '\u30FF' or
               >= '\u3400' and <= '\u4DBF' or
               >= '\u4E00' and <= '\u9FFF' or
               >= '\uF900' and <= '\uFAFF';
    }

    private static double Clamp01(double value)
    {
        return Math.Max(0.0, Math.Min(1.0, value));
    }

    private EffectiveMergeThresholds ResolveEffectiveThresholds(AppSettings settings, OcrEngineKind? effectiveEngineOverride = null)
    {
        var engineKind = effectiveEngineOverride ?? settings.OcrEngine;
        var applyPaddleScale = settings.EnableEngineScaledLineMergeProfile &&
                               engineKind == OcrEngineKind.Paddle;

        EffectiveMergeThresholds effective;
        if (!applyPaddleScale)
        {
            effective = new EffectiveMergeThresholds(
                settings.MergeOverlapRatioThreshold,
                settings.MergeVerticalWeight,
                settings.MergeThresholdRatio,
                settings.RowMergeYCenterToleranceRatio,
                settings.RowMergeHeightRatioMin,
                settings.RowMergeMaxGapRatio,
                settings.RowMergeHardBreakRatio,
                settings.VerticalGapRatio,
                VerticalColumnCenterToleranceRatio,
                VerticalColumnWidthRatioMin,
                VerticalColumnOverlapRatioMin,
                settings.VerticalColumnMergeOverlapRatioThreshold,
                settings.VerticalColumnMergeWeight,
                settings.VerticalColumnMergeThresholdRatio,
                settings.VerticalColumnMergeHardBreakRatio,
                EngineScaledApplied: false,
                engineKind);
        }
        else
        {
            effective = new EffectiveMergeThresholds(
                ClampScaled(settings.MergeOverlapRatioThreshold, settings.PaddleMergeOverlapScale, OverlapClampMin, OverlapClampMax),
                ClampScaled(settings.MergeVerticalWeight, settings.PaddleMergeVerticalWeightScale, WeightClampMin, WeightClampMax),
                ClampScaled(settings.MergeThresholdRatio, settings.PaddleMergeThresholdScale, RatioClampMin, RatioClampMax),
                ClampScaled(settings.RowMergeYCenterToleranceRatio, settings.PaddleRowMergeYCenterToleranceScale, RatioClampMin, RatioClampMax),
                ClampScaled(settings.RowMergeHeightRatioMin, settings.PaddleRowMergeHeightRatioMinScale, RatioClampMin, RatioClampMax),
                ClampScaled(settings.RowMergeMaxGapRatio, settings.PaddleRowMergeMaxGapScale, RatioClampMin, RatioClampMax),
                ClampScaled(settings.RowMergeHardBreakRatio, settings.PaddleRowMergeHardBreakScale, RatioClampMin, RatioClampMax),
                ClampScaled(settings.VerticalGapRatio, settings.PaddleVerticalGapScale, RatioClampMin, RatioClampMax),
                VerticalColumnCenterToleranceRatio,
                VerticalColumnWidthRatioMin,
                VerticalColumnOverlapRatioMin,
                ClampScaled(settings.VerticalColumnMergeOverlapRatioThreshold, settings.PaddleVerticalColumnMergeOverlapScale, OverlapClampMin, OverlapClampMax),
                ClampScaled(settings.VerticalColumnMergeWeight, settings.PaddleVerticalColumnMergeWeightScale, WeightClampMin, WeightClampMax),
                ClampScaled(settings.VerticalColumnMergeThresholdRatio, settings.PaddleVerticalColumnMergeThresholdScale, RatioClampMin, RatioClampMax),
                ClampScaled(settings.VerticalColumnMergeHardBreakRatio, settings.PaddleVerticalColumnMergeHardBreakScale, RatioClampMin, RatioClampMax),
                EngineScaledApplied: true,
                engineKind);
        }

        return ApplySimpleMergeTuning(effective, settings);
    }

    private void LogEffectiveThresholdsIfChanged(EffectiveMergeThresholds thresholds, AppSettings settings)
    {
        if (!thresholds.EngineScaledApplied || _logger == null)
        {
            return;
        }

        var key =
            $"{thresholds.EngineKind}|" +
            $"{thresholds.MergeOverlapRatioThreshold:0.###}|{thresholds.MergeVerticalWeight:0.###}|{thresholds.MergeThresholdRatio:0.###}|" +
            $"{thresholds.RowMergeYCenterToleranceRatio:0.###}|{thresholds.RowMergeHeightRatioMin:0.###}|{thresholds.RowMergeMaxGapRatio:0.###}|{thresholds.RowMergeHardBreakRatio:0.###}|" +
            $"{thresholds.VerticalGapRatio:0.###}|{thresholds.VerticalColumnMergeOverlapRatioThreshold:0.###}|{thresholds.VerticalColumnMergeWeight:0.###}|{thresholds.VerticalColumnMergeThresholdRatio:0.###}|{thresholds.VerticalColumnMergeHardBreakRatio:0.###}";
        if (string.Equals(key, _lastLoggedProfileKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedProfileKey = key;
        _logger.Info(
            $"Engine-scaled merge profile: engine={thresholds.EngineKind}, enabled={settings.EnableEngineScaledLineMergeProfile}, " +
            $"overlap={thresholds.MergeOverlapRatioThreshold:0.###}, weight={thresholds.MergeVerticalWeight:0.###}, threshold={thresholds.MergeThresholdRatio:0.###}, " +
            $"rowCenterTol={thresholds.RowMergeYCenterToleranceRatio:0.###}, rowHeightMin={thresholds.RowMergeHeightRatioMin:0.###}, rowMaxGap={thresholds.RowMergeMaxGapRatio:0.###}, rowHardBreak={thresholds.RowMergeHardBreakRatio:0.###}, " +
            $"verticalGap={thresholds.VerticalGapRatio:0.###}, colOverlap={thresholds.VerticalColumnMergeOverlapRatioThreshold:0.###}, colWeight={thresholds.VerticalColumnMergeWeight:0.###}, " +
            $"colThreshold={thresholds.VerticalColumnMergeThresholdRatio:0.###}, colHardBreak={thresholds.VerticalColumnMergeHardBreakRatio:0.###}.");
    }

    private void LogSimpleMergeTuningIfChanged(EffectiveMergeThresholds thresholds, AppSettings settings)
    {
        if (!settings.EnableSimpleMergeTuning || _logger == null)
        {
            _lastLoggedSimpleTuningKey = null;
            return;
        }

        var key =
            $"{settings.HorizontalMergeStrength}|{settings.VerticalMergeStrength}|" +
            $"{thresholds.MergeOverlapRatioThreshold:0.###}|{thresholds.MergeThresholdRatio:0.###}|{thresholds.RowMergeMaxGapRatio:0.###}|{thresholds.RowMergeHardBreakRatio:0.###}|" +
            $"{thresholds.VerticalGapRatio:0.###}|{thresholds.VerticalColumnMergeOverlapRatioThreshold:0.###}|{thresholds.VerticalColumnMergeThresholdRatio:0.###}|{thresholds.VerticalColumnMergeHardBreakRatio:0.###}";
        if (string.Equals(key, _lastLoggedSimpleTuningKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedSimpleTuningKey = key;
        _logger.Info(
            $"Simple merge tuning: enabled=1 h={settings.HorizontalMergeStrength} v={settings.VerticalMergeStrength}, " +
            $"h(overlap={thresholds.MergeOverlapRatioThreshold:0.###}, threshold={thresholds.MergeThresholdRatio:0.###}, rowMaxGap={thresholds.RowMergeMaxGapRatio:0.###}, rowHardBreak={thresholds.RowMergeHardBreakRatio:0.###}), " +
            $"v(stageA_centerTol={thresholds.VerticalStageACenterToleranceRatio:0.###}, stageA_widthMin={thresholds.VerticalStageAWidthRatioMin:0.###}, stageA_overlapMin={thresholds.VerticalStageAOverlapRatioMin:0.###}, " +
            $"gap={thresholds.VerticalGapRatio:0.###}, colOverlap={thresholds.VerticalColumnMergeOverlapRatioThreshold:0.###}, colThreshold={thresholds.VerticalColumnMergeThresholdRatio:0.###}, colHardBreak={thresholds.VerticalColumnMergeHardBreakRatio:0.###}).");
    }

    private static EffectiveMergeThresholds ApplySimpleMergeTuning(EffectiveMergeThresholds current, AppSettings settings)
    {
        if (!settings.EnableSimpleMergeTuning)
        {
            return current;
        }

        var horizontal = Clamp01(settings.HorizontalMergeStrength / 100.0);
        var vertical = Clamp01(settings.VerticalMergeStrength / 100.0);

        var tunedHorizontalOverlap = ClampScaled(
            Lerp(SimpleHorizontalOverlapMax, SimpleHorizontalOverlapMin, horizontal),
            1.0,
            OverlapClampMin,
            OverlapClampMax);
        var tunedHorizontalThreshold = ClampScaled(
            Lerp(SimpleHorizontalThresholdMin, SimpleHorizontalThresholdMax, horizontal),
            1.0,
            RatioClampMin,
            RatioClampMax);
        var tunedHorizontalRowMaxGap = ClampScaled(
            Lerp(SimpleHorizontalRowMaxGapMin, SimpleHorizontalRowMaxGapMax, horizontal),
            1.0,
            RatioClampMin,
            RatioClampMax);
        var tunedHorizontalRowHardBreak = ClampScaled(
            Lerp(SimpleHorizontalRowHardBreakMin, SimpleHorizontalRowHardBreakMax, horizontal),
            1.0,
            RatioClampMin,
            RatioClampMax);
        var tunedVerticalGap = ClampScaled(
            Lerp(SimpleVerticalGapMin, SimpleVerticalGapMax, vertical),
            1.0,
            RatioClampMin,
            RatioClampMax);
        var tunedVerticalColumnOverlap = ClampScaled(
            Lerp(SimpleVerticalColumnOverlapMax, SimpleVerticalColumnOverlapMin, vertical),
            1.0,
            OverlapClampMin,
            OverlapClampMax);
        var tunedVerticalColumnThreshold = ClampScaled(
            Lerp(SimpleVerticalColumnThresholdMin, SimpleVerticalColumnThresholdMax, vertical),
            1.0,
            RatioClampMin,
            RatioClampMax);
        var tunedVerticalColumnHardBreak = ClampScaled(
            Lerp(SimpleVerticalColumnHardBreakMin, SimpleVerticalColumnHardBreakMax, vertical),
            1.0,
            RatioClampMin,
            RatioClampMax);
        var tunedVerticalStageACenterTolerance = ClampScaled(
            Lerp(SimpleVerticalStageACenterToleranceMin, SimpleVerticalStageACenterToleranceMax, vertical),
            1.0,
            RatioClampMin,
            RatioClampMax);
        var tunedVerticalStageAWidthRatioMin = ClampScaled(
            Lerp(SimpleVerticalStageAWidthRatioMinMax, SimpleVerticalStageAWidthRatioMinMin, vertical),
            1.0,
            OverlapClampMin,
            OverlapClampMax);
        var tunedVerticalStageAOverlapRatioMin = ClampScaled(
            Lerp(SimpleVerticalStageAOverlapRatioMinMax, SimpleVerticalStageAOverlapRatioMinMin, vertical),
            1.0,
            OverlapClampMin,
            OverlapClampMax);

        return current with
        {
            MergeOverlapRatioThreshold = tunedHorizontalOverlap,
            MergeThresholdRatio = tunedHorizontalThreshold,
            RowMergeMaxGapRatio = tunedHorizontalRowMaxGap,
            RowMergeHardBreakRatio = tunedHorizontalRowHardBreak,
            VerticalGapRatio = tunedVerticalGap,
            VerticalStageACenterToleranceRatio = tunedVerticalStageACenterTolerance,
            VerticalStageAWidthRatioMin = tunedVerticalStageAWidthRatioMin,
            VerticalStageAOverlapRatioMin = tunedVerticalStageAOverlapRatioMin,
            VerticalColumnMergeOverlapRatioThreshold = tunedVerticalColumnOverlap,
            VerticalColumnMergeThresholdRatio = tunedVerticalColumnThreshold,
            VerticalColumnMergeHardBreakRatio = tunedVerticalColumnHardBreak
        };
    }

    private static double ClampScaled(double baseValue, double scale, double minValue, double maxValue)
    {
        var scaled = baseValue * scale;
        if (double.IsNaN(scaled) || double.IsInfinity(scaled))
        {
            return Math.Clamp(baseValue, minValue, maxValue);
        }

        return Math.Clamp(scaled, minValue, maxValue);
    }

    private static double Lerp(double minValue, double maxValue, double ratio)
    {
        return minValue + ((maxValue - minValue) * Clamp01(ratio));
    }

    private static List<OcrLine> OrderLines(IEnumerable<OcrLine> lines, WritingMode mode, AppSettings settings)
    {
        if (mode == WritingMode.Vertical)
        {
            if (settings.VerticalColumnOrder == VerticalColumnOrder.LeftToRight)
            {
                return lines
                    .OrderBy(line => line.Rect.X)
                    .ThenBy(line => line.Rect.Y)
                    .ToList();
            }

            return lines
                .OrderByDescending(line => line.Rect.X)
                .ThenBy(line => line.Rect.Y)
                .ToList();
        }

        return lines
            .OrderBy(line => line.Rect.Y)
            .ThenBy(line => line.Rect.X)
            .ToList();
    }

    private static double GetCenterY(Rect rect)
    {
        return rect.Top + (rect.Height / 2.0);
    }

    private static double GetCenterX(Rect rect)
    {
        return rect.Left + (rect.Width / 2.0);
    }

    private static bool PassesAlignmentGate(Rect a, Rect b, double overlapRatioThreshold)
    {
        var overlapWidth = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var minWidth = Math.Min(a.Width, b.Width);
        if (minWidth <= 0)
        {
            return false;
        }

        return (overlapWidth / minWidth) >= overlapRatioThreshold;
    }

    private static bool PassesVerticalAlignmentGate(Rect a, Rect b, double overlapRatioThreshold)
    {
        var overlapHeight = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        var minHeight = Math.Min(a.Height, b.Height);
        if (minHeight <= 0)
        {
            return false;
        }

        return (overlapHeight / minHeight) >= overlapRatioThreshold;
    }

    private static bool IsMergeableByCost(Rect a, Rect b, EffectiveMergeThresholds thresholds)
    {
        var verticalGap = Math.Max(0, b.Top - a.Bottom);
        var sizePenalty = Math.Abs(a.Height - b.Height);
        var cost = (thresholds.MergeVerticalWeight * verticalGap) + sizePenalty;
        var threshold = Math.Min(a.Height, b.Height) * thresholds.MergeThresholdRatio;
        return cost <= threshold;
    }

    private static bool IsVerticalColumnMergeableByCost(
        Rect a,
        Rect b,
        double horizontalGap,
        EffectiveMergeThresholds thresholds)
    {
        var minWidth = Math.Min(a.Width, b.Width);
        if (minWidth <= 0)
        {
            return false;
        }

        var sizePenalty = Math.Abs(a.Width - b.Width);
        var cost = (thresholds.VerticalColumnMergeWeight * horizontalGap) + sizePenalty;
        var threshold = minWidth * thresholds.VerticalColumnMergeThresholdRatio;
        return cost <= threshold;
    }

    private static double GetMedianLineHeight(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0)
        {
            return 0;
        }

        var heights = new List<double>(lines.Count);
        foreach (var line in lines)
        {
            var height = line.LineHeight > 0 ? line.LineHeight : line.Rect.Height;
            if (height > 0)
            {
                heights.Add(height);
            }
        }

        if (heights.Count == 0)
        {
            return 0;
        }

        // WHY: Median dampens outliers from tall OCR boxes.
        heights.Sort();
        var mid = heights.Count / 2;
        if (heights.Count % 2 == 1)
        {
            return heights[mid];
        }

        return (heights[mid - 1] + heights[mid]) / 2.0;
    }

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        public UnionFind(int count)
        {
            _parent = new int[count];
            _rank = new byte[count];
            for (var i = 0; i < count; i++)
            {
                _parent[i] = i;
            }
        }

        public int Find(int x)
        {
            if (_parent[x] != x)
            {
                _parent[x] = Find(_parent[x]);
            }

            return _parent[x];
        }

        public bool Union(int a, int b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA == rootB)
            {
                return false;
            }

            if (_rank[rootA] < _rank[rootB])
            {
                _parent[rootA] = rootB;
                return true;
            }

            _parent[rootB] = rootA;
            if (_rank[rootA] == _rank[rootB])
            {
                _rank[rootA]++;
            }

            return true;
        }
    }
}

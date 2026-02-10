using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OcrLineGrouper
{
    private const double VerticalDetectDominanceRatio = 1.2;
    private const int VerticalDetectMinSamples = 2;
    private const double VerticalColumnCenterToleranceRatio = 0.55;
    private const double VerticalColumnWidthRatioMin = 0.55;
    private const double VerticalColumnOverlapRatioMin = 0.10;
    private const double VerticalHardBreakMultiplier = 1.5;

    private enum WritingMode
    {
        Horizontal,
        Vertical,
        Unknown
    }

    public IReadOnlyList<OcrLine> MergeLines(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        if (!settings.EnableLineMerge || lines.Count <= 1)
        {
            return lines;
        }

        var writingMode = ResolveWritingMode(lines, settings);
        if (!settings.EnableTwoStageLineMerge)
        {
            if (writingMode == WritingMode.Vertical)
            {
                return OrderLines(lines, writingMode, settings);
            }

            var ordered = OrderLines(lines, WritingMode.Horizontal, settings);
            return MergeHorizontalLines(ordered, settings);
        }

        if (writingMode == WritingMode.Vertical)
        {
            return MergeVerticalLinesTwoStage(lines, settings);
        }

        var horizontalOrdered = OrderLines(lines, WritingMode.Horizontal, settings);
        var stageAResult = MergeSameRowTokens(horizontalOrdered, settings);
        return MergeHorizontalLines(stageAResult, settings);
    }

    private static WritingMode ResolveWritingMode(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        if (!settings.EnableVerticalMerge)
        {
            return WritingMode.Horizontal;
        }

        if (settings.VerticalModeOverride == VerticalModeOverride.Horizontal)
        {
            return WritingMode.Horizontal;
        }

        if (settings.VerticalModeOverride == VerticalModeOverride.Vertical)
        {
            return WritingMode.Vertical;
        }

        if (!settings.VerticalModeAutoDetect || !ShouldEnableVerticalDetectionForLanguage(settings.SourceLanguage))
        {
            return WritingMode.Horizontal;
        }

        var horizontalSignals = 0;
        var verticalSignals = 0;
        var samples = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var neighborIndex = FindNearestNeighborIndex(lines, i);
            if (neighborIndex < 0)
            {
                continue;
            }

            var dx = Math.Abs(GetCenterX(lines[i].Rect) - GetCenterX(lines[neighborIndex].Rect));
            var dy = Math.Abs(GetCenterY(lines[i].Rect) - GetCenterY(lines[neighborIndex].Rect));
            if (dx <= 0.001 && dy <= 0.001)
            {
                continue;
            }

            samples++;
            if (dy > dx)
            {
                verticalSignals++;
            }
            else if (dx > dy)
            {
                horizontalSignals++;
            }
        }

        if (samples < VerticalDetectMinSamples)
        {
            return WritingMode.Unknown;
        }

        // WHY: Require a dominance margin so mixed layouts fall back to horizontal safely.
        if (verticalSignals >= VerticalDetectMinSamples &&
            verticalSignals >= Math.Ceiling(horizontalSignals * VerticalDetectDominanceRatio))
        {
            return WritingMode.Vertical;
        }

        if (horizontalSignals >= VerticalDetectMinSamples &&
            horizontalSignals >= Math.Ceiling(verticalSignals * VerticalDetectDominanceRatio))
        {
            return WritingMode.Horizontal;
        }

        return WritingMode.Unknown;
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

    private static int FindNearestNeighborIndex(IReadOnlyList<OcrLine> lines, int sourceIndex)
    {
        var source = lines[sourceIndex];
        var sourceCenterX = GetCenterX(source.Rect);
        var sourceCenterY = GetCenterY(source.Rect);
        var nearest = -1;
        var nearestDistance = double.MaxValue;

        for (var i = 0; i < lines.Count; i++)
        {
            if (i == sourceIndex)
            {
                continue;
            }

            var dx = sourceCenterX - GetCenterX(lines[i].Rect);
            var dy = sourceCenterY - GetCenterY(lines[i].Rect);
            var distance = (dx * dx) + (dy * dy);
            if (distance >= nearestDistance)
            {
                continue;
            }

            nearestDistance = distance;
            nearest = i;
        }

        return nearest;
    }

    private static IReadOnlyList<OcrLine> MergeSameRowTokens(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        if (lines.Count <= 1)
        {
            return lines;
        }

        var rowClusters = BuildRowClusters(lines, settings);
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
                if (ShouldMergeAdjacentTokens(orderedRow[i].Rect, orderedRow[i + 1].Rect, settings))
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

    private static IReadOnlyList<OcrLine> MergeHorizontalLines(IReadOnlyList<OcrLine> lines, AppSettings settings)
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
                if (!PassesAlignmentGate(a.Rect, b.Rect, settings.MergeOverlapRatioThreshold))
                {
                    continue;
                }

                if (!IsMergeableByCost(a.Rect, b.Rect, settings))
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

    private static IReadOnlyList<OcrLine> MergeVerticalLinesTwoStage(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        if (lines.Count <= 1)
        {
            return OrderLines(lines, WritingMode.Vertical, settings);
        }

        var ordered = OrderLines(lines, WritingMode.Vertical, settings);
        var columnClusters = BuildColumnClusters(ordered, settings);
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
                if (ShouldMergeVerticalAdjacent(orderedColumn[i].Rect, orderedColumn[i + 1].Rect, settings))
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

        return OrderLines(merged, WritingMode.Vertical, settings);
    }

    private static UnionFind BuildRowClusters(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        var unionFind = new UnionFind(lines.Count);
        var neighborCount = Math.Max(1, settings.RowMergeNeighborCount);
        for (var i = 0; i < lines.Count; i++)
        {
            var a = lines[i];
            for (var offset = 1; offset <= neighborCount && i + offset < lines.Count; offset++)
            {
                var b = lines[i + offset];
                if (!IsSameRowCandidate(a.Rect, b.Rect, settings))
                {
                    continue;
                }

                unionFind.Union(i, i + offset);
            }
        }

        return unionFind;
    }

    private static UnionFind BuildColumnClusters(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        var unionFind = new UnionFind(lines.Count);
        var neighborCount = Math.Max(1, settings.RowMergeNeighborCount);
        for (var i = 0; i < lines.Count; i++)
        {
            var a = lines[i];
            for (var offset = 1; offset <= neighborCount && i + offset < lines.Count; offset++)
            {
                var b = lines[i + offset];
                if (!IsSameColumnCandidate(a.Rect, b.Rect))
                {
                    continue;
                }

                unionFind.Union(i, i + offset);
            }
        }

        return unionFind;
    }

    private static bool IsSameRowCandidate(Rect a, Rect b, AppSettings settings)
    {
        var minHeight = Math.Min(a.Height, b.Height);
        var maxHeight = Math.Max(a.Height, b.Height);
        if (minHeight <= 0 || maxHeight <= 0)
        {
            return false;
        }

        var centerDiff = Math.Abs(GetCenterY(a) - GetCenterY(b));
        if (centerDiff > minHeight * settings.RowMergeYCenterToleranceRatio)
        {
            return false;
        }

        var heightRatio = minHeight / maxHeight;
        return heightRatio >= settings.RowMergeHeightRatioMin;
    }

    private static bool IsSameColumnCandidate(Rect a, Rect b)
    {
        var minWidth = Math.Min(a.Width, b.Width);
        var maxWidth = Math.Max(a.Width, b.Width);
        if (minWidth <= 0 || maxWidth <= 0)
        {
            return false;
        }

        var centerDiff = Math.Abs(GetCenterX(a) - GetCenterX(b));
        if (centerDiff > minWidth * VerticalColumnCenterToleranceRatio)
        {
            return false;
        }

        var widthRatio = minWidth / maxWidth;
        if (widthRatio < VerticalColumnWidthRatioMin)
        {
            return false;
        }

        var overlapWidth = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        return (overlapWidth / minWidth) >= VerticalColumnOverlapRatioMin;
    }

    private static bool ShouldMergeAdjacentTokens(Rect left, Rect right, AppSettings settings)
    {
        var minHeight = Math.Min(left.Height, right.Height);
        if (minHeight <= 0)
        {
            return false;
        }

        var gapX = Math.Max(0, right.Left - left.Right);
        if (gapX > minHeight * settings.RowMergeHardBreakRatio)
        {
            return false;
        }

        return gapX <= minHeight * settings.RowMergeMaxGapRatio;
    }

    private static bool ShouldMergeVerticalAdjacent(Rect top, Rect bottom, AppSettings settings)
    {
        var minWidth = Math.Min(top.Width, bottom.Width);
        if (minWidth <= 0)
        {
            return false;
        }

        var gapRatio = Math.Max(0.1, settings.VerticalGapRatio);
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
        bool sortByXThenY)
    {
        var sortedGroup = group
            .OrderBy(line => sortByXThenY ? line.Rect.X : line.Rect.Y)
            .ThenBy(line => sortByXThenY ? line.Rect.Y : line.Rect.X)
            .ToList();

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

    private static bool IsMergeableByCost(Rect a, Rect b, AppSettings settings)
    {
        var verticalGap = Math.Max(0, b.Top - a.Bottom);
        var sizePenalty = Math.Abs(a.Height - b.Height);
        var cost = (settings.MergeVerticalWeight * verticalGap) + sizePenalty;
        var threshold = Math.Min(a.Height, b.Height) * settings.MergeThresholdRatio;
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

        public void Union(int a, int b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA == rootB)
            {
                return;
            }

            if (_rank[rootA] < _rank[rootB])
            {
                _parent[rootA] = rootB;
                return;
            }

            _parent[rootB] = rootA;
            if (_rank[rootA] == _rank[rootB])
            {
                _rank[rootA]++;
            }
        }
    }
}

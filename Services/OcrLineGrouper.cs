using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OcrLineGrouper
{
    public IReadOnlyList<OcrLine> MergeLines(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        if (!settings.EnableLineMerge || lines.Count <= 1)
        {
            return lines;
        }

        var ordered = OrderLines(lines);
        if (!settings.EnableTwoStageLineMerge)
        {
            return MergeVerticalLines(ordered, settings);
        }

        var stageAResult = MergeSameRowTokens(ordered, settings);
        return MergeVerticalLines(stageAResult, settings);
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

        return OrderLines(merged);
    }

    private static IReadOnlyList<OcrLine> MergeVerticalLines(IReadOnlyList<OcrLine> lines, AppSettings settings)
    {
        if (lines.Count <= 1)
        {
            return lines;
        }

        var ordered = OrderLines(lines);
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

        return OrderLines(merged);
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

    private static List<OcrLine> OrderLines(IEnumerable<OcrLine> lines)
    {
        return lines
            .OrderBy(line => line.Rect.Y)
            .ThenBy(line => line.Rect.X)
            .ToList();
    }

    private static double GetCenterY(Rect rect)
    {
        return rect.Top + (rect.Height / 2.0);
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

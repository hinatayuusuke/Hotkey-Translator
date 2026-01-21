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

        var ordered = lines
            .OrderBy(line => line.Rect.Y)
            .ThenBy(line => line.Rect.X)
            .ToList();

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

            var unionRect = sortedGroup[0].Rect;
            var minConfidence = sortedGroup[0].Confidence;
            for (var i = 1; i < sortedGroup.Count; i++)
            {
                unionRect = Rect.Union(unionRect, sortedGroup[i].Rect);
                minConfidence = Math.Min(minConfidence, sortedGroup[i].Confidence);
            }

            var lineCount = sortedGroup.Count;
            var lineHeight = GetMedianLineHeight(sortedGroup);
            if (lineHeight <= 0 && lineCount > 0 && unionRect.Height > 0)
            {
                lineHeight = unionRect.Height / lineCount;
            }

            var text = string.Join(Environment.NewLine, sortedGroup.Select(line => line.Text));
            merged.Add(new OcrLine(text, unionRect, minConfidence, lineCount, lineHeight));
        }

        return merged
            .OrderBy(line => line.Rect.Y)
            .ThenBy(line => line.Rect.X)
            .ToList();
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

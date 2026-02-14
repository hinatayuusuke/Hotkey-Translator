using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Orchestration.Stages;

internal readonly record struct DiffStageOutput(
    IReadOnlyList<OcrLine> ChangedLines,
    IReadOnlySet<int> ChangedUnitIds,
    long ElapsedMs);

internal sealed class DiffStage
{
    private readonly OcrDiffService _ocrDiffService;

    public DiffStage(OcrDiffService ocrDiffService)
    {
        _ocrDiffService = ocrDiffService;
    }

    public DiffStageOutput Execute(
        IReadOnlyList<ReadingUnit> units,
        IReadOnlyList<OcrLine> groupedLines,
        bool skipOcrDiff)
    {
        var stopwatch = Stopwatch.StartNew();
        var changedLines = skipOcrDiff ? groupedLines : _ocrDiffService.FilterChangedLines(groupedLines);
        var changedUnitIds = ResolveChangedUnitIds(units, groupedLines, changedLines, skipOcrDiff);
        stopwatch.Stop();
        return new DiffStageOutput(changedLines, changedUnitIds, stopwatch.ElapsedMilliseconds);
    }

    private static HashSet<int> ResolveChangedUnitIds(
        IReadOnlyList<ReadingUnit> units,
        IReadOnlyList<OcrLine> groupedLines,
        IReadOnlyList<OcrLine> changedLines,
        bool skipOcrDiff)
    {
        if (skipOcrDiff)
        {
            return units.Select(unit => unit.Id).ToHashSet();
        }

        if (changedLines.Count == 0)
        {
            return new HashSet<int>();
        }

        var changedLineSet = new HashSet<OcrLine>(changedLines);
        var changedUnitIds = new HashSet<int>();
        foreach (var unit in units)
        {
            foreach (var sourceIndex in unit.SourceIndices)
            {
                if (sourceIndex < 0 || sourceIndex >= groupedLines.Count)
                {
                    continue;
                }

                if (changedLineSet.Contains(groupedLines[sourceIndex]))
                {
                    changedUnitIds.Add(unit.Id);
                    break;
                }
            }
        }

        return changedUnitIds;
    }
}

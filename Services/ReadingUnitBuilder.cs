using System;
using System.Collections.Generic;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class ReadingUnitBuilder
{
    public IReadOnlyList<ReadingUnit> Build(IReadOnlyList<OcrLine> groupedLines, AppSettings settings)
    {
        if (groupedLines.Count == 0)
        {
            return Array.Empty<ReadingUnit>();
        }

        var units = new List<ReadingUnit>(groupedLines.Count);
        for (var i = 0; i < groupedLines.Count; i++)
        {
            var line = groupedLines[i];
            units.Add(new ReadingUnit(
                i,
                line.Text,
                line.Rect,
                Math.Max(1, line.LineCount),
                line.LineHeight,
                new[] { i }));
        }

        return units;
    }
}

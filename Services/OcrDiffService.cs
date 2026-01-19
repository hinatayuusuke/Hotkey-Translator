using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OcrDiffService
{
    private IReadOnlyList<OcrLine> _previous = Array.Empty<OcrLine>();

    public double IouThreshold { get; set; } = 0.85;

    public IReadOnlyList<OcrLine> FilterChangedLines(IReadOnlyList<OcrLine> current)
    {
        if (_previous.Count == 0)
        {
            _previous = current.ToArray();
            return current;
        }

        var changed = new List<OcrLine>();
        foreach (var line in current)
        {
            if (!IsSameAsPrevious(line))
            {
                changed.Add(line);
            }
        }

        _previous = current.ToArray();
        return changed;
    }

    private bool IsSameAsPrevious(OcrLine line)
    {
        foreach (var prev in _previous)
        {
            if (!string.Equals(prev.Text, line.Text, StringComparison.Ordinal))
            {
                continue;
            }

            // NOTE: IoU threshold absorbs small OCR jitter without suppressing real changes.
            if (ComputeIou(prev.Rect, line.Rect) >= IouThreshold)
            {
                return true;
            }
        }

        return false;
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
}

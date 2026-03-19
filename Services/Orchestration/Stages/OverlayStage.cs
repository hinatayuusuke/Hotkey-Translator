using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Orchestration.Stages;

internal sealed class OverlayStage
{
    private readonly OverlayPresenter _overlayPresenter;

    public OverlayStage(OverlayPresenter overlayPresenter)
    {
        _overlayPresenter = overlayPresenter;
    }

    public IReadOnlyList<OverlayItem> BuildItems(
        IReadOnlyList<ReadingUnit> readingUnits,
        Dictionary<int, string> translations,
        Rect roiScreen,
        Rect captureBounds,
        AppSettings settings,
        OverlayTextMode mode)
    {
        if (!settings.EnableFixedRoiOverlay)
        {
            return readingUnits
                .Select(unit => new OverlayItem(GetOverlayText(unit, translations, mode), unit.Rect, unit.LineCount, unit.LineHeight))
                .ToList();
        }

        if (readingUnits.Count == 0)
        {
            return Array.Empty<OverlayItem>();
        }

        var lines = new List<string>(readingUnits.Count);
        foreach (var unit in readingUnits)
        {
            var text = GetOverlayText(unit, translations, mode);
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(text);
            }
        }

        var combined = lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        var lineCount = Math.Max(1, readingUnits.Sum(unit => Math.Max(1, unit.LineCount)));
        var lineHeights = readingUnits.Select(unit => unit.LineHeight).Where(height => height > 0).ToList();
        var lineHeight = lineHeights.Count > 0 ? lineHeights.Average() : 0;
        var targetRect = ResolveFixedOverlayTargetRect(settings, roiScreen, captureBounds);

        return new[] { new OverlayItem(combined, targetRect, lineCount, lineHeight) };
    }

    public void Update(IReadOnlyList<OverlayItem> overlayItems, Rect? overlayClipScreen)
    {
        _overlayPresenter.Update(overlayItems, overlayClipScreen);
    }

    private static string GetOverlayText(
        ReadingUnit unit,
        Dictionary<int, string> translations,
        OverlayTextMode mode)
    {
        var text = mode == OverlayTextMode.Translated && translations.TryGetValue(unit.Id, out var translated)
            ? translated
            : unit.Text;
        return NormalizeOverlayText(text, unit.LineCount);
    }

    private static string NormalizeOverlayText(string text, int lineCount)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        if (lineCount <= 1)
        {
            return string.Join(" ", lines);
        }

        if (lines.Count <= lineCount)
        {
            return text;
        }

        // WHY: Constrain translated line breaks to the OCR line count to reduce overflow.
        var head = lines.Take(lineCount - 1);
        var tail = string.Join(" ", lines.Skip(lineCount - 1));
        return string.Join(Environment.NewLine, head.Append(tail));
    }

    private static Rect ResolveFixedOverlayTargetRect(AppSettings settings, Rect roiScreen, Rect captureBounds)
    {
        if (settings.FixedOverlayPlacementMode != FixedOverlayPlacementMode.CustomFrame)
        {
            return roiScreen;
        }

        if (captureBounds.IsEmpty || captureBounds.Width <= 0 || captureBounds.Height <= 0)
        {
            return roiScreen;
        }

        if (settings.FixedOverlayNormalizedRect is not { } normalized || normalized.IsEmpty)
        {
            return roiScreen;
        }

        var customRect = normalized.ToAbsolute(captureBounds);
        if (customRect.IsEmpty || customRect.Width <= 0 || customRect.Height <= 0)
        {
            return roiScreen;
        }

        // WHY: Keep fixed-overlay custom target fail-safe; invalid or stale saved geometry falls back to ROI.
        return customRect;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Hotkey_Translator.Services;

internal readonly record struct OverlayFontFitContext(
    Typeface Typeface,
    FlowDirection FlowDirection,
    CultureInfo Culture,
    double PixelsPerDip);

internal readonly record struct OverlayFontFitRequest(
    string Text,
    double AvailableWidth,
    double AvailableHeight,
    string CacheKey,
    double BaseFontSize,
    bool EnableStabilization,
    double MinFontSize,
    double MaxFontSize,
    double MinFallbackFontSize,
    int FitIterations,
    double QuantizeStepPx,
    double HysteresisThreshold);

internal sealed class OverlayFontFitter
{
    public double ResolveFontSize(
        in OverlayFontFitRequest request,
        in OverlayFontFitContext context,
        IReadOnlyDictionary<string, double> lastCache)
    {
        var text = request.Text ?? string.Empty;
        var minFont = Math.Clamp(request.MinFontSize, request.MinFallbackFontSize, request.MaxFontSize);
        var baseSize = Math.Clamp(request.BaseFontSize, minFont, request.MaxFontSize);
        if (string.IsNullOrWhiteSpace(text))
        {
            return baseSize;
        }

        if (request.AvailableWidth <= 0 ||
            request.AvailableHeight <= 0 ||
            double.IsInfinity(request.AvailableWidth) ||
            double.IsInfinity(request.AvailableHeight))
        {
            return baseSize;
        }

        var quantizedWidth = request.EnableStabilization
            ? QuantizeLength(request.AvailableWidth, request.QuantizeStepPx)
            : request.AvailableWidth;
        var quantizedHeight = request.EnableStabilization
            ? QuantizeLength(request.AvailableHeight, request.QuantizeStepPx)
            : request.AvailableHeight;

        var resolved = FitMaxFont(text, minFont, baseSize, quantizedWidth, quantizedHeight, request.FitIterations, context);
        if (!Fits(text, resolved, request.AvailableWidth, request.AvailableHeight, context))
        {
            resolved = FitMaxFont(text, minFont, baseSize, request.AvailableWidth, request.AvailableHeight, request.FitIterations, context);
        }

        if (!Fits(text, minFont, request.AvailableWidth, request.AvailableHeight, context))
        {
            var fallbackUpper = Math.Min(minFont, baseSize);
            resolved = FitMaxFont(
                text,
                request.MinFallbackFontSize,
                fallbackUpper,
                request.AvailableWidth,
                request.AvailableHeight,
                request.FitIterations,
                context);
        }

        if (!request.EnableStabilization)
        {
            return Math.Clamp(resolved, request.MinFallbackFontSize, request.MaxFontSize);
        }

        if (!string.IsNullOrEmpty(request.CacheKey) &&
            lastCache.TryGetValue(request.CacheKey, out var last))
        {
            if (resolved < last)
            {
                return Math.Clamp(resolved, request.MinFallbackFontSize, request.MaxFontSize);
            }

            if (resolved - last < request.HysteresisThreshold)
            {
                return Math.Clamp(last, request.MinFallbackFontSize, request.MaxFontSize);
            }
        }

        return Math.Clamp(resolved, request.MinFallbackFontSize, request.MaxFontSize);
    }

    public static string BuildCacheKey(string text, Rect rect, bool enableStabilization, double quantizeStepPx)
    {
        var x = enableStabilization ? QuantizeLength(rect.X, quantizeStepPx) : rect.X;
        var y = enableStabilization ? QuantizeLength(rect.Y, quantizeStepPx) : rect.Y;
        var width = enableStabilization ? QuantizeLength(rect.Width, quantizeStepPx) : rect.Width;
        var height = enableStabilization ? QuantizeLength(rect.Height, quantizeStepPx) : rect.Height;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0}|{1:0.0}|{2:0.0}|{3:0.0}|{4}",
            x,
            y,
            width,
            height,
            text ?? string.Empty);
    }

    private static bool Fits(string text, double fontSize, double maxWidth, double maxHeight, in OverlayFontFitContext context)
    {
        if (fontSize <= 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return false;
        }

        var formatted = new FormattedText(
            text,
            context.Culture,
            context.FlowDirection,
            context.Typeface,
            fontSize,
            Brushes.White,
            context.PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            // WHY: Keep MaxTextHeight unset so we can detect vertical overflow via measured Height.
            TextAlignment = TextAlignment.Left
        };

        return formatted.Width <= maxWidth && formatted.Height <= maxHeight;
    }

    private static double FitMaxFont(
        string text,
        double minFont,
        double maxFont,
        double maxWidth,
        double maxHeight,
        int fitIterations,
        in OverlayFontFitContext context)
    {
        var iterations = Math.Max(1, fitIterations);
        var lower = Math.Clamp(minFont, 1.0, maxFont);
        var upper = Math.Max(lower, maxFont);
        if (!Fits(text, lower, maxWidth, maxHeight, context))
        {
            return lower;
        }

        var best = lower;
        for (var i = 0; i < iterations; i++)
        {
            var mid = (lower + upper) / 2.0;
            if (Fits(text, mid, maxWidth, maxHeight, context))
            {
                best = mid;
                lower = mid;
            }
            else
            {
                upper = mid;
            }
        }

        return best;
    }

    private static double QuantizeLength(double value, double quantizeStepPx)
    {
        if (quantizeStepPx <= 0 || value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return value;
        }

        return Math.Round(value / quantizeStepPx, MidpointRounding.AwayFromZero) * quantizeStepPx;
    }
}

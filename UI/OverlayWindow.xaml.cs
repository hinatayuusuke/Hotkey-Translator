using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.UI;

public partial class OverlayWindow : Window
{
    private Brush _foreground = Brushes.White;
    private Brush _background = new SolidColorBrush(Color.FromArgb(136, 0, 0, 0));
    private double _fontSize = 18;
    private bool _isFixedRoiOverlay;
    private bool _enableFontStabilization = true;
    private Dictionary<string, double> _fontSizeCache = new();
    private static readonly Thickness OverlayPadding = new(4, 2, 4, 2);
    private const double MinFontSize = 8;
    private const double MaxFontSize = 72;
    private const int FitIterations = 7;
    private const double FontQuantizeStepPx = 5.0;
    private const double FontHysteresisThreshold = 1.0;
    private const double MinFallbackFontSize = 6.0;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void ApplyStyle(AppSettings settings)
    {
        _fontSize = settings.OverlayFontSize;
        _foreground = ParseBrush(settings.OverlayForeground, Brushes.White);
        // NOTE: Opacity slider overrides the alpha channel from OverlayBackground.
        var background = ParseBrush(settings.OverlayBackground, new SolidColorBrush(Color.FromArgb(136, 0, 0, 0)));
        _background = ApplyOverlayOpacity(background, settings.OverlayBackgroundOpacity);
        _isFixedRoiOverlay = settings.EnableFixedRoiOverlay;
        _enableFontStabilization = settings.EnableOverlayShortLineShrink;
    }

    public void UpdateItems(IReadOnlyList<OverlayItem> items)
    {
        OverlayCanvas.Children.Clear();
        var nextCache = new Dictionary<string, double>(items.Count);
        foreach (var item in items)
        {
            var rect = item.Rect;
            var availableWidth = rect.Width > 0
                ? Math.Max(0, rect.Width - OverlayPadding.Left - OverlayPadding.Right)
                : double.PositiveInfinity;
            var availableHeight = rect.Height > 0
                ? Math.Max(0, rect.Height - OverlayPadding.Top - OverlayPadding.Bottom)
                : double.PositiveInfinity;
            var cacheKey = BuildFontCacheKey(item, rect);
            var fontSize = ResolveFontSize(item, availableWidth, availableHeight, cacheKey);
            nextCache[cacheKey] = fontSize;
            var textBlock = new TextBlock
            {
                Text = item.Text,
                Foreground = _foreground,
                FontSize = fontSize,
                TextWrapping = TextWrapping.Wrap
            };

            var container = new Border
            {
                Background = _background,
                CornerRadius = new CornerRadius(2),
                Padding = OverlayPadding,
                Child = textBlock,
                MaxWidth = rect.Width > 0 ? rect.Width : double.PositiveInfinity,
                MaxHeight = rect.Height > 0 ? rect.Height : double.PositiveInfinity
            };

            if (rect.Width > 0)
            {
                container.Width = rect.Width;
                textBlock.MaxWidth = availableWidth;
            }

            if (rect.Height > 0)
            {
                container.Height = rect.Height;
                textBlock.MaxHeight = availableHeight;
            }

            Canvas.SetLeft(container, rect.X);
            Canvas.SetTop(container, rect.Y);
            OverlayCanvas.Children.Add(container);
        }

        _fontSizeCache = nextCache;
    }

    public void SetOverlayVisibility(bool visible)
    {
        OverlayCanvas.Opacity = visible ? 1.0 : 0.0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var extended = GetWindowLongPtr(hwnd, GwlExStyle);
        var updated = new IntPtr(extended.ToInt64() | WsExTransparent | WsExToolWindow | WsExNoActivate);
        SetWindowLongPtr(hwnd, GwlExStyle, updated);
        // WHY: Exclude the overlay from capture so scene-change checks and OCR use clean frames.
        _ = SetWindowDisplayAffinity(hwnd, WindowDisplayAffinityExcludeFromCapture);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateBounds();
    }

    private void UpdateBounds()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private double ResolveFontSize(OverlayItem item, double availableWidth, double availableHeight, string cacheKey)
    {
        var baseSize = Math.Clamp(_fontSize, MinFontSize, MaxFontSize);
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            return baseSize;
        }

        if (availableWidth <= 0 || availableHeight <= 0 || double.IsInfinity(availableWidth) || double.IsInfinity(availableHeight))
        {
            return baseSize;
        }

        var quantizedWidth = _enableFontStabilization ? QuantizeLength(availableWidth) : availableWidth;
        var quantizedHeight = _enableFontStabilization ? QuantizeLength(availableHeight) : availableHeight;
        var resolved = FitMaxFont(item.Text, MinFontSize, baseSize, quantizedWidth, quantizedHeight);
        if (!Fits(item.Text, resolved, availableWidth, availableHeight))
        {
            resolved = FitMaxFont(item.Text, MinFontSize, baseSize, availableWidth, availableHeight);
        }

        if (!Fits(item.Text, MinFontSize, availableWidth, availableHeight))
        {
            var upper = Math.Min(MinFontSize, baseSize);
            resolved = FitMaxFont(item.Text, MinFallbackFontSize, upper, availableWidth, availableHeight);
        }

        if (!_enableFontStabilization)
        {
            return Math.Clamp(resolved, MinFallbackFontSize, MaxFontSize);
        }

        if (_fontSizeCache.TryGetValue(cacheKey, out var last))
        {
            if (resolved < last)
            {
                return Math.Clamp(resolved, MinFallbackFontSize, MaxFontSize);
            }

            if (resolved - last < FontHysteresisThreshold)
            {
                return Math.Clamp(last, MinFallbackFontSize, MaxFontSize);
            }
        }

        return Math.Clamp(resolved, MinFallbackFontSize, MaxFontSize);
    }

    private bool Fits(string text, double fontSize, double maxWidth, double maxHeight)
    {
        if (fontSize <= 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            fontSize,
            _foreground,
            dpi.PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            // WHY: Keep MaxTextHeight unset so we can detect vertical overflow via measured Height.
            TextAlignment = TextAlignment.Left
        };

        return formatted.Width <= maxWidth && formatted.Height <= maxHeight;
    }

    private double FitMaxFont(string text, double minFont, double maxFont, double maxWidth, double maxHeight)
    {
        var lower = Math.Clamp(minFont, MinFallbackFontSize, MaxFontSize);
        var upper = Math.Clamp(maxFont, lower, MaxFontSize);
        if (!Fits(text, lower, maxWidth, maxHeight))
        {
            return lower;
        }

        var best = lower;
        for (var i = 0; i < FitIterations; i++)
        {
            var mid = (lower + upper) / 2.0;
            if (Fits(text, mid, maxWidth, maxHeight))
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

    private static double QuantizeLength(double value)
    {
        if (FontQuantizeStepPx <= 0 || value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return value;
        }

        return Math.Round(value / FontQuantizeStepPx, MidpointRounding.AwayFromZero) * FontQuantizeStepPx;
    }

    private string BuildFontCacheKey(OverlayItem item, Rect rect)
    {
        var x = _enableFontStabilization ? QuantizeLength(rect.X) : rect.X;
        var y = _enableFontStabilization ? QuantizeLength(rect.Y) : rect.Y;
        var width = _enableFontStabilization ? QuantizeLength(rect.Width) : rect.Width;
        var height = _enableFontStabilization ? QuantizeLength(rect.Height) : rect.Height;
        var text = item.Text ?? string.Empty;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0}|{1:0.0}|{2:0.0}|{3:0.0}|{4}",
            x,
            y,
            width,
            height,
            text);
    }

    private static Brush ParseBrush(string value, Brush fallback)
    {
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
    }

    private static Brush ApplyOverlayOpacity(Brush brush, double opacity)
    {
        if (brush is not SolidColorBrush solid)
        {
            return brush;
        }

        var clamped = Math.Clamp(opacity, 0.0, 1.0);
        var alpha = (byte)Math.Round(clamped * 255.0);
        return new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint WindowDisplayAffinityExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }
}

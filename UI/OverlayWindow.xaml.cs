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
    private Brush _background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
    private double _fontSize = 18;
    private static readonly Thickness OverlayPadding = new(4, 2, 4, 2);
    private const double MinLineHeightScale = 0.75;
    private const double MaxLineHeightScale = 0.95;
    private const double MinOccupancyRatio = 0.05;
    private const double MaxOccupancyRatio = 0.35;
    private const double MinFontSize = 8;
    private const double MaxFontSize = 192;
    private const int FitIterations = 7;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void ApplyStyle(AppSettings settings)
    {
        _fontSize = settings.OverlayFontSize;
        _foreground = ParseBrush(settings.OverlayForeground, Brushes.White);
        _background = ParseBrush(settings.OverlayBackground, new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)));
    }

    public void UpdateItems(IReadOnlyList<OverlayItem> items)
    {
        OverlayCanvas.Children.Clear();
        foreach (var item in items)
        {
            var availableWidth = item.Rect.Width > 0
                ? Math.Max(0, item.Rect.Width - OverlayPadding.Left - OverlayPadding.Right)
                : double.PositiveInfinity;
            var availableHeight = item.Rect.Height > 0
                ? Math.Max(0, item.Rect.Height - OverlayPadding.Top - OverlayPadding.Bottom)
                : double.PositiveInfinity;
            var fontSize = ResolveFontSize(item, availableWidth, availableHeight);
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
                MaxWidth = item.Rect.Width > 0 ? item.Rect.Width : double.PositiveInfinity,
                MaxHeight = item.Rect.Height > 0 ? item.Rect.Height : double.PositiveInfinity
            };

            if (item.Rect.Width > 0)
            {
                container.Width = item.Rect.Width;
                textBlock.MaxWidth = availableWidth;
            }

            if (item.Rect.Height > 0)
            {
                container.Height = item.Rect.Height;
                textBlock.MaxHeight = availableHeight;
            }

            Canvas.SetLeft(container, item.Rect.X);
            Canvas.SetTop(container, item.Rect.Y);
            OverlayCanvas.Children.Add(container);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var extended = GetWindowLongPtr(hwnd, GwlExStyle);
        var updated = new IntPtr(extended.ToInt64() | WsExTransparent | WsExToolWindow | WsExNoActivate);
        SetWindowLongPtr(hwnd, GwlExStyle, updated);
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

    private double ResolveFontSize(OverlayItem item, double availableWidth, double availableHeight)
    {
        var baseSize = GetBaseFontSize(item);
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            return baseSize;
        }

        if (availableWidth <= 0 || availableHeight <= 0 || double.IsInfinity(availableWidth) || double.IsInfinity(availableHeight))
        {
            return baseSize;
        }

        if (Fits(item.Text, baseSize, availableWidth, availableHeight))
        {
            return baseSize;
        }

        var low = MinFontSize;
        var high = baseSize;
        for (var i = 0; i < FitIterations; i++)
        {
            var mid = (low + high) / 2.0;
            if (Fits(item.Text, mid, availableWidth, availableHeight))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private double GetBaseFontSize(OverlayItem item)
    {
        var lineHeight = item.LineHeight;
        if (lineHeight <= 0 && item.LineCount > 0 && item.Rect.Height > 0)
        {
            lineHeight = item.Rect.Height / item.LineCount;
        }

        var baseScale = GetOccupancyScale(item.Rect);
        var baseSize = lineHeight > 0 ? lineHeight * baseScale : _fontSize;
        if (double.IsNaN(baseSize) || double.IsInfinity(baseSize) || baseSize <= 0)
        {
            return _fontSize;
        }

        return Math.Clamp(baseSize, MinFontSize, MaxFontSize);
    }

    private double GetOccupancyScale(Rect rect)
    {
        var screenHeight = SystemParameters.VirtualScreenHeight;
        if (screenHeight <= 0 || rect.Height <= 0)
        {
            return MinLineHeightScale;
        }

        var ratio = rect.Height / screenHeight;
        ratio = Math.Clamp(ratio, MinOccupancyRatio, MaxOccupancyRatio);
        var t = (ratio - MinOccupancyRatio) / (MaxOccupancyRatio - MinOccupancyRatio);
        // WHY: Small OCR boxes get conservative sizing to reduce overflow; large boxes can be larger.
        return MinLineHeightScale + (MaxLineHeightScale - MinLineHeightScale) * t;
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
            MaxTextHeight = maxHeight,
            TextAlignment = TextAlignment.Left
        };

        return formatted.Width <= maxWidth && formatted.Height <= maxHeight;
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

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }
}

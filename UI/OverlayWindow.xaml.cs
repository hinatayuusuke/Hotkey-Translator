using System;
using System.Collections.Generic;
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
            var textBlock = new TextBlock
            {
                Text = item.Text,
                Foreground = _foreground,
                FontSize = _fontSize,
                TextWrapping = TextWrapping.Wrap
            };

            var container = new Border
            {
                Background = _background,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 2, 4, 2),
                Child = textBlock,
                MaxWidth = item.Rect.Width > 0 ? item.Rect.Width : double.PositiveInfinity
            };

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

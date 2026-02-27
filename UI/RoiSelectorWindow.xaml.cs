using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.UI;

public partial class RoiSelectorWindow : Window
{
    private Point? _start;
    private readonly Rect _frameBounds;

    public RoiSelectorWindow(Rect frameBounds)
    {
        _frameBounds = frameBounds;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public Rect? SelectedRect { get; private set; }
    public NormalizedRect? SelectedNormalizedRect { get; private set; }
    public event Action<Rect?>? PreviewRectChanged;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        // WHY: ROI selection relies on Esc cancellation; force focus to this window so keyboard events are reliable.
        Activate();
        Focus();
        _ = Keyboard.Focus(this);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        SelectionRect.Visibility = Visibility.Visible;
        UpdateSelection(_start.Value, _start.Value);
        EmitPreviewRect(NormalizeRect(_start.Value, _start.Value));
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_start is null)
        {
            return;
        }

        var end = e.GetPosition(this);
        var rect = NormalizeRect(_start.Value, end);
        UpdateSelection(_start.Value, end);
        EmitPreviewRect(rect);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null)
        {
            return;
        }

        ReleaseMouseCapture();
        var end = e.GetPosition(this);
        var rect = NormalizeRect(_start.Value, end);
        // WHY: Convert window-local ROI to screen coordinates before DPI/device conversion.
        var screenTopLeft = PointToScreen(new Point(rect.X, rect.Y));
        var screenBottomRight = PointToScreen(new Point(rect.Right, rect.Bottom));
        var screenRect = new Rect(screenTopLeft, screenBottomRight);
        var deviceRect = DpiHelper.DipRectToDevice(this, screenRect);

        SelectedRect = deviceRect.Width <= 0 || deviceRect.Height <= 0 ? null : deviceRect;
        if (SelectedRect.HasValue && _frameBounds.Width > 0 && _frameBounds.Height > 0)
        {
            SelectedNormalizedRect = NormalizedRect.FromAbsolute(SelectedRect.Value, _frameBounds);
        }
        DialogResult = SelectedRect.HasValue;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelSelection();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CancelSelection();
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CancelSelection();
    }

    private void UpdateSelection(Point start, Point end)
    {
        var rect = NormalizeRect(start, end);
        Canvas.SetLeft(SelectionRect, rect.X);
        Canvas.SetTop(SelectionRect, rect.Y);
        SelectionRect.Width = rect.Width;
        SelectionRect.Height = rect.Height;
    }

    private static Rect NormalizeRect(Point start, Point end)
    {
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        var width = Math.Abs(start.X - end.X);
        var height = Math.Abs(start.Y - end.Y);
        return new Rect(x, y, width, height);
    }

    private void CancelSelection()
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        _start = null;
        SelectionRect.Visibility = Visibility.Collapsed;
        SelectedRect = null;
        SelectedNormalizedRect = null;
        PreviewRectChanged?.Invoke(null);
        DialogResult = false;
    }

    private void EmitPreviewRect(Rect localRect)
    {
        if (localRect.Width <= 0 || localRect.Height <= 0)
        {
            PreviewRectChanged?.Invoke(null);
            return;
        }

        // WHY: Hook ROI preview uses the same absolute device-space coordinates as persisted ROI.
        var screenTopLeft = PointToScreen(new Point(localRect.X, localRect.Y));
        var screenBottomRight = PointToScreen(new Point(localRect.Right, localRect.Bottom));
        var screenRect = new Rect(screenTopLeft, screenBottomRight);
        var deviceRect = DpiHelper.DipRectToDevice(this, screenRect);
        PreviewRectChanged?.Invoke(deviceRect.Width > 0 && deviceRect.Height > 0 ? deviceRect : null);
    }
}

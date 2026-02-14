using System;
using System.Windows;
using System.Windows.Media;
using Hotkey_Translator.UI;

namespace Hotkey_Translator.Services.Application;

internal sealed class PreviewZoomCoordinator : IDisposable
{
    private readonly Window _owner;
    private readonly Func<OcrPreviewZoomWindow> _windowFactory;
    private OcrPreviewZoomWindow? _zoomWindow;

    public PreviewZoomCoordinator(Window owner, Func<OcrPreviewZoomWindow>? windowFactory = null)
    {
        _owner = owner;
        _windowFactory = windowFactory ?? (() => new OcrPreviewZoomWindow());
    }

    public void ShowOrActivate(ImageSource? source)
    {
        var window = EnsureWindow();
        window.SetImage(source);
        if (!window.IsVisible)
        {
            window.Show();
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    public void UpdateImage(ImageSource? source)
    {
        _zoomWindow?.SetImage(source);
    }

    public void Dispose()
    {
        if (_zoomWindow == null)
        {
            return;
        }

        _zoomWindow.Closed -= OnZoomWindowClosed;
        _zoomWindow.Close();
        _zoomWindow = null;
    }

    private OcrPreviewZoomWindow EnsureWindow()
    {
        if (_zoomWindow != null)
        {
            return _zoomWindow;
        }

        var window = _windowFactory();
        window.Owner = _owner;
        window.Closed += OnZoomWindowClosed;
        _zoomWindow = window;
        return _zoomWindow;
    }

    private void OnZoomWindowClosed(object? sender, EventArgs e)
    {
        if (_zoomWindow == null)
        {
            return;
        }

        _zoomWindow.Closed -= OnZoomWindowClosed;
        _zoomWindow = null;
    }
}

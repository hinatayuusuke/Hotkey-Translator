using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;
using Hotkey_Translator.UI;

namespace Hotkey_Translator.Services;

public sealed class OverlayPresenter
{
    private readonly OverlayWindow _window;
    private IReadOnlyList<OverlayItem> _lastItems = new List<OverlayItem>();
    private bool _isEnabled = true;

    public event Action? Shown;
    public event Action? Hidden;
    public event Action? Updated;

    public OverlayPresenter(OverlayWindow window)
    {
        _window = window;
    }

    public void Show()
    {
        if (!_isEnabled)
        {
            return;
        }

        _window.Dispatcher.Invoke(() =>
        {
            if (!_window.IsVisible)
            {
                _window.Show();
                Shown?.Invoke();
            }
        });
    }

    public void Hide()
    {
        _window.Dispatcher.Invoke(() =>
        {
            if (_window.IsVisible)
            {
                _window.Hide();
                Hidden?.Invoke();
            }
        });
    }

    public void Update(IReadOnlyList<OverlayItem> items)
    {
        _lastItems = items.ToList();
        // NOTE: Keep latest items while disabled so toggle can show the newest overlay.
        if (!_isEnabled)
        {
            return;
        }

        _window.Dispatcher.Invoke(() =>
        {
            var converted = ConvertToDip(_lastItems);
            _window.UpdateItems(converted);
            Updated?.Invoke();
        });
    }

    public void ShowLast()
    {
        if (!_isEnabled)
        {
            return;
        }

        if (_lastItems.Count == 0)
        {
            return;
        }

        _window.Dispatcher.Invoke(() =>
        {
            var converted = ConvertToDip(_lastItems);
            _window.UpdateItems(converted);
            Updated?.Invoke();
        });
    }

    private IReadOnlyList<OverlayItem> ConvertToDip(IReadOnlyList<OverlayItem> items)
    {
        var converted = new List<OverlayItem>(items.Count);
        foreach (var item in items)
        {
            var rect = DpiHelper.ScreenRectToWindowDip(_window, item.Rect);
            var lineHeight = item.LineHeight;
            if (lineHeight > 0 && item.Rect.Height > 0 && rect.Height > 0)
            {
                lineHeight *= rect.Height / item.Rect.Height;
            }

            converted.Add(new OverlayItem(item.Text, rect, item.LineCount, lineHeight));
        }

        return converted;
    }

    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
        if (_isEnabled)
        {
            ShowLast();
            return;
        }

        Hide();
    }
}

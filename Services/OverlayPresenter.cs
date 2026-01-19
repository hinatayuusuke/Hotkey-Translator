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

    public OverlayPresenter(OverlayWindow window)
    {
        _window = window;
    }

    public void Show()
    {
        _window.Dispatcher.Invoke(() =>
        {
            if (!_window.IsVisible)
            {
                _window.Show();
            }
        });
    }

    public void Hide()
    {
        _window.Dispatcher.Invoke(() => _window.Hide());
    }

    public void Update(IReadOnlyList<OverlayItem> items)
    {
        _lastItems = items.ToList();
        _window.Dispatcher.Invoke(() =>
        {
            var converted = ConvertToDip(_lastItems);
            _window.UpdateItems(converted);
        });
    }

    public void ShowLast()
    {
        if (_lastItems.Count == 0)
        {
            return;
        }

        _window.Dispatcher.Invoke(() =>
        {
            var converted = ConvertToDip(_lastItems);
            _window.UpdateItems(converted);
        });
    }

    private IReadOnlyList<OverlayItem> ConvertToDip(IReadOnlyList<OverlayItem> items)
    {
        var converted = new List<OverlayItem>(items.Count);
        foreach (var item in items)
        {
            var rect = DpiHelper.DeviceRectToDip(_window, item.Rect);
            converted.Add(new OverlayItem(item.Text, rect));
        }

        return converted;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Hotkey_Translator.Models;
using Hotkey_Translator.UI;

namespace Hotkey_Translator.Services;

public sealed class OverlayPresenter
{
    private readonly OverlayWindow _window;
    private readonly AppLogger? _logger;
    private IReadOnlyList<OverlayItem> _lastItems = new List<OverlayItem>();
    private bool _isEnabled = true;
    private bool _perfLogEnabled;
    private int _perfLogThresholdMs;

    public event Action? Shown;
    public event Action? Hidden;
    public event Action? Updated;

    public OverlayPresenter(OverlayWindow window, AppLogger? logger = null)
    {
        _window = window;
        _logger = logger;
    }

    public void Show()
    {
        if (!_isEnabled)
        {
            return;
        }

        InvokeOnUi("OverlayShow", measureRender: true, () =>
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
        InvokeOnUi("OverlayHide", measureRender: false, () =>
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

        InvokeOnUi("OverlayUpdate", measureRender: true, () =>
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

        InvokeOnUi("OverlayShowLast", measureRender: true, () =>
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

    public void UpdatePerfLogging(bool enabled, int thresholdMs)
    {
        _perfLogEnabled = enabled;
        _perfLogThresholdMs = Math.Max(0, thresholdMs);
    }

    private void InvokeOnUi(string label, bool measureRender, Action action)
    {
        if (!_perfLogEnabled || _logger == null)
        {
            if (_window.Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            _window.Dispatcher.Invoke(action);
            return;
        }

        if (_window.Dispatcher.CheckAccess())
        {
            var localStopwatch = Stopwatch.StartNew();
            action();
            localStopwatch.Stop();
            LogInvokeLatency(label, localStopwatch.ElapsedMilliseconds);
            if (measureRender)
            {
                QueueRenderLatency(label);
            }
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _window.Dispatcher.Invoke(() =>
        {
            action();
            if (measureRender)
            {
                QueueRenderLatency(label);
            }
        });
        stopwatch.Stop();
        LogInvokeLatency(label, stopwatch.ElapsedMilliseconds);
    }

    private void QueueRenderLatency(string label)
    {
        if (!_perfLogEnabled || _logger == null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds >= _perfLogThresholdMs)
            {
                _logger.Info($"[Perf] {label}Render={stopwatch.ElapsedMilliseconds}ms.");
            }
        };
        CompositionTarget.Rendering += handler;
    }

    private void LogInvokeLatency(string label, long elapsedMs)
    {
        if (!_perfLogEnabled || _logger == null)
        {
            return;
        }

        if (elapsedMs >= _perfLogThresholdMs)
        {
            _logger.Info($"[Perf] {label}Invoke={elapsedMs}ms.");
        }
    }
}

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
    private Rect? _lastSmallBoxClipScreenRect;
    private bool _isEnabled = true;
    private bool _autoTranslateBadgeVisible;
    private Rect? _autoTranslateBadgeAnchorScreenRect;
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
            }

            _window.SetOverlayVisibility(true);
            _window.SetAutoTranslateBadgeVisible(_autoTranslateBadgeVisible, ToWindowDipRect(_autoTranslateBadgeAnchorScreenRect) ?? Rect.Empty);
            Shown?.Invoke();
        });
    }

    public void Hide()
    {
        InvokeOnUi("OverlayHide", measureRender: false, () =>
        {
            // WHY: Keep the window resident to avoid DWM flash; only toggle overlay visibility.
            _window.UpdateItems(Array.Empty<OverlayItem>());
            _window.HideLoadingSpinner();
            _window.SetAutoTranslateBadgeVisible(false, Rect.Empty);
            _window.SetOverlayVisibility(false);
            Hidden?.Invoke();
        });
    }

    public void Update(IReadOnlyList<OverlayItem> items, Rect? smallBoxClipScreenRect = null)
    {
        _lastItems = items.ToList();
        _lastSmallBoxClipScreenRect = NormalizeRect(smallBoxClipScreenRect);
        // NOTE: Keep latest items while disabled so toggle can show the newest overlay.
        if (!_isEnabled)
        {
            return;
        }

        InvokeOnUi("OverlayUpdate", measureRender: true, () =>
        {
            _window.SetSmallBoxClipBounds(ToWindowDipRect(_lastSmallBoxClipScreenRect));
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
            _window.SetSmallBoxClipBounds(ToWindowDipRect(_lastSmallBoxClipScreenRect));
            var converted = ConvertToDip(_lastItems);
            _window.UpdateItems(converted);
            Updated?.Invoke();
        });
    }

    public void ClearOverlay()
    {
        _lastItems = Array.Empty<OverlayItem>();
        if (!_isEnabled)
        {
            return;
        }

        InvokeOnUi("OverlayClear", measureRender: false, () =>
        {
            _window.UpdateItems(Array.Empty<OverlayItem>());
            Updated?.Invoke();
        });
    }

    public void ShowToast(string text, Rect anchor)
    {
        InvokeOnUi("OverlayToast", measureRender: false, () =>
        {
            _window.ShowToast(text, anchor);
        });
    }

    public void ShowLoadingSpinner(Rect anchor)
    {
        if (!_isEnabled)
        {
            return;
        }

        InvokeOnUi("OverlaySpinnerShow", measureRender: false, () =>
        {
            var anchorDip = anchor.IsEmpty ? Rect.Empty : DpiHelper.ScreenRectToWindowDip(_window, anchor);
            _window.ShowLoadingSpinner(anchorDip);
        });
    }

    public void HideLoadingSpinner()
    {
        InvokeOnUi("OverlaySpinnerHide", measureRender: false, () =>
        {
            _window.HideLoadingSpinner();
        });
    }

    public void SetAutoTranslateBadgeVisible(bool visible, Rect? anchorScreenRect = null)
    {
        _autoTranslateBadgeVisible = visible;
        _autoTranslateBadgeAnchorScreenRect = NormalizeRect(anchorScreenRect);
        if (!_isEnabled)
        {
            return;
        }

        InvokeOnUi("OverlayAutoBadge", measureRender: false, () =>
        {
            _window.SetAutoTranslateBadgeVisible(visible, ToWindowDipRect(_autoTranslateBadgeAnchorScreenRect) ?? Rect.Empty);
        });
    }

    public void ClearAutoTranslateBadge()
    {
        _autoTranslateBadgeVisible = false;
        _autoTranslateBadgeAnchorScreenRect = null;
        InvokeOnUi("OverlayAutoBadgeClear", measureRender: false, () =>
        {
            _window.SetAutoTranslateBadgeVisible(false, Rect.Empty);
        });
    }

    public bool TryResolveHookFontPx(OverlayItem item, Rect frameBoundsScreen, uint canvasH, out float fontPx)
    {
        fontPx = 0;
        if (_window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        var resolved = false;
        var resolvedFontPx = 0f;
        void Resolve()
        {
            resolved = _window.TryResolveHookFontPx(item, frameBoundsScreen, canvasH, out resolvedFontPx);
        }

        if (_window.Dispatcher.CheckAccess())
        {
            Resolve();
            fontPx = resolvedFontPx;
            return resolved;
        }

        _window.Dispatcher.Invoke(Resolve);
        fontPx = resolvedFontPx;
        return resolved;
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

    private Rect? ToWindowDipRect(Rect? screenRect)
    {
        if (screenRect is not { } rect || rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var dip = DpiHelper.ScreenRectToWindowDip(_window, rect);
        if (dip.IsEmpty || dip.Width <= 0 || dip.Height <= 0)
        {
            return null;
        }

        return dip;
    }

    private static Rect? NormalizeRect(Rect? rect)
    {
        if (rect is not { } value || value.IsEmpty || value.Width <= 0 || value.Height <= 0)
        {
            return null;
        }

        return value;
    }

    public void SetEnabled(bool enabled, bool showLast = true)
    {
        _isEnabled = enabled;
        if (_isEnabled)
        {
            Show();
            if (showLast)
            {
                ShowLast();
            }
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

using System;
using System.Windows;
using System.Windows.Threading;

namespace Hotkey_Translator.Services.Application;

internal sealed class DrawerLayoutController
{
    private readonly Window _window;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _isDrawerOpenAccessor;
    private readonly Func<double> _drawerHeightAccessor;
    private readonly double _fallbackDrawerHeight;
    private readonly double _resizeTolerance;
    private bool _drawerAutoExpanded;
    private double _drawerAutoExpandedDelta;
    private double _drawerAutoExpandedTargetHeight;
    private bool _drawerResizeScheduled;

    public DrawerLayoutController(
        Window window,
        Dispatcher dispatcher,
        Func<bool> isDrawerOpenAccessor,
        Func<double> drawerHeightAccessor,
        double fallbackDrawerHeight,
        double resizeTolerance)
    {
        _window = window;
        _dispatcher = dispatcher;
        _isDrawerOpenAccessor = isDrawerOpenAccessor;
        _drawerHeightAccessor = drawerHeightAccessor;
        _fallbackDrawerHeight = fallbackDrawerHeight;
        _resizeTolerance = resizeTolerance;
    }

    public void SyncForCurrentState()
    {
        if (_isDrawerOpenAccessor())
        {
            ScheduleDrawerAutoExpand();
            return;
        }

        TryRestoreWindowHeightForDrawerClose();
    }

    public void SyncStartupState()
    {
        if (!_isDrawerOpenAccessor() || _drawerAutoExpanded || _window.WindowState != WindowState.Normal)
        {
            return;
        }

        // WHY: Apply startup drawer expansion before first render to avoid visible "short -> expanded" resize flicker.
        TryAutoExpandWindowForDrawerOpen();
    }

    public void Reset()
    {
        _drawerAutoExpanded = false;
        _drawerAutoExpandedDelta = 0;
        _drawerAutoExpandedTargetHeight = 0;
        _drawerResizeScheduled = false;
    }

    private void ScheduleDrawerAutoExpand()
    {
        if (_drawerResizeScheduled || _drawerAutoExpanded || _window.WindowState != WindowState.Normal)
        {
            return;
        }

        _drawerResizeScheduled = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _drawerResizeScheduled = false;
            TryAutoExpandWindowForDrawerOpen();
        }));
    }

    private void TryAutoExpandWindowForDrawerOpen()
    {
        if (!_isDrawerOpenAccessor() || _drawerAutoExpanded || _window.WindowState != WindowState.Normal)
        {
            return;
        }

        var currentHeight = ResolveCurrentWindowHeight();
        var workArea = SystemParameters.WorkArea;
        var maxHeight = Math.Max(_window.MinHeight, workArea.Height);
        if (currentHeight >= maxHeight - 1)
        {
            return;
        }

        var desiredIncrease = ResolveDrawerExpansionHeight();
        if (desiredIncrease <= 0)
        {
            return;
        }

        var expandedHeight = Math.Min(currentHeight + desiredIncrease, maxHeight);
        var appliedIncrease = expandedHeight - currentHeight;
        if (appliedIncrease <= 1)
        {
            return;
        }

        _window.Height = expandedHeight;
        KeepWindowWithinWorkArea(expandedHeight, workArea);
        _drawerAutoExpanded = true;
        _drawerAutoExpandedDelta = appliedIncrease;
        _drawerAutoExpandedTargetHeight = expandedHeight;
    }

    private void TryRestoreWindowHeightForDrawerClose()
    {
        if (!_drawerAutoExpanded || _drawerAutoExpandedDelta <= 0)
        {
            return;
        }

        if (_window.WindowState != WindowState.Normal)
        {
            Reset();
            return;
        }

        var currentHeight = ResolveCurrentWindowHeight();
        var isNearAutoExpandedHeight =
            Math.Abs(currentHeight - _drawerAutoExpandedTargetHeight) <= _resizeTolerance;
        if (isNearAutoExpandedHeight)
        {
            var workArea = SystemParameters.WorkArea;
            var restoredHeight = Math.Max(_window.MinHeight, currentHeight - _drawerAutoExpandedDelta);
            restoredHeight = Math.Min(restoredHeight, workArea.Height);
            _window.Height = restoredHeight;
            KeepWindowWithinWorkArea(restoredHeight, workArea);
        }

        Reset();
    }

    private double ResolveDrawerExpansionHeight()
    {
        var measured = _drawerHeightAccessor();
        if (measured > 0)
        {
            return measured;
        }

        return _fallbackDrawerHeight;
    }

    private double ResolveCurrentWindowHeight()
    {
        if (!double.IsNaN(_window.Height) && _window.Height > 0)
        {
            return _window.Height;
        }

        if (_window.ActualHeight > 0)
        {
            return _window.ActualHeight;
        }

        return Math.Max(_window.MinHeight, _fallbackDrawerHeight);
    }

    private void KeepWindowWithinWorkArea(double windowHeight, Rect workArea)
    {
        var currentTop = _window.Top;
        if (double.IsNaN(currentTop))
        {
            return;
        }

        var minTop = workArea.Top;
        var maxTop = Math.Max(minTop, workArea.Bottom - windowHeight);
        var clampedTop = Math.Min(Math.Max(currentTop, minTop), maxTop);
        if (Math.Abs(clampedTop - currentTop) > 0.5)
        {
            _window.Top = clampedTop;
        }
    }
}

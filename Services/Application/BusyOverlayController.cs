using System;
using System.Windows.Threading;
using Hotkey_Translator.Services;
using Hotkey_Translator.ViewModels;

namespace Hotkey_Translator.Services.Application;

internal sealed class BusyOverlayController
{
    private readonly Dispatcher _dispatcher;
    private readonly RuntimeStatusViewModel _runtimeStatus;
    private int _scopedDepth;
    private bool _previousIsBusy;
    private string _previousMessage = string.Empty;
    private bool _previousIndeterminate = true;
    private double _previousPercent;

    public BusyOverlayController(Dispatcher dispatcher, RuntimeStatusViewModel runtimeStatus)
    {
        _dispatcher = dispatcher;
        _runtimeStatus = runtimeStatus;
    }

    public bool IsScopedBusyActive => _scopedDepth > 0;

    public void SetBusyOverlay(bool visible, string? message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => SetBusyOverlay(visible, message));
            return;
        }

        _runtimeStatus.IsBusy = visible;
        _runtimeStatus.BusyProgressIsIndeterminate = true;
        if (!visible)
        {
            _runtimeStatus.BusyProgressPercent = 0;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            _runtimeStatus.BusyMessage = message;
        }
    }

    public void BeginProgressScope(string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => BeginProgressScope(message));
            return;
        }

        if (_scopedDepth == 0)
        {
            _previousIsBusy = _runtimeStatus.IsBusy;
            _previousMessage = _runtimeStatus.BusyMessage;
            _previousIndeterminate = _runtimeStatus.BusyProgressIsIndeterminate;
            _previousPercent = _runtimeStatus.BusyProgressPercent;
        }

        _scopedDepth++;
        _runtimeStatus.IsBusy = true;
        _runtimeStatus.BusyProgressIsIndeterminate = true;
        _runtimeStatus.BusyProgressPercent = 0;
        _runtimeStatus.BusyMessage = message;
    }

    public void UpdateProgress(CapabilityInstallProgress progress)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => UpdateProgress(progress));
            return;
        }

        _runtimeStatus.IsBusy = true;
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            _runtimeStatus.BusyMessage = progress.Message;
        }

        if (progress.Percent < 0)
        {
            _runtimeStatus.BusyProgressIsIndeterminate = true;
            return;
        }

        _runtimeStatus.BusyProgressIsIndeterminate = false;
        _runtimeStatus.BusyProgressPercent = Math.Clamp(progress.Percent, 0, 100);
    }

    public void EndProgressScope()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(EndProgressScope);
            return;
        }

        if (_scopedDepth <= 0)
        {
            return;
        }

        _scopedDepth--;
        if (_scopedDepth > 0)
        {
            return;
        }

        if (_previousIsBusy)
        {
            _runtimeStatus.IsBusy = true;
            _runtimeStatus.BusyMessage = _previousMessage;
            _runtimeStatus.BusyProgressIsIndeterminate = _previousIndeterminate;
            _runtimeStatus.BusyProgressPercent = _previousPercent;
            return;
        }

        _runtimeStatus.IsBusy = false;
        _runtimeStatus.BusyProgressIsIndeterminate = true;
        _runtimeStatus.BusyProgressPercent = 0;
    }
}

using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Capture;

internal sealed class CaptureTargetResolver
{
    private readonly WindowBindingService _windowBindingService = new();
    private readonly AppLogger _logger;
    private string? _captureTargetResolutionState;

    public CaptureTargetResolver(AppLogger logger)
    {
        _logger = logger;
    }

    public CaptureRequest BuildCaptureRequest(AppSettings settings)
    {
        var request = new CaptureRequest(settings.CaptureMode, null);
        if (settings.CaptureMode != CaptureMode.ActiveWindow || !settings.EnableFixedCaptureWindow)
        {
            TrackCaptureTargetResolution(null);
            return request;
        }

        if (_windowBindingService.TryResolveWindowHandle(settings, out var hwnd, out var reason))
        {
            TrackCaptureTargetResolution($"Fixed capture target resolved: hwnd=0x{hwnd.ToInt64():X}.");
            return request with { TargetWindowHandle = hwnd };
        }

        TrackCaptureTargetResolution($"Fixed capture target invalid; fallback to active window. Reason: {reason ?? "unknown"}.");
        return request;
    }

    private void TrackCaptureTargetResolution(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _captureTargetResolutionState = null;
            return;
        }

        if (string.Equals(_captureTargetResolutionState, message, StringComparison.Ordinal))
        {
            return;
        }

        _captureTargetResolutionState = message;
        _logger.Info(message);
    }
}

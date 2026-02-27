using System;
using System.Runtime.InteropServices;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Capture;

internal sealed class CaptureTargetResolver
{
    private readonly WindowBindingService _windowBindingService = new();
    private readonly AppLogger _logger;
    private string? _captureTargetResolutionState;
    private IntPtr _lastResolvedTargetHwnd;

    public CaptureTargetResolver(AppLogger logger)
    {
        _logger = logger;
    }

    public CaptureRequest BuildCaptureRequest(AppSettings settings)
    {
        var request = new CaptureRequest(settings.CaptureMode, null);
        if (settings.CaptureMode != CaptureMode.ActiveWindow)
        {
            TrackCaptureTargetResolution(null);
            _lastResolvedTargetHwnd = IntPtr.Zero;
            return request;
        }

        if (TryResolveMirrorScalingWindow(settings, out var mirrorHwnd))
        {
            TrackResolvedTargetSwitch(mirrorHwnd);
            TrackCaptureTargetResolution(
                $"stage=mirror_capture event=target_resolved hwnd=0x{mirrorHwnd.ToInt64():X} source=findwindow.");
            return request with { TargetWindowHandle = mirrorHwnd };
        }

        if (!settings.EnableFixedCaptureWindow)
        {
            TrackCaptureTargetResolution(null);
            _lastResolvedTargetHwnd = IntPtr.Zero;
            return request;
        }

        if (_windowBindingService.TryResolveWindowHandle(settings, out var hwnd, out var reason))
        {
            TrackResolvedTargetSwitch(hwnd);
            TrackCaptureTargetResolution($"Fixed capture target resolved: hwnd=0x{hwnd.ToInt64():X}.");
            return request with { TargetWindowHandle = hwnd };
        }

        _lastResolvedTargetHwnd = IntPtr.Zero;
        TrackCaptureTargetResolution($"Fixed capture target invalid; fallback to active window. Reason: {reason ?? "unknown"}.");
        return request;
    }

    private static bool TryResolveMirrorScalingWindow(AppSettings settings, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (!settings.EnableMirrorFullscreenMode)
        {
            return false;
        }

        hwnd = FindWindow(ScalingWindowClassName, null);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            hwnd = IntPtr.Zero;
            return false;
        }

        return true;
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

    private void TrackResolvedTargetSwitch(IntPtr resolvedHwnd)
    {
        if (resolvedHwnd == IntPtr.Zero || resolvedHwnd == _lastResolvedTargetHwnd)
        {
            return;
        }

        var previous = _lastResolvedTargetHwnd;
        _lastResolvedTargetHwnd = resolvedHwnd;
        var details = BuildTargetDetails(resolvedHwnd);
        _logger.Info(
            $"stage=capture_target event=resolved_switch old=0x{previous.ToInt64():X} new=0x{resolvedHwnd.ToInt64():X} {details}.");
    }

    private static string BuildTargetDetails(IntPtr hwnd)
    {
        var client = "client=unknown";
        if (GetClientRect(hwnd, out var clientRect))
        {
            var w = Math.Max(0, clientRect.Right - clientRect.Left);
            var h = Math.Max(0, clientRect.Bottom - clientRect.Top);
            client = $"client={w}x{h}";
        }

        var monitorText = "monitor=unknown";
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var width = Math.Max(0, info.Monitor.Right - info.Monitor.Left);
                var height = Math.Max(0, info.Monitor.Bottom - info.Monitor.Top);
                monitorText = $"monitor=[{info.Monitor.Left},{info.Monitor.Top},{width},{height}]";
            }
        }

        var dpi = GetDpiForWindow(hwnd);
        var dpiText = dpi > 0 ? $"dpi={dpi}" : "dpi=unknown";
        return $"{client} {monitorText} {dpiText}";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const int MonitorDefaultToNearest = 2;
    private const string ScalingWindowClassName = "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}

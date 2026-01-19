using System;
using System.Runtime.InteropServices;
using System.Windows;
using Hotkey_Translator.Models;
using Windows.Graphics.Capture;

namespace Hotkey_Translator.Services;

public sealed class WgcCaptureProvider : ICaptureProvider
{
    private readonly AppLogger _logger;

    public WgcCaptureProvider(AppLogger logger)
    {
        _logger = logger;
    }

    public CaptureProviderKind Kind => CaptureProviderKind.Wgc;

    public bool IsEnabled(AppSettings settings)
    {
        return settings.EnableWgcCapture && GraphicsCaptureSession.IsSupported();
    }

    public bool TryGetBounds(CaptureMode mode, out Rect bounds)
    {
        bounds = mode switch
        {
            CaptureMode.ActiveWindow => GetActiveWindowBounds() ?? Rect.Empty,
            _ => GetPrimaryMonitorBounds()
        };

        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool TryCapture(CaptureMode mode, out CaptureFrame frame, out string? error)
    {
        frame = null!;
        error = "WGC capture is not wired yet.";
        _logger.Info("WGC capture requested, but provider is not wired yet.");
        return false;
    }

    private static Rect? GetActiveWindowBounds()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (GetWindowRect(hwnd, out var rect))
        {
            return rect.ToRect();
        }

        return null;
    }

    private static Rect GetPrimaryMonitorBounds()
    {
        var monitor = MonitorFromPoint(new PointStruct(0, 0), MonitorDefaultToPrimary);
        if (monitor == IntPtr.Zero)
        {
            return Rect.Empty;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return Rect.Empty;
        }

        return info.Monitor.ToRect();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointStruct pt, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private const int MonitorDefaultToPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;

        public PointStruct(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rect ToRect() => new Rect(Left, Top, Right - Left, Bottom - Top);
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

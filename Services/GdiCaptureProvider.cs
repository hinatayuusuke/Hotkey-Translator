using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class GdiCaptureProvider : ICaptureProvider
{
    public CaptureProviderKind Kind => CaptureProviderKind.Gdi;

    public bool IsEnabled(AppSettings settings) => true;

    public bool TryGetBounds(CaptureRequest request, out Rect bounds)
    {
        bounds = request.Mode switch
        {
            CaptureMode.ActiveWindow => GetActiveWindowBounds(ResolveActiveWindowHandle(request)) ?? GetVirtualScreenBounds(),
            _ => GetVirtualScreenBounds()
        };

        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool TryCapture(CaptureRequest request, out CaptureFrame frame, out string? error)
    {
        error = null;
        frame = null!;

        if (!TryGetBounds(request, out var bounds))
        {
            error = "Failed to resolve capture bounds.";
            return false;
        }

        try
        {
            // WHY: Keep Phase 1 reliable by using a pure GDI path first. WinRT capture
            // depends on Windows SDK runtime/graphics device setup and can fail silently
            // on some targets, which would block OCR/overlay validation. We switch to
            // GraphicsCapture only after verifying WinRT interop in the deployment env.
            var bitmap = new Bitmap((int)bounds.Width, (int)bounds.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    (int)bounds.X,
                    (int)bounds.Y,
                    0,
                    0,
                    new System.Drawing.Size((int)bounds.Width, (int)bounds.Height),
                    CopyPixelOperation.SourceCopy);
            }

            frame = new CaptureFrame(bitmap, bounds, Kind, DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Rect GetVirtualScreenBounds()
    {
        var left = GetSystemMetrics(SystemMetric.XVirtualScreen);
        var top = GetSystemMetrics(SystemMetric.YVirtualScreen);
        var width = GetSystemMetrics(SystemMetric.CxVirtualScreen);
        var height = GetSystemMetrics(SystemMetric.CyVirtualScreen);
        return new Rect(left, top, width, height);
    }

    private static IntPtr ResolveActiveWindowHandle(CaptureRequest request)
    {
        var configured = request.ResolveWindowHandle(GetForegroundWindow());
        if (configured != IntPtr.Zero && IsWindow(configured))
        {
            return configured;
        }

        return GetForegroundWindow();
    }

    private static Rect? GetActiveWindowBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (TryGetExtendedFrameBounds(hwnd, out var rect))
        {
            return rect;
        }

        if (GetWindowRect(hwnd, out var fallback))
        {
            return fallback.ToRect();
        }

        return null;
    }

    private static bool TryGetExtendedFrameBounds(IntPtr hwnd, out Rect rect)
    {
        rect = default;
        var size = Marshal.SizeOf<NativeRect>();
        if (DwmGetWindowAttribute(hwnd, DwmWindowAttribute.ExtendedFrameBounds, out var nativeRect, size) != 0)
        {
            return false;
        }

        rect = nativeRect.ToRect();
        return rect.Width > 0 && rect.Height > 0;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(SystemMetric smIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, DwmWindowAttribute dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    private enum SystemMetric
    {
        XVirtualScreen = 76,
        YVirtualScreen = 77,
        CxVirtualScreen = 78,
        CyVirtualScreen = 79
    }

    private enum DwmWindowAttribute
    {
        ExtendedFrameBounds = 9
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
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using Hotkey_Translator.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Hotkey_Translator.Services;

public sealed class DxgiDuplicationProvider : ICaptureProvider
{
    private readonly AppLogger _logger;

    public DxgiDuplicationProvider(AppLogger logger)
    {
        _logger = logger;
    }

    public CaptureProviderKind Kind => CaptureProviderKind.Dxgi;

    public bool IsEnabled(AppSettings settings) => settings.EnableDxgiCapture;

    public bool TryGetBounds(CaptureMode mode, out Rect bounds)
    {
        if (mode == CaptureMode.ActiveWindow)
        {
            bounds = GetActiveWindowBounds() ?? Rect.Empty;
            return bounds.Width > 0 && bounds.Height > 0;
        }

        bounds = GetPrimaryMonitorBounds();
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool TryCapture(CaptureMode mode, out CaptureFrame frame, out string? error)
    {
        frame = null!;
        error = null;

        try
        {
            var captureTarget = ResolveCaptureTarget(mode, out var monitorBounds, out var windowBounds, out var monitorHandle, out var targetBounds);
            if (!captureTarget)
            {
                error = "Failed to resolve DXGI capture target.";
                return false;
            }

            using var context = CreateDeviceAndContext(out var device);
            using var duplication = CreateDuplication(device, monitorHandle);

            var acquireResult = duplication.AcquireNextFrame(500, out var frameInfo, out var resource);
            if (acquireResult.Failure)
            {
                error = $"AcquireNextFrame failed: {acquireResult.Code}";
                return false;
            }

            try
            {
                using (resource)
                {
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    using var staging = CreateStagingTexture(device, texture);

                    context.CopyResource(staging, texture);
                    var bitmap = CopyToBitmap(context, staging);

                    if (mode == CaptureMode.ActiveWindow)
                    {
                        var intersect = Rect.Intersect(windowBounds, monitorBounds);
                        if (intersect.IsEmpty)
                        {
                            bitmap.Dispose();
                            error = "Active window is outside monitor bounds.";
                            return false;
                        }

                        var relative = new Rect(
                            intersect.X - monitorBounds.X,
                            intersect.Y - monitorBounds.Y,
                            intersect.Width,
                            intersect.Height);

                        var cropped = BitmapHelper.Crop(bitmap, relative);
                        bitmap.Dispose();
                        frame = new CaptureFrame(cropped, intersect, Kind, DateTimeOffset.UtcNow);
                        return true;
                    }

                    frame = new CaptureFrame(bitmap, targetBounds, Kind, DateTimeOffset.UtcNow);
                    return true;
                }
            }
            finally
            {
                duplication.ReleaseFrame();
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Info($"DXGI duplication failed: {ex.Message}");
            return false;
        }
    }

    private static bool ResolveCaptureTarget(
        CaptureMode mode,
        out Rect monitorBounds,
        out Rect windowBounds,
        out IntPtr monitorHandle,
        out Rect targetBounds)
    {
        monitorBounds = Rect.Empty;
        windowBounds = Rect.Empty;
        monitorHandle = IntPtr.Zero;
        targetBounds = Rect.Empty;

        if (mode == CaptureMode.ActiveWindow)
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var bounds = GetActiveWindowBounds();
            if (bounds is null)
            {
                return false;
            }

            windowBounds = bounds.Value;
            monitorHandle = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitorHandle == IntPtr.Zero)
            {
                return false;
            }

            monitorBounds = GetMonitorBounds(monitorHandle);
            targetBounds = windowBounds;
            return monitorBounds.Width > 0 && monitorBounds.Height > 0;
        }

        monitorHandle = MonitorFromPoint(new PointStruct(0, 0), MonitorDefaultToPrimary);
        if (monitorHandle == IntPtr.Zero)
        {
            return false;
        }

        monitorBounds = GetMonitorBounds(monitorHandle);
        targetBounds = monitorBounds;
        return monitorBounds.Width > 0 && monitorBounds.Height > 0;
    }

    private static ID3D11DeviceContext CreateDeviceAndContext(out ID3D11Device device)
    {
        var result = D3D11.D3D11CreateDevice(
            adapter: null,
            driverType: DriverType.Hardware,
            flags: DeviceCreationFlags.BgraSupport,
            featureLevels: new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out device,
            out _,
            out var context);

        if (result.Failure || device is null || context is null)
        {
            throw new InvalidOperationException("Failed to create D3D11 device for DXGI duplication.");
        }

        return context;
    }

    private static IDXGIOutputDuplication CreateDuplication(ID3D11Device device, IntPtr monitorHandle)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        var result = dxgiDevice.GetAdapter(out var adapter);
        if (result.Failure)
        {
            throw new InvalidOperationException("Failed to get DXGI adapter.");
        }

        using (adapter)
        {
            for (uint i = 0; ; i++)
            {
                var enumResult = adapter.EnumOutputs(i, out var output);
                if (enumResult.Failure)
                {
                    break;
                }

                using (output)
                {
                    var description = output.Description;
                    if (description.Monitor != monitorHandle)
                    {
                        continue;
                    }

                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    return output1.DuplicateOutput(device);
                }
            }
        }

        throw new InvalidOperationException("Failed to locate DXGI output for monitor.");
    }

    private static ID3D11Texture2D CreateStagingTexture(ID3D11Device device, ID3D11Texture2D source)
    {
        var desc = source.Description;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.Usage = ResourceUsage.Staging;
        desc.MiscFlags = ResourceOptionFlags.None;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.SampleDescription = new SampleDescription(1, 0);
        return device.CreateTexture2D(desc);
    }

    private static unsafe Bitmap CopyToBitmap(ID3D11DeviceContext context, ID3D11Texture2D texture)
    {
        var desc = texture.Description;
        var width = (int)desc.Width;
        var height = (int)desc.Height;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, width, height);
        var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);

        try
        {
            var dataBox = context.Map(texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var widthBytes = width * 4;
                var srcBase = (byte*)dataBox.DataPointer;
                var dstBase = (byte*)bitmapData.Scan0;
                for (var y = 0; y < height; y++)
                {
                    var srcRow = srcBase + (y * dataBox.RowPitch);
                    var dstRow = dstBase + (y * bitmapData.Stride);
                    Buffer.MemoryCopy(srcRow, dstRow, bitmapData.Stride, widthBytes);
                }
            }
            finally
            {
                context.Unmap(texture, 0);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        return bitmap;
    }

    private static Rect? GetActiveWindowBounds()
    {
        var hwnd = GetForegroundWindow();
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

    private static Rect GetPrimaryMonitorBounds()
    {
        var monitor = MonitorFromPoint(new PointStruct(0, 0), MonitorDefaultToPrimary);
        if (monitor == IntPtr.Zero)
        {
            return Rect.Empty;
        }

        return GetMonitorBounds(monitor);
    }

    private static Rect GetMonitorBounds(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return Rect.Empty;
        }

        return info.Monitor.ToRect();
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, DwmWindowAttribute dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointStruct pt, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private const int MonitorDefaultToPrimary = 1;
    private const int MonitorDefaultToNearest = 2;

    private enum DwmWindowAttribute
    {
        ExtendedFrameBounds = 9
    }

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

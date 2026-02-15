using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class GraphicsHookCaptureProvider : ICaptureProvider
{
    private const uint FrameHeaderMagic = 0x48465452; // "HFTR"
    private const uint FrameHeaderVersion = 1;
    private const uint PixelFormatBgra8 = 1;
    private const uint GraphicsApiDx11 = 1;

    public GraphicsHookCaptureProvider(AppLogger logger)
    {
        _logger = logger;
    }

    public CaptureProviderKind Kind => CaptureProviderKind.GraphicsHook;

    public bool IsEnabled(AppSettings settings)
    {
        // WHY: Hook capture is currently DX11-only and requires active-window + fixed binding (PID is derived from hwnd).
        return settings.EnableDx11HookPipeline && settings.CaptureMode == CaptureMode.ActiveWindow && settings.EnableFixedCaptureWindow;
    }

    public bool TryGetBounds(CaptureRequest request, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (request.Mode != CaptureMode.ActiveWindow)
        {
            return false;
        }

        if (!TryGetBoundTargetHwnd(request, out var hwnd))
        {
            return false;
        }

        bounds = GetActiveWindowBounds(hwnd) ?? Rect.Empty;
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool TryCapture(CaptureRequest request, out CaptureFrame frame, out string? error)
    {
        frame = null!;
        error = null;

        if (request.Mode != CaptureMode.ActiveWindow)
        {
            error = "Hook capture only supports active window mode.";
            return false;
        }

        if (!TryGetBoundTargetHwnd(request, out var hwnd))
        {
            error = "Hook capture requires a bound fixed target window.";
            return false;
        }

        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            error = "Invalid capture target window handle.";
            return false;
        }

        if (!TryGetBounds(request, out var bounds))
        {
            error = "Failed to resolve capture bounds.";
            return false;
        }

        if (!TryGetWindowPid(hwnd, out var pid) || pid <= 0)
        {
            error = "Failed to resolve capture target process id.";
            return false;
        }

        var mappingName = BuildFrameMappingName(pid);
        if (!TryReadLatestFrame(mappingName, out var header, out var payload, out var readError))
        {
            error = readError ?? "Hook shared frame read failed.";
            return false;
        }

        try
        {
            var bitmap = CreateBitmapFromBgraPayload(header, payload);
            frame = new CaptureFrame(bitmap, bounds, Kind, DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Error(ex, "Hook capture bitmap decode failed.");
            return false;
        }
    }

    private readonly AppLogger _logger;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SharedFrameHeader
    {
        public uint Magic;
        public uint Version;
        public ulong FrameId;
        public uint Width;
        public uint Height;
        public uint Stride;
        public uint PayloadBytes;
        public uint PixelFormat;
        public uint Api;
        public uint ProducerPid;
        public uint Reserved0;
        public ulong TimestampQpc;
    }

    private static string BuildFrameMappingName(int pid)
    {
        // NOTE: Must match Native/HookCommon/HookIpcProtocol.h naming.
        return $@"Local\HT_HOOK_FRAME_{GraphicsApiDx11}_{pid}";
    }

    private static bool TryReadLatestFrame(
        string mappingName,
        out SharedFrameHeader header,
        out byte[] payload,
        out string? error)
    {
        var headerBytes = Marshal.SizeOf<SharedFrameHeader>();
        header = default;
        payload = Array.Empty<byte>();
        error = null;

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                accessor.Read(0, out header);
                if (!ValidateHeader(header, out var headerError))
                {
                    error = headerError;
                    return false;
                }

                var payloadBytes = checked((int)header.PayloadBytes);
                if (payloadBytes <= 0 || payloadBytes > 7680 * 4320 * 4)
                {
                    error = $"Invalid payload size: {payloadBytes}.";
                    return false;
                }

                payload = new byte[payloadBytes];
                accessor.ReadArray(headerBytes, payload, 0, payloadBytes);

                // WHY: Writer stores payload first and header last. Re-read to detect races.
                SharedFrameHeader confirm = default;
                accessor.Read(0, out confirm);
                if (confirm.FrameId == header.FrameId && confirm.PayloadBytes == header.PayloadBytes)
                {
                    return true;
                }

                payload = Array.Empty<byte>();
            }

            error = "Frame was unstable (writer race).";
            return false;
        }
        catch (FileNotFoundException)
        {
            error = "Hook shared frame mapping not found.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool ValidateHeader(SharedFrameHeader header, out string? error)
    {
        error = null;

        if (header.Magic != FrameHeaderMagic || header.Version != FrameHeaderVersion)
        {
            error = "Hook frame header not initialized.";
            return false;
        }

        if (header.Api != GraphicsApiDx11)
        {
            error = $"Unexpected hook api: {header.Api}.";
            return false;
        }

        if (header.PixelFormat != PixelFormatBgra8)
        {
            error = $"Unexpected hook pixel format: {header.PixelFormat}.";
            return false;
        }

        if (header.Width == 0 || header.Height == 0 || header.Stride == 0)
        {
            error = "Invalid hook frame header dimensions.";
            return false;
        }

        return true;
    }

    private static Bitmap CreateBitmapFromBgraPayload(SharedFrameHeader header, byte[] payload)
    {
        var width = checked((int)header.Width);
        var height = checked((int)header.Height);
        var stride = checked((int)header.Stride);

        if (stride < width * 4)
        {
            throw new InvalidOperationException($"Invalid stride: {stride}.");
        }

        var expectedBytes = checked(stride * height);
        if (expectedBytes != payload.Length)
        {
            // NOTE: We currently require a full contiguous image payload.
            throw new InvalidOperationException($"Unexpected payload size: {payload.Length} != {expectedBytes}.");
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                var dstRow = IntPtr.Add(data.Scan0, y * data.Stride);

                // Copy only the active pixels; row pitch may include padding.
                Marshal.Copy(payload, y * stride, dstRow, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static bool TryGetBoundTargetHwnd(CaptureRequest request, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (!request.TargetWindowHandle.HasValue)
        {
            return false;
        }

        hwnd = request.TargetWindowHandle.Value;
        return hwnd != IntPtr.Zero;
    }

    private static bool TryGetWindowPid(IntPtr hwnd, out int pid)
    {
        pid = 0;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var nativePid);
        pid = unchecked((int)nativePid);
        return pid > 0;
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
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, DwmWindowAttribute dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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

        public Rect ToRect()
        {
            return new Rect(Left, Top, Right - Left, Bottom - Top);
        }
    }
}

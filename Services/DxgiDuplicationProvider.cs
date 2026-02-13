using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Hotkey_Translator.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Hotkey_Translator.Services;

public sealed class DxgiDuplicationProvider : ICaptureProvider
{
    private readonly AppLogger _logger;
    private readonly bool _debugLogEnabled = IsDxgiDebugEnabled();
    private readonly object _sessionLock = new();
    private DxgiResidentSession? _residentSession;
    private bool _residentEnabled;
    private Bitmap? _lastSuccessfulBitmap;
    private Rect _lastSuccessfulBounds;
    private CaptureMode _lastSuccessfulMode;
    private IntPtr _lastSuccessfulMonitorHandle;

    public DxgiDuplicationProvider(AppLogger logger)
    {
        _logger = logger;
    }

    public CaptureProviderKind Kind => CaptureProviderKind.Dxgi;

    public bool IsEnabled(AppSettings settings) => settings.EnableDxgiCapture;

    public void SetResidentEnabled(bool enabled)
    {
        lock (_sessionLock)
        {
            if (_residentEnabled == enabled)
            {
                return;
            }

            _residentEnabled = enabled;
            if (!enabled)
            {
                ResetSession("DXGI resident disabled.");
            }
        }
    }

    public bool TryGetBounds(CaptureRequest request, out Rect bounds)
    {
        if (request.Mode == CaptureMode.ActiveWindow)
        {
            bounds = GetActiveWindowBounds(ResolveActiveWindowHandle(request)) ?? Rect.Empty;
            return bounds.Width > 0 && bounds.Height > 0;
        }

        bounds = GetPrimaryMonitorBounds();
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool TryCapture(CaptureRequest request, out CaptureFrame frame, out string? error)
    {
        frame = null!;
        error = null;

        try
        {
            var captureTarget = ResolveCaptureTarget(
                request,
                out var monitorBounds,
                out var windowBounds,
                out var monitorHandle,
                out var targetBounds,
                out var windowHandle);
            if (!captureTarget)
            {
                error = "Failed to resolve DXGI capture target.";
                return false;
            }

            if (_debugLogEnabled)
            {
                var windowInfo = request.Mode == CaptureMode.ActiveWindow
                    ? $"hwnd=0x{windowHandle.ToInt64():X} title=\"{GetWindowTitle(windowHandle)}\" process=\"{GetWindowProcessName(windowHandle)}\""
                    : "hwnd=<screen>";
                DebugLog($"Capture start mode={request.Mode} resident={_residentEnabled} {windowInfo}");
                DebugLog($"Monitor handle=0x{monitorHandle.ToInt64():X} monitorBounds={FormatRect(monitorBounds)} windowBounds={FormatRect(windowBounds)} targetBounds={FormatRect(targetBounds)}");
            }

            lock (_sessionLock)
            {
                if (!_residentEnabled)
                {
                    ResetSession("DXGI not selected; using transient session.");
                    using var transientSession = CreateSession(monitorHandle);
                    return TryCaptureWithSession(transientSession, request, monitorBounds, windowBounds, targetBounds, false, out frame, out error);
                }

                if (!EnsureResidentSession(monitorHandle, out var session, out error))
                {
                    return false;
                }

                return TryCaptureWithSession(session, request, monitorBounds, windowBounds, targetBounds, true, out frame, out error);
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
        CaptureRequest request,
        out Rect monitorBounds,
        out Rect windowBounds,
        out IntPtr monitorHandle,
        out Rect targetBounds,
        out IntPtr windowHandle)
    {
        monitorBounds = Rect.Empty;
        windowBounds = Rect.Empty;
        monitorHandle = IntPtr.Zero;
        targetBounds = Rect.Empty;
        windowHandle = IntPtr.Zero;

        if (request.Mode == CaptureMode.ActiveWindow)
        {
            windowHandle = ResolveActiveWindowHandle(request);
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            var bounds = GetActiveWindowBounds(windowHandle);
            if (bounds is null)
            {
                return false;
            }

            windowBounds = bounds.Value;
            monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
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

    private DxgiResidentSession CreateSession(IntPtr monitorHandle)
    {
        var context = CreateDeviceAndContext(out var device);
        try
        {
            var duplication = CreateDuplication(device, monitorHandle, out var outputDescription);
            var duplicationDesc = duplication.Description;
            return new DxgiResidentSession(device, context, duplication, monitorHandle, duplicationDesc, outputDescription);
        }
        catch
        {
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    private bool EnsureResidentSession(IntPtr monitorHandle, out DxgiResidentSession session, out string? error)
    {
        error = null;
        session = _residentSession!;

        if (_residentSession == null || _residentSession.MonitorHandle != monitorHandle)
        {
            ResetSession("DXGI monitor changed; recreating session.");
        }

        if (_residentSession == null)
        {
            try
            {
                _residentSession = CreateSession(monitorHandle);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        session = _residentSession!;
        return true;
    }

    private void ResetSession(string reason)
    {
        if (_residentSession == null)
        {
            return;
        }

        _logger.Info($"DXGI session reset: {reason}");
        _residentSession.Dispose();
        _residentSession = null;
    }

    private bool TryCaptureWithSession(
        DxgiResidentSession session,
        CaptureRequest request,
        Rect monitorBounds,
        Rect windowBounds,
        Rect targetBounds,
        bool allowRecreate,
        out CaptureFrame frame,
        out string? error)
    {
        frame = null!;
        error = null;

        for (var attempt = 0; attempt <= MaxWarmupAttempts; attempt++)
        {
            var acquireResult = session.Duplication.AcquireNextFrame(AcquireTimeoutMs, out var frameInfo, out var resource);
            if (acquireResult.Failure)
            {
                if (allowRecreate && IsRecoverableDxgiError(acquireResult.Code))
                {
                    ResetSession($"DXGI duplication lost: {acquireResult.Code}.");
                    if (!EnsureResidentSession(session.MonitorHandle, out session, out error))
                    {
                        return false;
                    }

                    continue;
                }

                if (IsWaitTimeout(acquireResult.Code))
                {
                    if (TryCreateFrameFromLastSuccess(request.Mode, session.MonitorHandle, out frame))
                    {
                        if (_debugLogEnabled)
                        {
                            DebugLog("AcquireNextFrame timed out; using last successful frame.");
                        }

                        error = null;
                        return true;
                    }

                    error = "DXGI timed out waiting for a new frame.";
                    return false;
                }

                error = $"AcquireNextFrame failed: {acquireResult.Code}";
                return false;
            }

            try
            {
                using (resource)
                {
                    if (resource is null)
                    {
                        error = "DXGI returned no resource.";
                        return false;
                    }

                    // WHY: Some drivers deliver an initial frame with no present time; skip warm-up frames to avoid black captures.
                    if (frameInfo.AccumulatedFrames == 0 && frameInfo.LastPresentTime == 0)
                    {
                        if (attempt < MaxWarmupAttempts)
                        {
                            continue;
                        }

                        error = "DXGI frame not ready (warm-up).";
                        return false;
                    }

                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    if (_debugLogEnabled)
                    {
                        var outputDesc = session.OutputDescription;
                        var dupDesc = session.DuplicationDescription;
                        var textureDesc = texture.Description;
                        DebugLog($"Output device={TrimDeviceName(outputDesc.DeviceName)} desktop={FormatDesktopCoordinates(outputDesc)} rotation={outputDesc.Rotation} attached={outputDesc.AttachedToDesktop}");
                        DebugLog($"Duplication rotation={dupDesc.Rotation} mode={dupDesc.ModeDescription.Width}x{dupDesc.ModeDescription.Height} format={dupDesc.ModeDescription.Format}");
                        DebugLog($"FrameInfo present={frameInfo.LastPresentTime} accum={frameInfo.AccumulatedFrames} texture={textureDesc.Width}x{textureDesc.Height} format={textureDesc.Format}");
                    }
                    session.EnsureStaging(texture);

                    session.Context.CopyResource(session.Staging!, texture);
                    var bitmap = CopyToBitmap(session.Context, session.Staging!);

                    if (request.Mode == CaptureMode.ActiveWindow)
                    {
                        var intersect = Rect.Intersect(windowBounds, monitorBounds);
                        if (intersect.IsEmpty)
                        {
                            if (_debugLogEnabled)
                            {
                                DebugLog($"Intersect empty window={FormatRect(windowBounds)} monitor={FormatRect(monitorBounds)}");
                            }
                            bitmap.Dispose();
                            error = "Active window is outside monitor bounds.";
                            return false;
                        }

                        var relative = new Rect(
                            intersect.X - monitorBounds.X,
                            intersect.Y - monitorBounds.Y,
                            intersect.Width,
                            intersect.Height);
                        var transformed = TransformDesktopRectToOutputRect(
                            intersect,
                            monitorBounds,
                            session.DuplicationDescription.Rotation,
                            bitmap.Width,
                            bitmap.Height);
                        if (_debugLogEnabled)
                        {
                            DebugLog($"Rect intersect={FormatRect(intersect)} relative={FormatRect(relative)} transformed={FormatRect(transformed)}");
                        }

                        var cropped = BitmapHelper.Crop(bitmap, transformed);
                        cropped = RotateToDesktopOrientation(cropped, session.DuplicationDescription.Rotation);
                        bitmap.Dispose();
                        RememberLastSuccessfulFrame(cropped, intersect, request.Mode, session.MonitorHandle);
                        frame = new CaptureFrame(cropped, intersect, Kind, DateTimeOffset.UtcNow);
                        return true;
                    }

                    RememberLastSuccessfulFrame(bitmap, targetBounds, request.Mode, session.MonitorHandle);
                    frame = new CaptureFrame(bitmap, targetBounds, Kind, DateTimeOffset.UtcNow);
                    return true;
                }
            }
            finally
            {
                session.Duplication.ReleaseFrame();
            }
        }

        error = "DXGI frame not ready (warm-up).";
        return false;
    }

    private void RememberLastSuccessfulFrame(Bitmap bitmap, Rect bounds, CaptureMode mode, IntPtr monitorHandle)
    {
        _lastSuccessfulBitmap?.Dispose();
        _lastSuccessfulBitmap = (Bitmap)bitmap.Clone();
        _lastSuccessfulBounds = bounds;
        _lastSuccessfulMode = mode;
        _lastSuccessfulMonitorHandle = monitorHandle;
    }

    private bool TryCreateFrameFromLastSuccess(CaptureMode mode, IntPtr monitorHandle, out CaptureFrame frame)
    {
        frame = null!;
        if (_lastSuccessfulBitmap == null)
        {
            return false;
        }

        if (_lastSuccessfulMode != mode || _lastSuccessfulMonitorHandle != monitorHandle)
        {
            return false;
        }

        var copy = (Bitmap)_lastSuccessfulBitmap.Clone();
        frame = new CaptureFrame(copy, _lastSuccessfulBounds, Kind, DateTimeOffset.UtcNow);
        return true;
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

    private static IDXGIOutputDuplication CreateDuplication(ID3D11Device device, IntPtr monitorHandle, out OutputDescription outputDescription)
    {
        outputDescription = default;
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

                    outputDescription = description;
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

    private void DebugLog(string message)
    {
        if (_debugLogEnabled)
        {
            _logger.Info($"[DXGI-DEBUG] {message}");
        }
    }

    private static bool IsDxgiDebugEnabled()
    {
        var value = Environment.GetEnvironmentVariable(DxgiDebugEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetWindowProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatRect(Rect rect)
    {
        return $"[{rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##},{rect.Height:0.##}]";
    }

    private static string FormatDesktopCoordinates(OutputDescription description)
    {
        var coords = description.DesktopCoordinates;
        return $"[{coords.Left},{coords.Top},{coords.Right},{coords.Bottom}]";
    }

    private static string TrimDeviceName(string deviceName)
    {
        return deviceName?.TrimEnd('\0') ?? string.Empty;
    }

    private static Rect TransformDesktopRectToOutputRect(
        Rect desktopRect,
        Rect monitorBounds,
        ModeRotation rotation,
        int outputWidth,
        int outputHeight)
    {
        var relative = new Rect(
            desktopRect.X - monitorBounds.X,
            desktopRect.Y - monitorBounds.Y,
            desktopRect.Width,
            desktopRect.Height);

        if (rotation == ModeRotation.Identity || rotation == ModeRotation.Unspecified)
        {
            return relative;
        }

        if (outputWidth <= 0 || outputHeight <= 0)
        {
            return relative;
        }

        // WHY: DXGI duplication surfaces are rotated; map desktop-space rect into output-space before cropping.
        var topLeft = TransformPoint(relative.X, relative.Y, outputWidth, outputHeight, rotation);
        var topRight = TransformPoint(relative.Right, relative.Y, outputWidth, outputHeight, rotation);
        var bottomLeft = TransformPoint(relative.X, relative.Bottom, outputWidth, outputHeight, rotation);
        var bottomRight = TransformPoint(relative.Right, relative.Bottom, outputWidth, outputHeight, rotation);

        var minX = Math.Min(Math.Min(topLeft.X, topRight.X), Math.Min(bottomLeft.X, bottomRight.X));
        var minY = Math.Min(Math.Min(topLeft.Y, topRight.Y), Math.Min(bottomLeft.Y, bottomRight.Y));
        var maxX = Math.Max(Math.Max(topLeft.X, topRight.X), Math.Max(bottomLeft.X, bottomRight.X));
        var maxY = Math.Max(Math.Max(topLeft.Y, topRight.Y), Math.Max(bottomLeft.Y, bottomRight.Y));

        var transformed = new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
        return ClampRect(transformed, outputWidth, outputHeight);
    }

    private static System.Windows.Point TransformPoint(
        double x,
        double y,
        double outputWidth,
        double outputHeight,
        ModeRotation rotation)
    {
        return rotation switch
        {
            // WHY: Desktop (rotated) -> Texture (native) mapping; pixel edge -1 is ignored for continuous coords.
            ModeRotation.Rotate90 => new System.Windows.Point(y, outputHeight - x),
            ModeRotation.Rotate180 => new System.Windows.Point(outputWidth - x, outputHeight - y),
            ModeRotation.Rotate270 => new System.Windows.Point(outputWidth - y, x),
            _ => new System.Windows.Point(x, y)
        };
    }

    private static Rect ClampRect(Rect rect, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0)
        {
            return rect;
        }

        var x = Math.Max(0, rect.X);
        var y = Math.Max(0, rect.Y);
        var right = Math.Min(maxWidth, rect.Right);
        var bottom = Math.Min(maxHeight, rect.Bottom);
        if (right <= x || bottom <= y)
        {
            return Rect.Empty;
        }

        return new Rect(x, y, right - x, bottom - y);
    }

    private static Bitmap RotateToDesktopOrientation(Bitmap source, ModeRotation rotation)
    {
        var rotateFlip = rotation switch
        {
            ModeRotation.Rotate90 => RotateFlipType.Rotate90FlipNone,
            ModeRotation.Rotate180 => RotateFlipType.Rotate180FlipNone,
            ModeRotation.Rotate270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };

        if (rotateFlip == RotateFlipType.RotateNoneFlipNone)
        {
            return source;
        }

        // WHY: DXGI duplication delivers rotated output buffers; rotate cropped frames for OCR/preview alignment.
        source.RotateFlip(rotateFlip);
        return source;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, DwmWindowAttribute dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointStruct pt, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private const int AcquireTimeoutMs = 200;
    private const int MaxWarmupAttempts = 2;
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);
    private const int MonitorDefaultToPrimary = 1;
    private const int MonitorDefaultToNearest = 2;
    private const string DxgiDebugEnvVar = "HOTKEY_TRANSLATOR_DXGI_DEBUG";

    private static bool IsRecoverableDxgiError(int code)
    {
        return code == DxgiErrorAccessLost || code == DxgiErrorInvalidCall;
    }

    private static bool IsWaitTimeout(int code)
    {
        return code == DxgiErrorWaitTimeout;
    }

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

    private sealed class DxgiResidentSession : IDisposable
    {
        public DxgiResidentSession(
            ID3D11Device device,
            ID3D11DeviceContext context,
            IDXGIOutputDuplication duplication,
            IntPtr monitorHandle,
            OutduplDescription duplicationDescription,
            OutputDescription outputDescription)
        {
            Device = device;
            Context = context;
            Duplication = duplication;
            MonitorHandle = monitorHandle;
            DuplicationDescription = duplicationDescription;
            OutputDescription = outputDescription;
        }

        public ID3D11Device Device { get; }
        public ID3D11DeviceContext Context { get; }
        public IDXGIOutputDuplication Duplication { get; }
        public IntPtr MonitorHandle { get; }
        public OutduplDescription DuplicationDescription { get; }
        public OutputDescription OutputDescription { get; }
        public ID3D11Texture2D? Staging { get; private set; }

        public void EnsureStaging(ID3D11Texture2D source)
        {
            var desc = source.Description;
            if (Staging != null)
            {
                var stagingDesc = Staging.Description;
                if (stagingDesc.Width == desc.Width && stagingDesc.Height == desc.Height && stagingDesc.Format == desc.Format)
                {
                    return;
                }

                Staging.Dispose();
                Staging = null;
            }

            Staging = CreateStagingTexture(Device, source);
        }

        public void Dispose()
        {
            Staging?.Dispose();
            Duplication.Dispose();
            Context.Dispose();
            Device.Dispose();
        }
    }
}

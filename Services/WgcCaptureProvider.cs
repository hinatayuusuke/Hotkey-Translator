using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Hotkey_Translator.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using static Vortice.Direct3D11.D3D11;

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
        error = null;
        IDirect3DDevice? direct3DDevice = null;

        try
        {
            if (!GraphicsCaptureSession.IsSupported())
            {
                error = "GraphicsCaptureSession is not supported on this OS.";
                return false;
            }

            var item = CreateCaptureItem(mode);
            if (item is null)
            {
                error = "Failed to create GraphicsCaptureItem.";
                return false;
            }

            if (!TryGetBounds(mode, out var bounds))
            {
                error = "Failed to resolve capture bounds.";
                return false;
            }

            _logger.Info("WGC: creating Direct3D devices.");
            var devices = CreateDirect3DDevices();
            using var d3dDevice = devices.D3DDevice;
            direct3DDevice = devices.Direct3DDevice;

            var size = item.Size;
            using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                direct3DDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                size);

            using var session = framePool.CreateCaptureSession(item);
            Direct3D11CaptureFrame? capturedFrame = null;

            using var frameReady = new AutoResetEvent(false);
            framePool.FrameArrived += (_, _) =>
            {
                capturedFrame = framePool.TryGetNextFrame();
                frameReady.Set();
            };

            session.StartCapture();

            if (!frameReady.WaitOne(TimeSpan.FromMilliseconds(500)))
            {
                error = "Timed out waiting for WGC frame.";
                return false;
            }

            if (capturedFrame is null)
            {
                error = "No frame received from WGC.";
                return false;
            }

            _logger.Info("WGC: converting capture surface to SoftwareBitmap.");
            using (capturedFrame)
            using (var softwareBitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(capturedFrame.Surface).AsTask().GetAwaiter().GetResult())
            {
                var bitmap = BitmapHelper.FromSoftwareBitmap(softwareBitmap);
                frame = new CaptureFrame(bitmap, bounds, Kind, DateTimeOffset.UtcNow);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Error(ex, "WGC capture failed.");
            return false;
        }
        finally
        {
            (direct3DDevice as IDisposable)?.Dispose();
        }
    }

    private static (ID3D11Device D3DDevice, IDirect3DDevice Direct3DDevice) CreateDirect3DDevices()
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0
        };

        var result = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device? d3dDevice,
            out FeatureLevel _);

        if (result.Failure || d3dDevice is null)
        {
            throw new InvalidOperationException($"D3D11CreateDevice failed: {result.Code}");
        }

        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        var winrtDevice = CreateWinrtDeviceFromDxgiDevice(dxgiDevice);
        return (d3dDevice, winrtDevice);
    }

    private GraphicsCaptureItem? CreateCaptureItem(CaptureMode mode)
    {
        try
        {
            _logger.Info("WGC: querying activation factory via CsWinRT.");

            var factoryRef = WinRT.ActivationFactory.Get(GraphicsCaptureItemRuntimeClass);
            var interop = factoryRef.AsInterface<IGraphicsCaptureItemInterop>();
            var itemIid = GraphicsCaptureItemInterfaceGuid;

            if (mode == CaptureMode.ActiveWindow)
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return null;
                _logger.Info("WGC: creating capture item for window.");
                var itemPtr = interop.CreateForWindow(hwnd, ref itemIid);
                return MarshalToGraphicsCaptureItem(itemPtr);
            }

            var monitor = MonitorFromPoint(new PointStruct(0, 0), MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero) return null;
            _logger.Info("WGC: creating capture item for monitor.");
            var monitorPtr = interop.CreateForMonitor(monitor, ref itemIid);
            return MarshalToGraphicsCaptureItem(monitorPtr);
        }
        catch (Exception ex)
        {
            _logger.Error($"WGC: exception detail: {ex}");
            _logger.Error(ex, "WGC: failed to create GraphicsCaptureItem.");
            return null;
        }
    }

    private static GraphicsCaptureItem? MarshalToGraphicsCaptureItem(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    private static IDirect3DDevice CreateWinrtDeviceFromDxgiDevice(IDXGIDevice dxgiDevice)
    {
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var devicePtr);
        Marshal.ThrowExceptionForHR(hr);
        // WHY: Use CsWinRT ABI wrapper to avoid invalid cast RCW issues.
        return MarshalInterface<IDirect3DDevice>.FromAbi(devicePtr);
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

    private static readonly Guid GraphicsCaptureItemInterfaceGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private const string GraphicsCaptureItemRuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    [DllImport("d3d11.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

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
[ComImport]
[Guid("3B219634-2708-4E31-A0C2-052C90311E3B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IGraphicsCaptureItemInterop
{
    // メソッドの戻り値を IntPtr ではなく、生成されるオブジェクトとして定義
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

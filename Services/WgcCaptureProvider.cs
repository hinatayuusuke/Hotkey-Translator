using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Hotkey_Translator.Models;
using WinRT;
using WinRT.Interop;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

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

            _logger.Info("WGC: creating Direct3D device.");
            using var direct3DDevice = CreateDirect3DDevice();

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
    }

    private IDirect3DDevice CreateDirect3DDevice()
    {
        IntPtr d3dDevice = IntPtr.Zero;
        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr direct3DDevice = IntPtr.Zero;
        try
        {
            unsafe
            {
                var levels = stackalloc D3DFeatureLevel[2]
                {
                    D3DFeatureLevel.Level11_1,
                    D3DFeatureLevel.Level11_0
                };

                var hr = D3D11CreateDevice(
                    IntPtr.Zero,
                    D3DDriverType.Hardware,
                    IntPtr.Zero,
                    D3D11CreateDeviceBgraSupport,
                    levels,
                    2,
                    D3D11SdkVersion,
                    out d3dDevice,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (hr != 0 || d3dDevice == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to create D3D11 device.");
                }
            }

            var dxgiGuid = IidIdxgiDevice;
            var qiResult = Marshal.QueryInterface(d3dDevice, ref dxgiGuid, out dxgiDevice);
            if (qiResult != 0)
            {
                throw new InvalidOperationException("Failed to query IDXGIDevice.");
            }

            if (dxgiDevice == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to query IDXGIDevice.");
            }

            var result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out direct3DDevice);
            if (result != 0 || direct3DDevice == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create IDirect3DDevice for WGC.");
            }

            _logger.Info("WGC: created IDirect3DDevice.");
            return (IDirect3DDevice)Marshal.GetObjectForIUnknown(direct3DDevice);
        }
        finally
        {
            if (direct3DDevice != IntPtr.Zero)
            {
                Marshal.Release(direct3DDevice);
            }

            if (dxgiDevice != IntPtr.Zero)
            {
                Marshal.Release(dxgiDevice);
            }

            if (d3dDevice != IntPtr.Zero)
            {
                Marshal.Release(d3dDevice);
            }
        }
    }

    private GraphicsCaptureItem? CreateCaptureItem(CaptureMode mode)
    {
        try
        {
            _logger.Info("WGC: creating interop factory via RoGetActivationFactory.");

            // 1. 文字列から WinRT クラス名を作成
            var hsClassId = WinRT.MarshalString.CreateMarshaler(GraphicsCaptureItemRuntimeClass);
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;

            try
            {
                // 2. 直接アクティベーションファクトリを取得
                int hr = RoGetActivationFactory(WinRT.MarshalString.GetAbi(hsClassId), ref iid, out IntPtr factoryPtr);
                if (hr != 0 || factoryPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Failed to get activation factory: {hr}");
                }

                // 3. 取得したポインタをインターフェースにラップ
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);

                try
                {
                    var itemIid = GraphicsCaptureItemInterfaceGuid;
                    IntPtr itemPtr = IntPtr.Zero;

                    if (mode == CaptureMode.ActiveWindow)
                    {
                        var hwnd = GetForegroundWindow();
                        if (hwnd == IntPtr.Zero) return null;
                        _logger.Info("WGC: creating capture item for window.");
                        itemPtr = interop.CreateForWindow(hwnd, ref itemIid);
                    }
                    else
                    {
                        var monitor = MonitorFromPoint(new PointStruct(0, 0), MonitorDefaultToPrimary);
                        if (monitor == IntPtr.Zero) return null;
                        _logger.Info("WGC: creating capture item for monitor.");
                        itemPtr = interop.CreateForMonitor(monitor, ref itemIid);
                    }

                    return MarshalToGraphicsCaptureItem(itemPtr);
                }
                finally
                {
                    Marshal.Release(factoryPtr);
                }
            }
            finally
            {
                // WHY: MarshalStringはIDisposable非実装のため、明示解放が必要。
                WinRT.MarshalString.DisposeMarshaler(hsClassId);
            }
        }
        catch (Exception ex)
        {
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
        typeof(GraphicsCaptureItem).GetInterface("IGraphicsCaptureItem")?.GUID ?? typeof(GraphicsCaptureItem).GUID;
    private const string GraphicsCaptureItemRuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll")]
    private static extern unsafe int D3D11CreateDevice(
        IntPtr adapter,
        D3DDriverType driverType,
        IntPtr software,
        uint flags,
        D3DFeatureLevel* featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        IntPtr featureLevel,
        IntPtr immediateContext);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

       [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointStruct pt, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private const int MonitorDefaultToPrimary = 1;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private static readonly Guid IidIdxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private enum D3DDriverType : uint
    {
        Hardware = 1
    }

    private enum D3DFeatureLevel : uint
    {
        Level11_1 = 0xB100,
        Level11_0 = 0xB000
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
[ComImport]
[Guid("3B219634-2708-4E31-A0C2-052C90311E3B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]

public interface IGraphicsCaptureItemInterop
{
// メソッドの戻り値を IntPtr ではなく、生成されるオブジェクトとして定義
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

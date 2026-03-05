using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Hotkey_Translator.Services;

internal sealed class RawInputMouseSession : IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RidevInputSink = 0x00000100;
    private const ushort RiMouseLeftButtonDown = 0x0001;
    private const ushort RiMouseLeftButtonUp = 0x0002;

    private readonly Window _window;
    private readonly Action<Point> _onLeftDown;
    private readonly Action<Point> _onMove;
    private readonly Action<Point> _onLeftUp;
    private HwndSource? _source;
    private IntPtr _windowHandle = IntPtr.Zero;
    private bool _registered;
    private bool _isLeftDown;

    public RawInputMouseSession(
        Window window,
        Action<Point> onLeftDown,
        Action<Point> onMove,
        Action<Point> onLeftUp)
    {
        _window = window;
        _onLeftDown = onLeftDown;
        _onMove = onMove;
        _onLeftUp = onLeftUp;
    }

    public bool TryStart(out string? failureReason)
    {
        failureReason = null;
        if (_registered)
        {
            return true;
        }

        _windowHandle = new WindowInteropHelper(_window).Handle;
        if (_windowHandle == IntPtr.Zero)
        {
            failureReason = "rawinput_mouse_window_handle_unavailable";
            return false;
        }

        _source = HwndSource.FromHwnd(_windowHandle);
        if (_source == null)
        {
            failureReason = "rawinput_mouse_hwnd_source_unavailable";
            return false;
        }

        _source.AddHook(WndProc);
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01, // Generic Desktop Controls
                Usage = 0x02,     // Mouse
                Flags = RidevInputSink,
                Target = _windowHandle
            }
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            _source.RemoveHook(WndProc);
            _source = null;
            failureReason = BuildWin32FailureReason("register_raw_mouse_input_failed");
            return false;
        }

        _registered = true;
        return true;
    }

    public void Dispose()
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _registered = false;
        _isLeftDown = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput)
        {
            return IntPtr.Zero;
        }

        if (TryReadMouseData(lParam, out var mouseData))
        {
            ProcessMouseData(mouseData);
        }

        handled = false;
        return IntPtr.Zero;
    }

    private void ProcessMouseData(RawMouse mouseData)
    {
        var buttonFlags = mouseData.ButtonFlags;
        var hasDelta = mouseData.LastX != 0 || mouseData.LastY != 0;
        if (!TryGetCursorPos(out var cursorScreen))
        {
            return;
        }

        if ((buttonFlags & RiMouseLeftButtonDown) != 0)
        {
            _isLeftDown = true;
            _onLeftDown(cursorScreen);
        }

        // WHY: WM_INPUT mouse packets carry relative deltas; use current cursor position as a stable screen-space ROI point.
        if (_isLeftDown && (hasDelta || (buttonFlags & (RiMouseLeftButtonDown | RiMouseLeftButtonUp)) != 0))
        {
            _onMove(cursorScreen);
        }

        if ((buttonFlags & RiMouseLeftButtonUp) != 0)
        {
            if (_isLeftDown)
            {
                _onLeftUp(cursorScreen);
            }

            _isLeftDown = false;
        }
    }

    private static bool TryReadMouseData(IntPtr rawInputHandle, out RawMouse mouseData)
    {
        mouseData = default;
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        var queryResult = GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize);
        if (queryResult == uint.MaxValue || size < headerSize)
        {
            return false;
        }

        var buffer = new byte[size];
        var gcHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var ptr = gcHandle.AddrOfPinnedObject();
            var readSize = size;
            var readResult = GetRawInputData(rawInputHandle, RidInput, ptr, ref readSize, headerSize);
            if (readResult == uint.MaxValue || readSize < headerSize)
            {
                return false;
            }

            var header = Marshal.PtrToStructure<RawInputHeader>(ptr);
            if (header.Type != RimTypeMouse)
            {
                return false;
            }

            var mouseOffset = Marshal.SizeOf<RawInputHeader>();
            var mousePtr = IntPtr.Add(ptr, mouseOffset);
            mouseData = Marshal.PtrToStructure<RawMouse>(mousePtr);
            return true;
        }
        finally
        {
            gcHandle.Free();
        }
    }

    private static bool TryGetCursorPos(out Point point)
    {
        point = default;
        if (!GetCursorPos(out var nativePoint))
        {
            return false;
        }

        point = new Point(nativePoint.X, nativePoint.Y);
        return true;
    }

    private static string BuildWin32FailureReason(string prefix)
    {
        var code = Marshal.GetLastWin32Error();
        var message = new Win32Exception(code).Message;
        return $"{prefix}: {message} (code={code})";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        public uint Buttons;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;

        public ushort ButtonFlags => (ushort)(Buttons & 0xFFFF);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] rawInputDevices,
        uint numberOfDevices,
        uint sizeOfRawInputDevice);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint sizeOfHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);
}

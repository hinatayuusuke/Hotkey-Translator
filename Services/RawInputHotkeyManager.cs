using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Hotkey_Translator.Services.Application;

namespace Hotkey_Translator.Services;

internal sealed class RawInputHotkeyManager : IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeKeyboard = 1;
    private const ushort RiKeyBreak = 0x0001;
    private const uint RidevInputSink = 0x00000100;

    private readonly Window _window;
    private readonly Dictionary<(int VirtualKey, ModifierKeys Modifiers), HotkeyBindingRegistration> _bindings = new();
    private readonly HashSet<int> _pressedVirtualKeys = new();
    private HwndSource? _source;
    private IntPtr _windowHandle = IntPtr.Zero;
    private bool _registered;

    public RawInputHotkeyManager(Window window)
    {
        _window = window;
    }

    public bool TryRegisterBindings(
        IReadOnlyList<HotkeyBindingRegistration> bindings,
        out string? failureReason)
    {
        failureReason = null;
        if (!EnsureRegistered(out failureReason))
        {
            return false;
        }

        _bindings.Clear();
        foreach (var binding in bindings)
        {
            var virtualKey = NormalizeVirtualKey(KeyInterop.VirtualKeyFromKey(binding.Key));
            if (virtualKey == 0)
            {
                failureReason = $"invalid_virtual_key: {binding.Name}";
                _bindings.Clear();
                return false;
            }

            var tuple = (virtualKey, NormalizeModifiers(binding.Modifiers));
            if (!_bindings.TryAdd(tuple, binding))
            {
                failureReason = $"duplicate_binding: {binding.Name}";
                _bindings.Clear();
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _bindings.Clear();
        _pressedVirtualKeys.Clear();
        _registered = false;
    }

    private bool EnsureRegistered(out string? failureReason)
    {
        failureReason = null;
        if (_registered)
        {
            return true;
        }

        _windowHandle = new WindowInteropHelper(_window).Handle;
        if (_windowHandle == IntPtr.Zero)
        {
            failureReason = "rawinput_window_handle_unavailable";
            return false;
        }

        _source = HwndSource.FromHwnd(_windowHandle);
        if (_source == null)
        {
            failureReason = "rawinput_hwnd_source_unavailable";
            return false;
        }

        _source.AddHook(WndProc);
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01, // Generic Desktop Controls
                Usage = 0x06,     // Keyboard
                Flags = RidevInputSink,
                Target = _windowHandle
            }
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            _source.RemoveHook(WndProc);
            _source = null;
            failureReason = BuildWin32FailureReason("register_raw_input_devices_failed");
            return false;
        }

        _registered = true;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput)
        {
            return IntPtr.Zero;
        }

        if (TryReadKeyboardData(lParam, out var keyboardData))
        {
            ProcessKeyboardData(keyboardData);
        }

        handled = false;
        return IntPtr.Zero;
    }

    private void ProcessKeyboardData(RawKeyboard keyboard)
    {
        var virtualKey = NormalizeVirtualKey(keyboard.VirtualKey);
        if (virtualKey == 0)
        {
            return;
        }

        var isBreak = (keyboard.Flags & RiKeyBreak) != 0;
        if (isBreak)
        {
            _pressedVirtualKeys.Remove(virtualKey);
            return;
        }

        // WHY: Suppress auto-repeat while key is held so behavior matches one-shot WM_HOTKEY triggering.
        if (!_pressedVirtualKeys.Add(virtualKey))
        {
            return;
        }

        var modifiers = GetActiveModifiers();
        if (_bindings.TryGetValue((virtualKey, modifiers), out var binding))
        {
            binding.Handler?.Invoke(this, EventArgs.Empty);
        }
    }

    private static ModifierKeys GetActiveModifiers()
    {
        var modifiers = ModifierKeys.None;
        if (IsVirtualKeyDown(0x11)) // VK_CONTROL
        {
            modifiers |= ModifierKeys.Control;
        }

        if (IsVirtualKeyDown(0x12)) // VK_MENU
        {
            modifiers |= ModifierKeys.Alt;
        }

        if (IsVirtualKeyDown(0x10)) // VK_SHIFT
        {
            modifiers |= ModifierKeys.Shift;
        }

        return modifiers;
    }

    private static bool IsVirtualKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static ModifierKeys NormalizeModifiers(ModifierKeys modifiers)
    {
        var normalized = ModifierKeys.None;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            normalized |= ModifierKeys.Control;
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            normalized |= ModifierKeys.Alt;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            normalized |= ModifierKeys.Shift;
        }

        return normalized;
    }

    private static int NormalizeVirtualKey(int virtualKey)
    {
        return virtualKey switch
        {
            0xA0 or 0xA1 => 0x10, // VK_LSHIFT, VK_RSHIFT -> VK_SHIFT
            0xA2 or 0xA3 => 0x11, // VK_LCONTROL, VK_RCONTROL -> VK_CONTROL
            0xA4 or 0xA5 => 0x12, // VK_LMENU, VK_RMENU -> VK_MENU
            _ => virtualKey
        };
    }

    private static bool TryReadKeyboardData(IntPtr rawInputHandle, out RawKeyboard keyboardData)
    {
        keyboardData = default;
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
            if (header.Type != RimTypeKeyboard)
            {
                return false;
            }

            var keyboardOffset = Marshal.SizeOf<RawInputHeader>();
            var keyboardPtr = IntPtr.Add(ptr, keyboardOffset);
            keyboardData = Marshal.PtrToStructure<RawKeyboard>(keyboardPtr);
            return true;
        }
        finally
        {
            gcHandle.Free();
        }
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
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}

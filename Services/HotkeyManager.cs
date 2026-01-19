using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Hotkey_Translator.Services;

public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly Window _window;
    private readonly int _id;
    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private HwndSource? _source;

    public HotkeyManager(Window window, Key key, ModifierKeys modifiers, int id = 1)
    {
        _window = window;
        _id = id;
        _modifiers = (uint)modifiers;
        _virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
    }

    public event EventHandler? HotkeyPressed;

    public void Register()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);

        if (!RegisterHotKey(handle, _id, _modifiers, _virtualKey))
        {
            throw new InvalidOperationException("Failed to register hotkey.");
        }
    }

    public void Dispose()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        UnregisterHotKey(handle, _id);
        _source?.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

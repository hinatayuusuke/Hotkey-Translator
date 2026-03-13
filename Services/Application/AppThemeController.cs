using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Hotkey_Translator.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Hotkey_Translator.Services.Application;

internal sealed class AppThemeController
{
    // COMPAT: Windows 11 uses attribute 20, while older Windows 10 builds may only honor 19.
    private const int DwmUseImmersiveDarkModeAttribute = 20;
    private const int DwmUseImmersiveDarkModeLegacyAttribute = 19;

    public void Apply(AppSettings settings, Window window)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(window);

        var applicationTheme = settings.ThemeMode == AppThemeMode.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        ApplicationThemeManager.Apply(applicationTheme, WindowBackdropType.None, true);
        ApplyStandardTitleBarTheme(window, applicationTheme == ApplicationTheme.Dark);
    }

    // WHY: Standard caption buttons are painted by DWM, so the client-area theme does not affect
    // them unless the HWND opt-in attribute is updated separately.
    private static void ApplyStandardTitleBarTheme(Window window, bool useDarkTitleBar)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (TrySetDwmWindowAttribute(handle, DwmUseImmersiveDarkModeAttribute, useDarkTitleBar))
        {
            return;
        }

        _ = TrySetDwmWindowAttribute(handle, DwmUseImmersiveDarkModeLegacyAttribute, useDarkTitleBar);
    }

    private static bool TrySetDwmWindowAttribute(IntPtr handle, int attribute, bool enabled)
    {
        var value = enabled ? 1 : 0;
        return DwmSetWindowAttribute(handle, attribute, ref value, Marshal.SizeOf<int>()) == 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}

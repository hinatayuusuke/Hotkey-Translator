using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Hotkey_Translator.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Hotkey_Translator.Services.Application;

internal readonly record struct AppThemeApplyResult(
    ApplicationTheme ApplicationTheme,
    IntPtr WindowHandle,
    bool DarkTitleBarRequested,
    bool DarkTitleBarApplied,
    int? PreferredAttribute,
    int? PreferredHResult,
    int? FallbackAttribute,
    int? FallbackHResult);

internal sealed class AppThemeController
{
    // COMPAT: Windows 11 uses attribute 20, while older Windows 10 builds may only honor 19.
    private const int DwmUseImmersiveDarkModeAttribute = 20;
    private const int DwmUseImmersiveDarkModeLegacyAttribute = 19;

    public AppThemeApplyResult Apply(AppSettings settings, Window window)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(window);

        var applicationTheme = settings.ThemeMode == AppThemeMode.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        ApplicationThemeManager.Apply(applicationTheme, WindowBackdropType.None, true);
        return ApplyStandardTitleBarTheme(window, applicationTheme, applicationTheme == ApplicationTheme.Dark);
    }

    // WHY: Standard caption buttons are painted by DWM, so the client-area theme does not affect
    // them unless the HWND opt-in attribute is updated separately.
    private static AppThemeApplyResult ApplyStandardTitleBarTheme(
        Window window,
        ApplicationTheme applicationTheme,
        bool useDarkTitleBar)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return new AppThemeApplyResult(
                applicationTheme,
                handle,
                useDarkTitleBar,
                DarkTitleBarApplied: false,
                PreferredAttribute: null,
                PreferredHResult: null,
                FallbackAttribute: null,
                FallbackHResult: null);
        }

        var preferredHr = SetDwmWindowAttribute(handle, DwmUseImmersiveDarkModeAttribute, useDarkTitleBar);
        if (preferredHr == 0)
        {
            return new AppThemeApplyResult(
                applicationTheme,
                handle,
                useDarkTitleBar,
                DarkTitleBarApplied: true,
                PreferredAttribute: DwmUseImmersiveDarkModeAttribute,
                PreferredHResult: preferredHr,
                FallbackAttribute: null,
                FallbackHResult: null);
        }

        var fallbackHr = SetDwmWindowAttribute(handle, DwmUseImmersiveDarkModeLegacyAttribute, useDarkTitleBar);
        return new AppThemeApplyResult(
            applicationTheme,
            handle,
            useDarkTitleBar,
            DarkTitleBarApplied: fallbackHr == 0,
            PreferredAttribute: DwmUseImmersiveDarkModeAttribute,
            PreferredHResult: preferredHr,
            FallbackAttribute: DwmUseImmersiveDarkModeLegacyAttribute,
            FallbackHResult: fallbackHr);
    }

    private static int SetDwmWindowAttribute(IntPtr handle, int attribute, bool enabled)
    {
        var value = enabled ? 1 : 0;
        return DwmSetWindowAttribute(handle, attribute, ref value, Marshal.SizeOf<int>());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}

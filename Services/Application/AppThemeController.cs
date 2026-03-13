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
    int? FallbackHResult,
    uint? CaptionColor,
    int? CaptionColorHResult,
    uint? TextColor,
    int? TextColorHResult);

internal sealed class AppThemeController
{
    // COMPAT: Windows 11 uses attribute 20, while older Windows 10 builds may only honor 19.
    private const int DwmUseImmersiveDarkModeAttribute = 20;
    private const int DwmUseImmersiveDarkModeLegacyAttribute = 19;
    private const int DwmCaptionColorAttribute = 35;
    private const int DwmTextColorAttribute = 36;

    private static readonly uint DarkCaptionColor = ToColorRef(31, 31, 31);
    private static readonly uint LightCaptionColor = ToColorRef(243, 243, 243);
    private static readonly uint DarkTextColor = ToColorRef(255, 255, 255);
    private static readonly uint LightTextColor = ToColorRef(0, 0, 0);

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
        var captionColor = useDarkTitleBar ? DarkCaptionColor : LightCaptionColor;
        var textColor = useDarkTitleBar ? DarkTextColor : LightTextColor;
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
                FallbackHResult: null,
                CaptionColor: captionColor,
                CaptionColorHResult: null,
                TextColor: textColor,
                TextColorHResult: null);
        }

        var preferredHr = SetDwmWindowAttribute(handle, DwmUseImmersiveDarkModeAttribute, useDarkTitleBar);
        int? fallbackAttribute = null;
        int? fallbackHr = null;
        var darkTitleBarApplied = preferredHr == 0;
        if (!darkTitleBarApplied)
        {
            fallbackAttribute = DwmUseImmersiveDarkModeLegacyAttribute;
            fallbackHr = SetDwmWindowAttribute(handle, DwmUseImmersiveDarkModeLegacyAttribute, useDarkTitleBar);
            darkTitleBarApplied = fallbackHr == 0;
        }

        // WHY: Some Windows configurations accept immersive dark mode but still choose a caption
        // palette that hides the title text, so we explicitly set caption/text colors as a fallback-safe default.
        var captionColorHr = SetDwmColorAttribute(handle, DwmCaptionColorAttribute, captionColor);
        var textColorHr = SetDwmColorAttribute(handle, DwmTextColorAttribute, textColor);
        return new AppThemeApplyResult(
            applicationTheme,
            handle,
            useDarkTitleBar,
            DarkTitleBarApplied: darkTitleBarApplied,
            PreferredAttribute: DwmUseImmersiveDarkModeAttribute,
            PreferredHResult: preferredHr,
            FallbackAttribute: fallbackAttribute,
            FallbackHResult: fallbackHr,
            CaptionColor: captionColor,
            CaptionColorHResult: captionColorHr,
            TextColor: textColor,
            TextColorHResult: textColorHr);
    }

    private static int SetDwmWindowAttribute(IntPtr handle, int attribute, bool enabled)
    {
        var value = enabled ? 1 : 0;
        return DwmSetWindowAttribute(handle, attribute, ref value, Marshal.SizeOf<int>());
    }

    private static int SetDwmColorAttribute(IntPtr handle, int attribute, uint colorRef)
    {
        var value = unchecked((int)colorRef);
        return DwmSetWindowAttribute(handle, attribute, ref value, Marshal.SizeOf<int>());
    }

    private static uint ToColorRef(byte red, byte green, byte blue) =>
        (uint)(red | (green << 8) | (blue << 16));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}

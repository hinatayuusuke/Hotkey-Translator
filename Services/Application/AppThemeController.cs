using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Hotkey_Translator.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace Hotkey_Translator.Services.Application;

internal readonly record struct AppThemeApplyResult(
    ApplicationTheme ApplicationTheme,
    IntPtr WindowHandle,
    bool TitleBarManagedByOs);

internal sealed class AppThemeController
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DarkCaptionColor = 0x001F1F1F;
    private const int LightCaptionColor = 0x00F3F3F3;
    private const int DarkTextColor = 0x00FFFFFF;
    private const int LightTextColor = 0x00000000;

    public AppThemeApplyResult Apply(AppSettings settings, Window window)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(window);

        var applicationTheme = settings.ThemeMode == AppThemeMode.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        ApplyThemeResources(applicationTheme);
        var handle = new WindowInteropHelper(window).Handle;
        ApplyStandardTitleBarTheme(handle, applicationTheme);
        // WHY: Keep the standard non-client/title-bar rendering so the app name remains visible,
        // while using DWM only to hint the caption colors that should track the selected app theme.
        return new AppThemeApplyResult(
            applicationTheme,
            handle,
            TitleBarManagedByOs: true);
    }

    private static void ApplyThemeResources(ApplicationTheme applicationTheme)
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var mergedDictionaries = resources.MergedDictionaries;
        for (var index = 0; index < mergedDictionaries.Count; index++)
        {
            if (mergedDictionaries[index] is not ThemesDictionary currentThemeDictionary)
            {
                continue;
            }

            // WHY: Replacing the merged dictionary updates WPF-UI brushes without touching
            // non-client/title-bar behavior that ApplicationThemeManager.Apply altered.
            mergedDictionaries[index] = new ThemesDictionary
            {
                Theme = applicationTheme
            };
            return;
        }

        mergedDictionaries.Insert(0, new ThemesDictionary
        {
            Theme = applicationTheme
        });
    }

    private static void ApplyStandardTitleBarTheme(IntPtr handle, ApplicationTheme applicationTheme)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDarkMode = applicationTheme == ApplicationTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref useDarkMode, sizeof(int));

        var captionColor = applicationTheme == ApplicationTheme.Dark ? DarkCaptionColor : LightCaptionColor;
        var textColor = applicationTheme == ApplicationTheme.Dark ? DarkTextColor : LightTextColor;
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);
}

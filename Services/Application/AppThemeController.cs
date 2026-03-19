using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
    private const string BusyDialogBackgroundBrushKey = "BusyDialogBackgroundBrush";
    private const string HotkeyConflictBorderBrushKey = "HotkeyConflictBorderBrush";
    private const string HotkeyConflictBackgroundBrushKey = "HotkeyConflictBackgroundBrush";
    private const string HotkeyConflictTextBrushKey = "HotkeyConflictTextBrush";
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
            ApplyCustomThemeResources(resources, applicationTheme);
            return;
        }

        mergedDictionaries.Insert(0, new ThemesDictionary
        {
            Theme = applicationTheme
        });
        ApplyCustomThemeResources(resources, applicationTheme);
    }

    private static void ApplyCustomThemeResources(ResourceDictionary resources, ApplicationTheme applicationTheme)
    {
        // WHY: WPF-UI card brushes can render with theme translucency, but the busy dialog must stay fully opaque
        // while still tracking the selected light/dark theme.
        var brush = new SolidColorBrush(applicationTheme == ApplicationTheme.Dark
            ? Color.FromRgb(0x2B, 0x2B, 0x2B)
            : Color.FromRgb(0xF8, 0xF8, 0xF8));
        brush.Freeze();
        resources[BusyDialogBackgroundBrushKey] = brush;

        // WHY: Hotkey conflict visuals need separate light/dark tuning so the warning stays legible
        // without overpowering the rest of the settings panel in either theme.
        var conflictBorderBrush = new SolidColorBrush(applicationTheme == ApplicationTheme.Dark
            ? Color.FromRgb(0xCC, 0x6B, 0x6B)
            : Color.FromRgb(0xC0, 0x3A, 0x3A));
        conflictBorderBrush.Freeze();
        resources[HotkeyConflictBorderBrushKey] = conflictBorderBrush;

        var conflictBackgroundBrush = new SolidColorBrush(applicationTheme == ApplicationTheme.Dark
            ? Color.FromArgb(0x33, 0xCC, 0x6B, 0x6B)
            : Color.FromArgb(0x1A, 0xF2, 0x5B, 0x5B));
        conflictBackgroundBrush.Freeze();
        resources[HotkeyConflictBackgroundBrushKey] = conflictBackgroundBrush;

        var conflictTextBrush = new SolidColorBrush(applicationTheme == ApplicationTheme.Dark
            ? Color.FromRgb(0xFF, 0xB3, 0xB3)
            : Color.FromRgb(0xB4, 0x23, 0x18));
        conflictTextBrush.Freeze();
        resources[HotkeyConflictTextBrushKey] = conflictTextBrush;
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

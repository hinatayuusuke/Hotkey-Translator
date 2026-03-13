using System;
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
    public AppThemeApplyResult Apply(AppSettings settings, Window window)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(window);

        var applicationTheme = settings.ThemeMode == AppThemeMode.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        ApplyThemeResources(applicationTheme);
        var handle = new WindowInteropHelper(window).Handle;
        // WHY: Rely on the OS-standard non-client rendering because explicit DWM title-bar attributes
        // hid the app name on some environments even when the calls succeeded.
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
}

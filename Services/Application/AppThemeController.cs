using System;
using System.Windows;
using System.Windows.Interop;
using Hotkey_Translator.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

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

        ApplicationThemeManager.Apply(applicationTheme, WindowBackdropType.None, true);
        var handle = new WindowInteropHelper(window).Handle;
        // WHY: Rely on the OS-standard non-client rendering because explicit DWM title-bar attributes
        // hid the app name on some environments even when the calls succeeded.
        return new AppThemeApplyResult(
            applicationTheme,
            handle,
            TitleBarManagedByOs: true);
    }
}

using System.Windows;
using System.Windows.Controls;

namespace Hotkey_Translator.UI;

public partial class HookFullscreenControl : UserControl
{
    public HookFullscreenControl()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler? BrowseLauncherClicked;

    public event RoutedEventHandler? LaunchClicked;

    public event RoutedEventHandler? CopySteamLaunchOptionsClicked;

    private void OnBrowseLauncherClicked(object sender, RoutedEventArgs e)
    {
        BrowseLauncherClicked?.Invoke(sender, e);
    }

    private void OnLaunchClicked(object sender, RoutedEventArgs e)
    {
        LaunchClicked?.Invoke(sender, e);
    }

    private void OnCopySteamLaunchOptionsClicked(object sender, RoutedEventArgs e)
    {
        CopySteamLaunchOptionsClicked?.Invoke(sender, e);
    }
}

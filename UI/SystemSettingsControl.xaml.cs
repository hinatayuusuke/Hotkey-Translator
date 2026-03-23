using System.Windows.Controls;
using System.Windows;

namespace Hotkey_Translator.UI;

public partial class SystemSettingsControl : UserControl
{
    public SystemSettingsControl()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler? ResetAllSettingsClicked;

    private void OnResetAllSettingsClicked(object sender, RoutedEventArgs e)
    {
        ResetAllSettingsClicked?.Invoke(sender, e);
    }
}

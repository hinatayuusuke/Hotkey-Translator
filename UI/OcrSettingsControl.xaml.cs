using System.Windows;
using System.Windows.Controls;

namespace Hotkey_Translator.UI;

public partial class OcrSettingsControl : UserControl
{
    public OcrSettingsControl()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler? InstallWinRtLanguagePackClicked;

    public TextBlock WinRtLanguagePackStatusTextBlock => WinRtLanguagePackStatusText;

    public Button InstallWinRtLanguagePackButtonElement => InstallWinRtLanguagePackButton;

    private void OnInstallWinRtLanguagePackClicked(object sender, RoutedEventArgs e)
    {
        InstallWinRtLanguagePackClicked?.Invoke(sender, e);
    }
}

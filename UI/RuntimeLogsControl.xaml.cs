using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hotkey_Translator.UI;

public partial class RuntimeLogsControl : UserControl
{
    public RuntimeLogsControl()
    {
        InitializeComponent();
    }

    public event MouseButtonEventHandler? OcrPreviewClicked;

    public event MouseButtonEventHandler? PinnedPreviewClicked;

    public double DrawerActualHeight => BottomDrawerBorder.ActualHeight;

    public TextBox LogTextBox => LogBox;

    public ImageSource? OcrPreviewSource => OcrPreprocessPreviewImage.Source;

    public ImageSource? PinnedPreviewSource => PinnedCaptureThumbnailImage.Source;

    public void SetOcrPreview(BitmapSource source)
    {
        OcrPreprocessPreviewImage.Source = source;
        OcrPreprocessPreviewHint.Visibility = System.Windows.Visibility.Collapsed;
    }

    public void SetPinnedThumbnail(BitmapSource? source, string message)
    {
        PinnedCaptureThumbnailImage.Source = source;
        if (source == null)
        {
            PinnedCaptureThumbnailHint.Text = string.IsNullOrWhiteSpace(message) ? "No fixed target" : message;
            PinnedCaptureThumbnailHint.Visibility = System.Windows.Visibility.Visible;
            return;
        }

        PinnedCaptureThumbnailHint.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void OnOcrPreviewClicked(object sender, MouseButtonEventArgs e)
    {
        OcrPreviewClicked?.Invoke(sender, e);
    }

    private void OnPinnedPreviewClicked(object sender, MouseButtonEventArgs e)
    {
        PinnedPreviewClicked?.Invoke(sender, e);
    }
}

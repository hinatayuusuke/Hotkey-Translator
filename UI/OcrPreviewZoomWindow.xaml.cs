using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Hotkey_Translator.UI;

public partial class OcrPreviewZoomWindow : Window
{
    private const double MinZoom = 0.2;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.15;
    private double _zoom = 1.0;

    public OcrPreviewZoomWindow()
    {
        InitializeComponent();
        UpdateZoomText();
    }

    public void SetImage(ImageSource? source)
    {
        PreviewImage.Source = source;
        PreviewPlaceholder.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PreviewImage.Source == null)
        {
            return;
        }

        var factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        PreviewScaleTransform.ScaleX = _zoom;
        PreviewScaleTransform.ScaleY = _zoom;
        UpdateZoomText();
        e.Handled = true;
    }

    private void UpdateZoomText()
    {
        ZoomText.Text = $"{_zoom * 100:0}%";
    }
}

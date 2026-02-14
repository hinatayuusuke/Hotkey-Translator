using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Hotkey_Translator.ViewModels;

namespace Hotkey_Translator;

public partial class MainWindow
{
    private void OnOcrPreprocessPreviewReady(Bitmap bitmap)
    {
        try
        {
            _previewFrameDispatcher.Enqueue(bitmap);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to queue OCR preprocess preview.");
        }
    }

    private void OnMainWindowViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsBottomPanelOpen))
        {
            _drawerLayoutController.SyncForCurrentState();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsBottomPanelOpen) or nameof(MainWindowViewModel.BottomPreviewPaneVisible))
        {
            _previewFrameDispatcher.RequestFlushIfVisible();
        }
    }

    private bool ShouldRenderBottomPreviewPane()
    {
        return _mainWindowViewModel.IsBottomPanelOpen && _mainWindowViewModel.BottomPreviewPaneVisible;
    }

    private void OnOcrPreviewClicked(object sender, MouseButtonEventArgs e)
    {
        _previewZoomCoordinator.ShowOrActivate(OcrPreprocessPreviewImage.Source);
        e.Handled = true;
    }

    private void ApplyPreviewBitmapSource(BitmapSource source)
    {
        OcrPreprocessPreviewImage.Source = source;
        OcrPreprocessPreviewHint.Visibility = Visibility.Collapsed;
        _previewZoomCoordinator.UpdateImage(source);
    }
}

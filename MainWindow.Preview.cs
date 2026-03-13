using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Hotkey_Translator.Models;
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
        _previewZoomCoordinator.ShowOrActivate(RuntimeLogsControl.OcrPreviewSource);
        e.Handled = true;
    }

    private void OnPinnedPreviewClicked(object sender, MouseButtonEventArgs e)
    {
        if (RuntimeLogsControl.PinnedPreviewSource != null)
        {
            _previewZoomCoordinator.ShowOrActivate(RuntimeLogsControl.PinnedPreviewSource);
        }

        e.Handled = true;
    }

    private void UpdatePinnedThumbnailFromLockResult(FixedCaptureWindowSpec? spec)
    {
        if (spec == null)
        {
            SetPinnedCaptureThumbnail(null, "Failed to get thumbnail");
            return;
        }

        if (TryCapturePinnedThumbnail(spec.Hwnd, out var source, out var reason))
        {
            SetPinnedCaptureThumbnail(source, string.Empty);
            return;
        }

        _logger?.Error($"Pinned thumbnail capture failed: {reason}");
        SetPinnedCaptureThumbnail(null, "Failed to get thumbnail");
    }

    private void ClearPinnedCaptureThumbnail(string message)
    {
        SetPinnedCaptureThumbnail(null, string.IsNullOrWhiteSpace(message) ? "No fixed target" : message);
    }

    private void ApplyPreviewBitmapSource(BitmapSource source)
    {
        RuntimeLogsControl.SetOcrPreview(source);
        _previewZoomCoordinator.UpdateImage(source);
    }

    // WHY: Pinned capture must stay independent from OCR preview updates to avoid cross-refresh side effects.
    private void SetPinnedCaptureThumbnail(BitmapSource? source, string message)
    {
        RuntimeLogsControl.SetPinnedThumbnail(source, message);
    }

    private static bool TryCapturePinnedThumbnail(long hwndValue, out BitmapSource? source, out string reason)
    {
        source = null;
        reason = string.Empty;

        if (hwndValue == 0)
        {
            reason = "invalid handle";
            return false;
        }

        var hwnd = new IntPtr(hwndValue);
        if (!GetWindowRect(hwnd, out var rect))
        {
            reason = "GetWindowRect failed";
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            reason = $"window bounds invalid ({width}x{height})";
            return false;
        }

        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }

            source = CreateBitmapSource(bitmap);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static BitmapSource CreateBitmapSource(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var source = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                bitmap.HorizontalResolution,
                bitmap.VerticalResolution,
                System.Windows.Media.PixelFormats.Pbgra32,
                null,
                buffer,
                stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

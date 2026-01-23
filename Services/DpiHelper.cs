using System.Windows;
using System.Windows.Media;

namespace Hotkey_Translator.Services;

public static class DpiHelper
{
    public static Rect ScreenRectToWindowDip(Window window, Rect screenDeviceRect)
    {
        // WHY: OCR bounds are stored in screen device pixels; PointFromScreen converts to window DIP.
        var topLeft = window.PointFromScreen(new Point(screenDeviceRect.X, screenDeviceRect.Y));
        var bottomRight = window.PointFromScreen(new Point(screenDeviceRect.Right, screenDeviceRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    public static Rect DeviceRectToDip(Visual visual, Rect deviceRect)
    {
        var source = PresentationSource.FromVisual(visual);
        if (source?.CompositionTarget == null)
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            return new Rect(
                deviceRect.X / dpi.DpiScaleX,
                deviceRect.Y / dpi.DpiScaleY,
                deviceRect.Width / dpi.DpiScaleX,
                deviceRect.Height / dpi.DpiScaleY);
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new Point(deviceRect.X, deviceRect.Y));
        var bottomRight = transform.Transform(new Point(deviceRect.Right, deviceRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    public static Rect DipRectToDevice(Visual visual, Rect dipRect)
    {
        var source = PresentationSource.FromVisual(visual);
        if (source?.CompositionTarget == null)
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            return new Rect(
                dipRect.X * dpi.DpiScaleX,
                dipRect.Y * dpi.DpiScaleY,
                dipRect.Width * dpi.DpiScaleX,
                dipRect.Height * dpi.DpiScaleY);
        }

        var transform = source.CompositionTarget.TransformToDevice;
        var topLeft = transform.Transform(new Point(dipRect.X, dipRect.Y));
        var bottomRight = transform.Transform(new Point(dipRect.Right, dipRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}

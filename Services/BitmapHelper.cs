using System;
using System.Drawing;
using System.Windows;

namespace Hotkey_Translator.Services;

public static class BitmapHelper
{
    public static Bitmap Crop(Bitmap source, Rect rect)
    {
        var safeRect = new Rect(0, 0, source.Width, source.Height);
        var intersect = Rect.Intersect(safeRect, rect);
        if (intersect.IsEmpty)
        {
            throw new ArgumentException("Crop rect is outside the bitmap bounds.");
        }

        var x = (int)Math.Floor(intersect.X);
        var y = (int)Math.Floor(intersect.Y);
        var width = (int)Math.Ceiling(intersect.Width);
        var height = (int)Math.Ceiling(intersect.Height);

        width = Math.Min(width, source.Width - x);
        height = Math.Min(height, source.Height - y);

        var cropRect = new Rectangle(x, y, width, height);

        return source.Clone(cropRect, source.PixelFormat);
    }
}

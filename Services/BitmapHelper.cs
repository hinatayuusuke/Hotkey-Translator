using System;
using System.Drawing;
using System.Windows;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;

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

    public static Bitmap FromSoftwareBitmap(SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            bitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var buffer = new byte[width * height * 4];
            bitmap.CopyToBuffer(buffer.AsBuffer());
            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            result.UnlockBits(data);
        }

        return result;
    }

    public static Bitmap Resize(Bitmap source, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Resize dimensions must be positive.");
        }

        if (width == source.Width && height == source.Height)
        {
            return (Bitmap)source.Clone();
        }

        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        result.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        using var graphics = Graphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        // WHY: Bilinear downsampling balances OCR legibility with performance.
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        return result;
    }
}

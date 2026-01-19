using System;
using System.Drawing;
using System.Windows;

namespace Hotkey_Translator.Models;

public sealed class CaptureFrame : IDisposable
{
    public CaptureFrame(Bitmap bitmap, Rect bounds)
    {
        Bitmap = bitmap;
        Bounds = bounds;
    }

    public Bitmap Bitmap { get; }
    public Rect Bounds { get; }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}

using System;
using System;
using System.Drawing;
using System.Windows;

namespace Hotkey_Translator.Models;

public sealed class CaptureFrame : IDisposable
{
    public CaptureFrame(Bitmap bitmap, Rect bounds, CaptureProviderKind providerKind, DateTimeOffset timestamp)
    {
        Bitmap = bitmap;
        Bounds = bounds;
        ProviderKind = providerKind;
        Timestamp = timestamp;
    }

    public Bitmap Bitmap { get; }
    public Rect Bounds { get; }
    public CaptureProviderKind ProviderKind { get; }
    public DateTimeOffset Timestamp { get; }
    public bool IsBlack { get; set; }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}

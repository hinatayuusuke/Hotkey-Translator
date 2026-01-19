using System;
using System.Windows;

namespace Hotkey_Translator.Models;

public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public Rect ToAbsolute(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return Rect.Empty;
        }

        var clamped = Clamp();
        var x = bounds.X + (clamped.X * bounds.Width);
        var y = bounds.Y + (clamped.Y * bounds.Height);
        var width = clamped.Width * bounds.Width;
        var height = clamped.Height * bounds.Height;
        return Rect.Intersect(bounds, new Rect(x, y, width, height));
    }

    public static NormalizedRect FromAbsolute(Rect absolute, Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new NormalizedRect(0, 0, 0, 0);
        }

        var intersect = Rect.Intersect(bounds, absolute);
        if (intersect.IsEmpty)
        {
            return new NormalizedRect(0, 0, 0, 0);
        }

        var x = (intersect.X - bounds.X) / bounds.Width;
        var y = (intersect.Y - bounds.Y) / bounds.Height;
        var width = intersect.Width / bounds.Width;
        var height = intersect.Height / bounds.Height;
        return new NormalizedRect(x, y, width, height).Clamp();
    }

    public NormalizedRect Clamp()
    {
        var x = Math.Clamp(X, 0, 1);
        var y = Math.Clamp(Y, 0, 1);
        var width = Math.Clamp(Width, 0, 1 - x);
        var height = Math.Clamp(Height, 0, 1 - y);
        return new NormalizedRect(x, y, width, height);
    }
}

using System.Windows;

namespace Hotkey_Translator.Models;

public readonly record struct SerializableRect(double X, double Y, double Width, double Height)
{
    public Rect ToRect() => new Rect(X, Y, Width, Height);

    public static SerializableRect FromRect(Rect rect) => new SerializableRect(rect.X, rect.Y, rect.Width, rect.Height);

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

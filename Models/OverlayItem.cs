using System.Windows;

namespace Hotkey_Translator.Models;

public sealed record OverlayItem(string Text, Rect Rect, int LineCount = 1, double LineHeight = 0);

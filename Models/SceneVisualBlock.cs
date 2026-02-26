using System.Windows;

namespace Hotkey_Translator.Models;

public sealed record SceneVisualBlock(
    Rect Rect,
    ulong Hash,
    double Area);

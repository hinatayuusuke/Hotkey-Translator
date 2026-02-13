using System.Windows;

namespace Hotkey_Translator.Models;

public sealed record SceneTextBlock(
    Rect Rect,
    string NormalizedText,
    int CharCount);

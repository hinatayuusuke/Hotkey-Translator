using System.Windows;

namespace Hotkey_Translator.Models;

public sealed record OcrLine(string Text, Rect Rect, float Confidence);

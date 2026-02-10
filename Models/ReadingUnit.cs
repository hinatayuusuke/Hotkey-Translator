using System.Collections.Generic;
using System.Windows;

namespace Hotkey_Translator.Models;

public sealed record ReadingUnit(
    int Id,
    string Text,
    Rect Rect,
    int LineCount,
    double LineHeight,
    IReadOnlyList<int> SourceIndices);

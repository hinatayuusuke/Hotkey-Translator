using System;
using System.Collections.Generic;
using System.Windows;

namespace Hotkey_Translator.Models;

public sealed record SceneTextSnapshot(
    IReadOnlyList<SceneTextBlock> Blocks,
    IReadOnlyList<ReadingUnit> ReadingUnits,
    Rect RoiScreen,
    Rect? OverlayClipScreen,
    DateTime CapturedAtUtc,
    string SnapshotSignature);

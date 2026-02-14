using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Orchestration;

internal sealed class PipelineExecutionContext
{
    public PipelineExecutionContext(AppSettings settings, ForceRunOptions options, DateTimeOffset startedAtUtc)
    {
        Settings = settings;
        Options = options;
        StartedAtUtc = startedAtUtc;
    }

    public AppSettings Settings { get; }

    public ForceRunOptions Options { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public CaptureFrame? Frame { get; set; }

    public Rect RoiScreen { get; set; }

    public Rect? OverlayClipScreen { get; set; }

    public Bitmap? RoiSnapshot { get; set; }

    public Rect RoiSnapshotBounds { get; set; }

    public OcrResultModel? OcrResult { get; set; }

    public IReadOnlyList<OcrLine> GroupedLines { get; set; } = Array.Empty<OcrLine>();

    public IReadOnlyList<ReadingUnit> ReadingUnits { get; set; } = Array.Empty<ReadingUnit>();

    public IReadOnlySet<int> ChangedUnitIds { get; set; } = new HashSet<int>();

    public bool IsDiffUnchanged { get; set; }

    public Dictionary<int, string> Translations { get; } = new();

    public IReadOnlyList<OverlayItem> OverlayItems { get; set; } = Array.Empty<OverlayItem>();

    public ulong? RoiHash { get; set; }

    public PipelineStageResult? FinalStageResult { get; set; }
}

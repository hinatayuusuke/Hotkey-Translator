using System;

namespace Hotkey_Translator.Models;

public sealed class GraphicsHookLauncherTargetSignature
{
    public string Key { get; set; } = string.Empty;
    public string ExeName { get; set; } = string.Empty;
    public string? ExePathSuffix { get; set; }
    public GraphicsHookApiKind? PreferredApi { get; set; }
    public string[] WindowClassAllowList { get; set; } = Array.Empty<string>();
    public string[] WindowTitleContainsAny { get; set; } = Array.Empty<string>();
    public bool RequireVisibleTopLevel { get; set; } = true;
    public bool RequireOwnerlessWindow { get; set; } = true;
    public bool RequireExclusiveFullscreen { get; set; } = true;
    public int MinClientWidth { get; set; } = 1280;
    public int MinClientHeight { get; set; } = 720;
    public bool LearnedFromDiscovery { get; set; }
    public DateTimeOffset? LearnedAtUtc { get; set; }
}

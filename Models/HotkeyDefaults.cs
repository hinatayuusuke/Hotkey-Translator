using System;
using System.Collections.Generic;

namespace Hotkey_Translator.Models;

internal static class HotkeyDefaults
{
    public const string DisabledKey = "Disable";
    public const string NoneModifiers = "None";

    public const string RunOnceKey = "F8";
    public const string RunOnceModifiers = NoneModifiers;
    public const string RunNextRoiKey = DisabledKey;
    public const string RunNextRoiModifiers = NoneModifiers;
    public const string RunNextNextRoiKey = DisabledKey;
    public const string RunNextNextRoiModifiers = NoneModifiers;
    public const string ToggleOverlayKey = "F9";
    public const string ToggleOverlayModifiers = NoneModifiers;
    public const string ForceRunKey = "F10";
    public const string ForceRunModifiers = NoneModifiers;
    public const string ForceRunNextRoiKey = DisabledKey;
    public const string ForceRunNextRoiModifiers = NoneModifiers;
    public const string ForceRunNextNextRoiKey = DisabledKey;
    public const string ForceRunNextNextRoiModifiers = NoneModifiers;
    public const string ForceGeminiStrictKey = DisabledKey;
    public const string ForceGeminiStrictModifiers = NoneModifiers;
    public const string OcrOnlyKey = DisabledKey;
    public const string OcrOnlyModifiers = NoneModifiers;
    public const string ToggleSceneAutoTranslateKey = DisabledKey;
    public const string ToggleSceneAutoTranslateModifiers = NoneModifiers;
    public const string SelectRoiKey = "F6";
    public const string SelectRoiModifiers = NoneModifiers;
    public const string SelectFixedOverlayFrameKey = DisabledKey;
    public const string SelectFixedOverlayFrameModifiers = NoneModifiers;
    public const string NextRoiPresetKey = DisabledKey;
    public const string NextRoiPresetModifiers = NoneModifiers;
    public const string PreviousRoiPresetKey = DisabledKey;
    public const string PreviousRoiPresetModifiers = NoneModifiers;
    public const string LockCaptureWindowKey = "F7";
    public const string LockCaptureWindowModifiers = NoneModifiers;
    public const string UnlockCaptureWindowKey = "F7";
    public const string UnlockCaptureWindowModifiers = "Shift";
    public const string ToggleMirrorFullscreenKey = "F7";
    public const string ToggleMirrorFullscreenModifiers = "Control, Shift";

    private static readonly IReadOnlyList<string> SupportedKeyValues = CreateSupportedKeyValues();
    private static readonly IReadOnlyDictionary<string, string> CanonicalKeyValues = CreateCanonicalKeyValues();

    public static IReadOnlyList<string> KeyOptions => SupportedKeyValues;

    public static string NormalizeStoredKey(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return DisabledKey;
        }

        return CanonicalKeyValues.TryGetValue(normalized, out var canonical)
            ? canonical
            : DisabledKey;
    }

    public static bool IsDisabledKey(string? value)
    {
        return string.Equals(NormalizeStoredKey(value), DisabledKey, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> CreateSupportedKeyValues()
    {
        var keys = new List<string>
        {
            DisabledKey
        };

        for (var i = 1; i <= 12; i++)
        {
            keys.Add($"F{i}");
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys.Add(c.ToString());
        }

        return keys;
    }

    private static IReadOnlyDictionary<string, string> CreateCanonicalKeyValues()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SupportedKeyValues)
        {
            map[key] = key;
        }

        return map;
    }
}

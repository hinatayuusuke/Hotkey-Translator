using System;

namespace Hotkey_Translator.Services.Settings;

internal static class SettingsRuleHelpers
{
    public static bool ClampSetting(double value, double min, double max, double fallback, out double normalized)
    {
        if (!double.IsFinite(value))
        {
            normalized = fallback;
            return true;
        }

        var clamped = Math.Clamp(value, min, max);
        if (Math.Abs(clamped - value) < 0.0001)
        {
            normalized = clamped;
            return false;
        }

        normalized = clamped;
        return true;
    }

    public static bool ClampSetting(int value, int min, int max, int fallback, out int normalized)
    {
        if (value < min || value > max)
        {
            normalized = fallback;
            return true;
        }

        normalized = value;
        return false;
    }
}

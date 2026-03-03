using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class GraphicsHookSettingsRule : ISettingsRule
{
    public string RuleId => "graphics_hook_settings";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;

        if (!Enum.IsDefined(typeof(GraphicsHookApiKind), settings.GraphicsHookApi))
        {
            settings.GraphicsHookApi = GraphicsHookApiKind.Dx11;
            changed = true;
        }

        changed |= SettingsRuleHelpers.ClampSetting(
            settings.GraphicsHookCaptureFpsLimit,
            1,
            120,
            15,
            out var captureFpsLimit);

        settings.GraphicsHookCaptureFpsLimit = captureFpsLimit;

        if (string.IsNullOrWhiteSpace(settings.GraphicsHookPipeName))
        {
            settings.GraphicsHookPipeName = "hotkey_translator_hook";
            changed = true;
        }

        if (changed)
        {
            report.Add(
                RuleId,
                $"Normalized Graphics hook settings: api={settings.GraphicsHookApi}, fps_limit={settings.GraphicsHookCaptureFpsLimit}, pipe=\"{settings.GraphicsHookPipeName}\".");
        }

        return changed;
    }
}

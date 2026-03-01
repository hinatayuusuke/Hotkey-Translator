using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class Dx11HookSettingsRule : ISettingsRule
{
    public string RuleId => "dx11_hook_settings";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;

        changed |= SettingsRuleHelpers.ClampSetting(
            settings.Dx11HookCaptureFpsLimit,
            1,
            120,
            15,
            out var captureFpsLimit);

        settings.Dx11HookCaptureFpsLimit = captureFpsLimit;

        if (string.IsNullOrWhiteSpace(settings.Dx11HookPipeName))
        {
            settings.Dx11HookPipeName = "hotkey_translator_hook";
            changed = true;
        }

        if (changed)
        {
            report.Add(
                RuleId,
                $"Normalized DX11 hook settings: fps_limit={settings.Dx11HookCaptureFpsLimit}, pipe=\"{settings.Dx11HookPipeName}\".");
        }

        return changed;
    }
}

using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class NdlOcrSettingsRule : ISettingsRule
{
    public string RuleId => "ndl_ocr";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;

        changed |= NormalizeStringDefault(
            settings.NdlGrpcHost,
            "127.0.0.1",
            value => settings.NdlGrpcHost = value);

        changed |= SettingsRuleHelpers.ClampSetting(settings.NdlGrpcPort, 1, 65535, 50053, out var grpcPort);
        settings.NdlGrpcPort = grpcPort;
        changed |= SettingsRuleHelpers.ClampSetting(settings.NdlGrpcReadyTimeoutMs, 1000, 600000, 120000, out var readyTimeoutMs);
        settings.NdlGrpcReadyTimeoutMs = readyTimeoutMs;
        changed |= SettingsRuleHelpers.ClampSetting(settings.NdlGrpcRestartMax, 1, 20, 3, out var restartMax);
        settings.NdlGrpcRestartMax = restartMax;
        changed |= SettingsRuleHelpers.ClampSetting(settings.NdlGrpcRestartWindowSeconds, 1, 300, 30, out var restartWindowSeconds);
        settings.NdlGrpcRestartWindowSeconds = restartWindowSeconds;

        changed |= NormalizeStringDefault(
            settings.NdlGrpcEndpoint,
            $"http://{settings.NdlGrpcHost}:{settings.NdlGrpcPort}",
            value => settings.NdlGrpcEndpoint = value);

        var normalizedDevice = NormalizeDevice(settings.NdlDevice);
        if (!string.Equals(settings.NdlDevice, normalizedDevice, StringComparison.Ordinal))
        {
            settings.NdlDevice = normalizedDevice;
            changed = true;
        }

        changed |= SettingsRuleHelpers.ClampSetting(settings.NdlDetScoreThreshold, 0.0, 1.0, 0.2, out var detScoreThreshold);
        changed |= SettingsRuleHelpers.ClampSetting(settings.NdlDetConfThreshold, 0.0, 1.0, 0.25, out var detConfThreshold);
        changed |= SettingsRuleHelpers.ClampSetting(settings.NdlDetIouThreshold, 0.0, 1.0, 0.2, out var detIouThreshold);
        settings.NdlDetScoreThreshold = detScoreThreshold;
        settings.NdlDetConfThreshold = detConfThreshold;
        settings.NdlDetIouThreshold = detIouThreshold;

        changed |= NormalizeOptionalPath(settings.NdlModelDir, value => settings.NdlModelDir = value);
        changed |= NormalizeOptionalPath(settings.NdlConfigDir, value => settings.NdlConfigDir = value);

        if (changed)
        {
            report.Add(
                RuleId,
                $"NDLOCR settings were normalized: device={settings.NdlDevice}, endpoint={settings.NdlGrpcEndpoint}.");
        }

        return changed;
    }

    private static string NormalizeDevice(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "cuda" ? "cuda" : "cpu";
    }

    private static bool NormalizeStringDefault(string? current, string fallback, Action<string> assign)
    {
        var normalized = string.IsNullOrWhiteSpace(current) ? fallback : current.Trim();
        if (string.Equals(current, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        assign(normalized);
        return true;
    }

    private static bool NormalizeOptionalPath(string? current, Action<string?> assign)
    {
        var normalized = string.IsNullOrWhiteSpace(current) ? null : current.Trim();
        if (string.Equals(current, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        assign(normalized);
        return true;
    }
}

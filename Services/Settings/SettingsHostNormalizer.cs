using System;
using System.IO;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings;

internal static class SettingsHostNormalizer
{
    private const string DefaultLlamaModelFileName = "HY-MT1.5-1.8B-Q8_0.gguf";

    public static bool NormalizeLlamaSettings(AppSettings settings)
    {
        var changed = false;
        changed |= SetIfDifferent(
            string.IsNullOrWhiteSpace(settings.LlamaHost) ? "127.0.0.1" : settings.LlamaHost.Trim(),
            settings.LlamaHost,
            value => settings.LlamaHost = value);
        changed |= SetIfDifferent(
            settings.LlamaPort <= 0 ? 8088 : settings.LlamaPort,
            settings.LlamaPort,
            value => settings.LlamaPort = value);
        changed |= SetIfDifferent(Math.Max(256, settings.LlamaContextSize), settings.LlamaContextSize, value => settings.LlamaContextSize = value);
        changed |= SetIfDifferent(Math.Max(0, settings.LlamaGpuLayers), settings.LlamaGpuLayers, value => settings.LlamaGpuLayers = value);
        changed |= SetIfDifferent(Math.Max(1, settings.LlamaThreads), settings.LlamaThreads, value => settings.LlamaThreads = value);
        changed |= SetIfDifferent(Math.Max(1, settings.LlamaParallel), settings.LlamaParallel, value => settings.LlamaParallel = value);
        changed |= SetIfDifferent(Math.Max(1, settings.LlamaBatchSize), settings.LlamaBatchSize, value => settings.LlamaBatchSize = value);
        changed |= SetIfDifferent(Math.Max(1, settings.LlamaMaxTokens), settings.LlamaMaxTokens, value => settings.LlamaMaxTokens = value);
        changed |= SetIfDifferent(Math.Clamp(settings.LlamaTemperature, 0.0, 2.0), settings.LlamaTemperature, value => settings.LlamaTemperature = value);
        changed |= SetIfDifferent(Math.Clamp(settings.LlamaTopP, 0.0, 1.0), settings.LlamaTopP, value => settings.LlamaTopP = value);
        changed |= SetIfDifferent(Math.Max(0, settings.LlamaTopK), settings.LlamaTopK, value => settings.LlamaTopK = value);
        changed |= SetIfDifferent(Math.Clamp(settings.LlamaRepeatPenalty, 0.5, 2.0), settings.LlamaRepeatPenalty, value => settings.LlamaRepeatPenalty = value);
        changed |= SetIfDifferent(
            string.IsNullOrWhiteSpace(settings.LlamaGrpcHost) ? "127.0.0.1" : settings.LlamaGrpcHost.Trim(),
            settings.LlamaGrpcHost,
            value => settings.LlamaGrpcHost = value);
        changed |= SetIfDifferent(
            settings.LlamaGrpcPort <= 0 ? 50071 : settings.LlamaGrpcPort,
            settings.LlamaGrpcPort,
            value => settings.LlamaGrpcPort = value);
        var modelFileName = NormalizeLlamaModelFileName(settings.LlamaSelectedModelFileName);
        changed |= SetIfDifferent(modelFileName, settings.LlamaSelectedModelFileName, value => settings.LlamaSelectedModelFileName = value);
        return changed;
    }

    public static string NormalizeLlamaModelFileName(string? value)
    {
        var fileName = Path.GetFileName((value ?? string.Empty).Trim());
        return string.IsNullOrWhiteSpace(fileName) ||
               !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? DefaultLlamaModelFileName
            : fileName;
    }

    private static bool SetIfDifferent<T>(T next, T current, Action<T> assign)
        where T : notnull
    {
        if (Equals(next, current))
        {
            return false;
        }

        assign(next);
        return true;
    }
}

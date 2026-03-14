using System;
using System.IO;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings;

internal static class SettingsHostNormalizer
{
    private const string DefaultLlamaModelFileName = "HY-MT1.5-1.8B-Q8_0.gguf";
    private const string DefaultVisionLlmModelFileName = "Qwen3.5-4B-Q4_K_M.gguf";
    private const string DefaultVisionLlmMmprojFileName = "mmproj-Qwen3.5-4B-BF16.gguf";
    private static readonly string[] LegacyDefaultVisionLlmMmprojFileNames =
    {
        "4Bmmproj-F16.gguf",
        "mmproj-F16.gguf",
        "mmproj-BF16.gguf"
    };

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

    public static bool NormalizeVisionLlmSettings(AppSettings settings)
    {
        var changed = false;
        changed |= SetIfDifferent(
            string.IsNullOrWhiteSpace(settings.VisionLlmHost) ? "127.0.0.1" : settings.VisionLlmHost.Trim(),
            settings.VisionLlmHost,
            value => settings.VisionLlmHost = value);
        changed |= SetIfDifferent(
            settings.VisionLlmPort <= 0 ? 8089 : settings.VisionLlmPort,
            settings.VisionLlmPort,
            value => settings.VisionLlmPort = value);
        changed |= SetIfDifferent(Math.Max(256, settings.VisionLlmContextSize), settings.VisionLlmContextSize, value => settings.VisionLlmContextSize = value);
        changed |= SetIfDifferent(Math.Max(0, settings.VisionLlmGpuLayers), settings.VisionLlmGpuLayers, value => settings.VisionLlmGpuLayers = value);
        changed |= SetIfDifferent(Math.Max(1, settings.VisionLlmThreads), settings.VisionLlmThreads, value => settings.VisionLlmThreads = value);
        changed |= SetIfDifferent(Math.Max(1, settings.VisionLlmParallel), settings.VisionLlmParallel, value => settings.VisionLlmParallel = value);
        changed |= SetIfDifferent(Math.Max(1, settings.VisionLlmBatchSize), settings.VisionLlmBatchSize, value => settings.VisionLlmBatchSize = value);
        changed |= SetIfDifferent(Math.Max(1, settings.VisionLlmMaxTokens), settings.VisionLlmMaxTokens, value => settings.VisionLlmMaxTokens = value);
        changed |= SetIfDifferent(Math.Max(256, settings.VisionLlmMaxImageSide), settings.VisionLlmMaxImageSide, value => settings.VisionLlmMaxImageSide = value);
        changed |= SetIfDifferent(
            string.IsNullOrWhiteSpace(settings.VisionLlmGrpcHost) ? "127.0.0.1" : settings.VisionLlmGrpcHost.Trim(),
            settings.VisionLlmGrpcHost,
            value => settings.VisionLlmGrpcHost = value);
        changed |= SetIfDifferent(
            settings.VisionLlmGrpcPort <= 0 ? 50074 : settings.VisionLlmGrpcPort,
            settings.VisionLlmGrpcPort,
            value => settings.VisionLlmGrpcPort = value);
        changed |= SetIfDifferent(
            Math.Clamp(settings.VisionLlmGrpcReadyTimeoutMs, 1000, 600000),
            settings.VisionLlmGrpcReadyTimeoutMs,
            value => settings.VisionLlmGrpcReadyTimeoutMs = value);
        changed |= SetIfDifferent(
            Math.Clamp(settings.VisionLlmGrpcRestartMax, 1, 20),
            settings.VisionLlmGrpcRestartMax,
            value => settings.VisionLlmGrpcRestartMax = value);
        changed |= SetIfDifferent(
            Math.Clamp(settings.VisionLlmGrpcRestartWindowSeconds, 1, 300),
            settings.VisionLlmGrpcRestartWindowSeconds,
            value => settings.VisionLlmGrpcRestartWindowSeconds = value);
        changed |= SetIfDifferent(
            $"http://{settings.VisionLlmGrpcHost}:{settings.VisionLlmGrpcPort}",
            string.IsNullOrWhiteSpace(settings.VisionLlmGrpcEndpoint) ? string.Empty : settings.VisionLlmGrpcEndpoint.Trim(),
            value => settings.VisionLlmGrpcEndpoint = value);
        changed |= SetIfDifferent(
            NormalizeVisionLlmModelFileName(settings.VisionLlmSelectedModelFileName),
            settings.VisionLlmSelectedModelFileName,
            value => settings.VisionLlmSelectedModelFileName = value);
        changed |= SetIfDifferent(
            NormalizeVisionLlmMmprojFileName(settings.VisionLlmSelectedMmprojFileName),
            settings.VisionLlmSelectedMmprojFileName,
            value => settings.VisionLlmSelectedMmprojFileName = value);
        var hybridBaseEngine = Enum.IsDefined(typeof(VisionGeometryHybridBaseEngineKind), settings.VisionGeometryHybridBaseEngine)
            ? settings.VisionGeometryHybridBaseEngine
            : VisionGeometryHybridBaseEngineKind.WinRt;
        changed |= SetIfDifferent(
            hybridBaseEngine,
            settings.VisionGeometryHybridBaseEngine,
            value => settings.VisionGeometryHybridBaseEngine = value);
        changed |= SetIfDifferent(
            Math.Clamp(settings.VisionGeometryMatchMinScore, 0.0, 1.0),
            settings.VisionGeometryMatchMinScore,
            value => settings.VisionGeometryMatchMinScore = value);
        return changed;
    }

    public static string NormalizeVisionLlmModelFileName(string? value)
    {
        var fileName = Path.GetFileName((value ?? string.Empty).Trim());
        return string.IsNullOrWhiteSpace(fileName) ||
               !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? DefaultVisionLlmModelFileName
            : fileName;
    }

    public static string NormalizeVisionLlmMmprojFileName(string? value)
    {
        var fileName = Path.GetFileName((value ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultVisionLlmMmprojFileName;
        }

        // COMPAT: Older defaults used generic mmproj names that collide between 4B and 9B on Hugging Face.
        // Rewrite them to the model-specific local alias so existing settings migrate onto the new layout.
        return Array.Exists(
                LegacyDefaultVisionLlmMmprojFileNames,
                legacyName => string.Equals(legacyName, fileName, StringComparison.OrdinalIgnoreCase))
            ? DefaultVisionLlmMmprojFileName
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

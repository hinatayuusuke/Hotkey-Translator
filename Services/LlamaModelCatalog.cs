using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hotkey_Translator.Services;

public sealed class LlamaModelCatalog
{
    private const string DefaultProjectDirectory = "TranslationServiceLlama";
    private const string ModelsRelativePath = "LlamaCpp\\Models";

    public IReadOnlyList<string> GetAvailableModelFileNames(string? projectDirSetting)
    {
        var modelsDirectory = ResolveModelsDirectory(projectDirSetting);
        if (!Directory.Exists(modelsDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(modelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
    }

    public string NormalizeModelFileName(string? modelFileName, string fallbackFileName)
    {
        var fallback = ExtractSafeModelFileName(fallbackFileName);
        var normalized = ExtractSafeModelFileName(modelFileName);
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    public string ResolveModelPath(string? projectDirSetting, string modelFileName)
    {
        var modelsDirectory = ResolveModelsDirectory(projectDirSetting);
        var safeFileName = ExtractSafeModelFileName(modelFileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            throw new InvalidOperationException("Model file name is invalid.");
        }

        return Path.Combine(modelsDirectory, safeFileName);
    }

    private static string ResolveModelsDirectory(string? projectDirSetting)
    {
        var projectDir = string.IsNullOrWhiteSpace(projectDirSetting)
            ? DefaultProjectDirectory
            : projectDirSetting.Trim();
        return Path.Combine(ResolvePath(projectDir), ModelsRelativePath);
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var baseCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (Directory.Exists(baseCandidate) || File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private static string ExtractSafeModelFileName(string? value)
    {
        // SECURITY: Accept only bare .gguf file names to keep model resolution under the fixed Models directory.
        var fileName = Path.GetFileName((value ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        return fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? fileName : string.Empty;
    }
}

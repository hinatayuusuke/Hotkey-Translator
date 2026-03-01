using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hotkey_Translator.Services;

public sealed class LlamaModelCatalog
{
    private const string FixedProjectDirectory = "TranslationServiceLlama";
    private const string ModelsRelativePath = "LlamaCpp\\Models";

    public IReadOnlyList<string> GetAvailableModelFileNames()
    {
        var modelsDirectory = ResolveModelsDirectory();
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

    public string ResolveModelPath(string modelFileName)
    {
        var modelsDirectory = ResolveModelsDirectory();
        var safeFileName = ExtractSafeModelFileName(modelFileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            throw new InvalidOperationException("Model file name is invalid.");
        }

        return Path.Combine(modelsDirectory, safeFileName);
    }

    private static string ResolveModelsDirectory()
    {
        return Path.Combine(ResolvePath(FixedProjectDirectory), ModelsRelativePath);
    }

    private static string ResolvePath(string path)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
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

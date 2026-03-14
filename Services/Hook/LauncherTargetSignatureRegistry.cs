using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Hook;

internal sealed class LauncherTargetSignatureRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _registryPath;

    public LauncherTargetSignatureRegistry()
    {
        _registryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "Data",
            "GraphicsHookLauncherSignatures.json"));
    }

    public async Task<GraphicsHookLauncherTargetSignature?> TryLoadAsync(
        string expectedExeName,
        string? expectedExePath,
        CancellationToken cancellationToken)
    {
        var entries = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return null;
        }

        var normalizedExeName = Normalize(expectedExeName);
        var normalizedExePath = NormalizePath(expectedExePath);
        GraphicsHookLauncherTargetSignature? fallback = null;

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(normalizedExePath) &&
                !string.IsNullOrWhiteSpace(entry.ExePathSuffix) &&
                normalizedExePath.EndsWith(NormalizePath(entry.ExePathSuffix), StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }

            if (string.Equals(Normalize(entry.ExeName), normalizedExeName, StringComparison.OrdinalIgnoreCase))
            {
                fallback ??= entry;
            }
        }

        return fallback;
    }

    public async Task SaveOrUpdateAsync(
        GraphicsHookLauncherTargetSignature signature,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signature);

        var entries = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var normalizedExeName = Normalize(signature.ExeName);
        var normalizedExePathSuffix = NormalizePath(signature.ExePathSuffix);

        var existingIndex = entries.FindIndex(entry =>
            (!string.IsNullOrWhiteSpace(normalizedExePathSuffix) &&
             !string.IsNullOrWhiteSpace(entry.ExePathSuffix) &&
             string.Equals(NormalizePath(entry.ExePathSuffix), normalizedExePathSuffix, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Normalize(entry.ExeName), normalizedExeName, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            entries[existingIndex] = signature;
        }
        else
        {
            entries.Add(signature);
        }

        var directory = Path.GetDirectoryName(_registryPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(entries, JsonOptions);
        var tempPath = $"{_registryPath}.tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _registryPath, true);
    }

    private async Task<List<GraphicsHookLauncherTargetSignature>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_registryPath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(_registryPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<GraphicsHookLauncherTargetSignature>>(json, JsonOptions) ?? [];
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizePath(string? value)
    {
        return Normalize(value).Replace('/', '\\');
    }
}

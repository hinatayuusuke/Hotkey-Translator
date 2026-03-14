using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Hotkey_Translator.Services;

internal sealed record ModelAssetDescriptor(
    string LocalFileName,
    string DownloadUrl,
    string Sha256,
    long? SizeBytes);

internal static class ModelAssetProvisioner
{
    private static readonly HttpClient DownloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task EnsureAssetAsync(
        ModelAssetDescriptor asset,
        string assetPath,
        string assetTag,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var safeLocalFileName = ValidateAssetDescriptor(asset, assetPath);
        if (TryValidateAsset(assetPath, asset, out _))
        {
            log?.Invoke(
                $"stage=model_asset_download event=skip asset={assetTag} local_filename={safeLocalFileName} reason=already_ready.");
            return;
        }

        var assetDirectory = Path.GetDirectoryName(assetPath) ?? throw new InvalidOperationException("Asset directory is invalid.");
        Directory.CreateDirectory(assetDirectory);
        var lockPath = $"{assetPath}.lock";
        await using var lockHandle = await AcquireExclusiveLockAsync(lockPath, cancellationToken).ConfigureAwait(false);

        // WHY: Another process may complete the download while we waited on the lock.
        if (TryValidateAsset(assetPath, asset, out _))
        {
            log?.Invoke(
                $"stage=model_asset_download event=skip asset={assetTag} local_filename={safeLocalFileName} reason=became_ready_while_waiting_lock.");
            return;
        }

        var tempPath = $"{assetPath}.tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        var stopwatch = Stopwatch.StartNew();
        log?.Invoke(
            $"stage=model_asset_download event=start asset={assetTag} local_filename={safeLocalFileName} size_bytes={asset.SizeBytes?.ToString() ?? "unknown"} url={asset.DownloadUrl}.");

        try
        {
            using var response = await DownloadClient.GetAsync(
                asset.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (!TryValidateAsset(tempPath, asset, out var reason))
            {
                File.Delete(tempPath);
                throw new InvalidDataException(
                    $"Downloaded asset validation failed for '{safeLocalFileName}': {reason}");
            }

            File.Move(tempPath, assetPath, true);
            stopwatch.Stop();
            log?.Invoke(
                $"stage=model_asset_download event=complete asset={assetTag} local_filename={safeLocalFileName} elapsed_ms={stopwatch.ElapsedMilliseconds}.");
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public static void EnsureExistingNonEmptyFile(string assetPath, string assetLabel)
    {
        var fileInfo = new FileInfo(assetPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException($"{assetLabel} was not found: {assetPath}");
        }

        if (fileInfo.Length <= 0)
        {
            throw new InvalidDataException($"{assetLabel} is empty: {assetPath}");
        }
    }

    public static bool TryValidateAsset(string assetPath, ModelAssetDescriptor asset, out string reason)
    {
        reason = string.Empty;
        if (!File.Exists(assetPath))
        {
            reason = "missing file";
            return false;
        }

        var info = new FileInfo(assetPath);
        if (info.Length <= 0)
        {
            reason = "empty file";
            return false;
        }

        if (asset.SizeBytes is > 0 && info.Length != asset.SizeBytes.Value)
        {
            reason = $"size mismatch (expected {asset.SizeBytes.Value}, actual {info.Length})";
            return false;
        }

        var actualSha = ComputeFileSha256(assetPath);
        if (!string.Equals(actualSha, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"sha256 mismatch (expected {asset.Sha256}, actual {actualSha})";
            return false;
        }

        return true;
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ValidateAssetDescriptor(ModelAssetDescriptor asset, string assetPath)
    {
        var safeLocalFileName = Path.GetFileName((asset.LocalFileName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(safeLocalFileName) ||
            !safeLocalFileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Asset manifest local filename is invalid: '{asset.LocalFileName}'.");
        }

        var actualFileName = Path.GetFileName(assetPath);
        if (!string.Equals(safeLocalFileName, actualFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Asset manifest local filename mismatch. Expected '{actualFileName}', got '{safeLocalFileName}'.");
        }

        if (string.IsNullOrWhiteSpace(asset.DownloadUrl) || string.IsNullOrWhiteSpace(asset.Sha256))
        {
            throw new InvalidDataException($"Asset manifest is missing required download fields for '{safeLocalFileName}'.");
        }

        return safeLocalFileName;
    }

    private static async Task<FileStream> AcquireExclusiveLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Timed out waiting for lock: {lockPath}");
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failures and let the original exception surface.
        }
    }
}

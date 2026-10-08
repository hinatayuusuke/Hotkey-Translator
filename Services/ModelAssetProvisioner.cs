using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Services.GrpcHost;

namespace Hotkey_Translator.Services;

internal sealed record ModelAssetDescriptor(
    string LocalFileName,
    string DownloadUrl,
    string Sha256,
    long? SizeBytes);

internal sealed record ModelAssetTimeouts(TimeSpan Headers, TimeSpan Idle, TimeSpan Total, TimeSpan Lock)
{
    // WHY: Large models need a long transfer budget, but an idle connection must not hold the UI indefinitely.
    public static ModelAssetTimeouts Default { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60),
        TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));
}

internal static class ModelAssetProvisioner
{
    private static readonly HttpClient DownloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task EnsureAssetAsync(
        ModelAssetDescriptor asset, string assetPath, string assetTag, Action<string>? log,
        CancellationToken cancellationToken, Action<HostLoadProgress>? progress = null,
        HttpClient? client = null, ModelAssetTimeouts? timeouts = null)
    {
        var safeLocalFileName = ValidateAssetDescriptor(asset, assetPath);
        timeouts ??= ModelAssetTimeouts.Default;
        progress?.Invoke(new(assetTag, HostLoadPhase.Validation, safeLocalFileName));
        if ((await ValidateAssetAsync(assetPath, asset, cancellationToken).ConfigureAwait(false)).Valid) return;

        Directory.CreateDirectory(Path.GetDirectoryName(assetPath) ?? throw new InvalidOperationException("Asset directory is invalid."));
        progress?.Invoke(new(assetTag, HostLoadPhase.Lock, safeLocalFileName));
        await using var lockHandle = await AcquireExclusiveLockAsync(assetPath + ".lock", cancellationToken, timeouts.Lock).ConfigureAwait(false);
        progress?.Invoke(new(assetTag, HostLoadPhase.Validation, safeLocalFileName));
        if ((await ValidateAssetAsync(assetPath, asset, cancellationToken).ConfigureAwait(false)).Valid) return;

        var tempPath = assetPath + ".tmp";
        // SECURITY: Only the lock owner may remove the unfinished asset; never delete the final model on cancellation.
        TryDeleteFile(tempPath);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(timeouts.Total);
        var phase = "download response";
        var elapsed = Stopwatch.StartNew();
        progress?.Invoke(new(assetTag, HostLoadPhase.Download, safeLocalFileName));
        log?.Invoke($"stage=model_asset_download event=start asset={assetTag} local_filename={safeLocalFileName}.");
        try
        {
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            headers.CancelAfter(timeouts.Headers);
            using var response = await (client ?? DownloadClient).GetAsync(asset.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            headers.CancelAfter(Timeout.InfiniteTimeSpan);
            response.EnsureSuccessStatusCode();
            phase = "download reception";
            var size = response.Content.Headers.ContentLength ?? asset.SizeBytes;
            long received = 0;
            var report = Stopwatch.StartNew();
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            await using (var input = await response.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                    idle.CancelAfter(timeouts.Idle);
                    var count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), total.Token).ConfigureAwait(false);
                    received += count;
                    if (report.ElapsedMilliseconds >= 250)
                    {
                        progress?.Invoke(new(assetTag, HostLoadPhase.Download, safeLocalFileName, received, size));
                        report.Restart();
                    }
                }
            }
            phase = "download validation";
            progress?.Invoke(new(assetTag, HostLoadPhase.Validation, safeLocalFileName));
            var validation = await ValidateAssetAsync(tempPath, asset, total.Token).ConfigureAwait(false);
            if (!validation.Valid) throw new InvalidDataException($"Asset validation failed for '{safeLocalFileName}': {validation.Reason}");
            total.Token.ThrowIfCancellationRequested();
            File.Move(tempPath, assetPath, true);
            log?.Invoke($"stage=model_asset_download event=complete asset={assetTag} elapsed_ms={elapsed.ElapsedMilliseconds}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDeleteFile(tempPath);
            throw new TimeoutException($"Timed out during {phase}: {safeLocalFileName}.");
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

    internal static Task<(bool Valid, string Reason)> ValidateAssetAsync(string path, ModelAssetDescriptor asset,
        CancellationToken cancellationToken)
    {
        // WHY: Cached file reads can complete synchronously; hashing large models must still stay off the UI thread.
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists) return (false, "missing file");
            if (info.Length <= 0) return (false, "empty file");
            if (asset.SizeBytes is > 0 && asset.SizeBytes != info.Length) return (false, "size mismatch");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(hash), asset.Sha256, StringComparison.OrdinalIgnoreCase)
                ? (true, "") : (false, "sha256 mismatch");
        }, cancellationToken);
    }

    private static async Task<FileStream> AcquireExclusiveLockAsync(string lockPath, CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (elapsed.Elapsed >= timeout)
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

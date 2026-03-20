using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

internal sealed class OneOcrProcessHost : IDisposable
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly AppLogger? _logger;
    private OneOcrProtocolClient? _client;
    private Process? _process;
    private int _startSequence;
    private bool _disposed;

    public OneOcrProcessHost(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<OneOcrRecognizeResponse> RecognizeAsync(byte[] imageBytes, AppSettings settings, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await RecognizeCoreAsync(imageBytes, settings, allowRestart: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcess();
        _sync.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<OneOcrRecognizeResponse> RecognizeCoreAsync(
        byte[] imageBytes,
        AppSettings settings,
        bool allowRestart,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await EnsureStartedAsync(settings, cancellationToken).ConfigureAwait(false);
            var response = await client.SendAsync<OneOcrRecognizeResponse>(
                new OneOcrRequestMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = "recognize",
                    ImageFormat = "png",
                    ImageBytesBase64 = Convert.ToBase64String(imageBytes),
                    MaxLineCount = settings.OneOcrMaxLineCount
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.Ok)
            {
                throw new InvalidOperationException($"OneOCR helper failed: {response.Error ?? "unknown error"}");
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (allowRestart)
        {
            _logger?.Error(ex, "OneOCR helper request failed; restarting helper once.");
            RestartProcess();
            return await RecognizeCoreAsync(imageBytes, settings, allowRestart: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<OneOcrProtocolClient> EnsureStartedAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true } && _process is { HasExited: false })
        {
            return _client;
        }

        RestartProcess();

        if (!settings.EnableOneOcrHelper)
        {
            throw new InvalidOperationException("OneOCR helper is disabled in settings.");
        }

        var helperPath = ResolveExistingPath(settings.OneOcrHelperRelativePath);
        var vendorPath = ResolveExistingPath(settings.OneOcrVendorRelativePath);
        var pipeName = $"{settings.OneOcrPipeName}_{Environment.ProcessId}_{Interlocked.Increment(ref _startSequence)}";
        var readyTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, settings.OneOcrReadyTimeoutMs));

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            WorkingDirectory = Path.GetDirectoryName(helperPath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--vendor-dir");
        startInfo.ArgumentList.Add(vendorPath);
        startInfo.ArgumentList.Add("--max-line-count");
        startInfo.ArgumentList.Add(settings.OneOcrMaxLineCount.ToString());

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start OneOCR helper process.");
        _process.OutputDataReceived += OnHelperOutputDataReceived;
        _process.ErrorDataReceived += OnHelperErrorDataReceived;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var client = new OneOcrProtocolClient();
        try
        {
            await client.ConnectAsync(pipeName, readyTimeout, cancellationToken).ConfigureAwait(false);
            var ready = await client.ReadAsync<OneOcrReadyResponse>(cancellationToken).ConfigureAwait(false);
            if (!ready.Ok || !string.Equals(ready.Type, "ready", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"OneOCR helper did not become ready: {ready.Error ?? ready.Message ?? "unknown state"}");
            }
        }
        catch
        {
            client.Dispose();
            StopProcess();
            throw;
        }

        _client = client;
        _logger?.Info($"stage=oneocr event=helper_started pid={_process.Id} path=\"{helperPath}\" pipe={pipeName}.");
        return client;
    }

    private void RestartProcess()
    {
        StopProcess();
    }

    private void StopProcess()
    {
        try
        {
            _client?.Dispose();
        }
        finally
        {
            _client = null;
        }

        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1500);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to stop OneOCR helper process cleanly.");
        }
        finally
        {
            try
            {
                _process.Dispose();
            }
            finally
            {
                _process = null;
            }
        }
    }

    private void OnHelperOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            _logger?.Info($"stage=oneocr event=helper_stdout message=\"{e.Data}\".");
        }
    }

    private void OnHelperErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            _logger?.Info($"stage=oneocr event=helper_stderr message=\"{e.Data}\".");
        }
    }

    private static string ResolveExistingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("OneOCR helper path is not configured.");
        }

        if (Path.IsPathRooted(path))
        {
            return EnsureExists(path);
        }

        // WHY: AppContext.BaseDirectory is correct for packaged builds, but repo-local runs resolve from the current working directory.
        var baseCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (File.Exists(baseCandidate) || Directory.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        var currentCandidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        return EnsureExists(currentCandidate);
    }

    private static string EnsureExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return path;
        }

        throw new FileNotFoundException($"OneOCR helper path was not found: {path}");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

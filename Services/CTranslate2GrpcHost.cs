using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.TranslationGrpc;

namespace Hotkey_Translator.Services;

public sealed class CTranslate2GrpcHost : IDisposable
{
    private readonly AppLogger? _logger;
    private readonly object _lock = new();
    private readonly List<DateTimeOffset> _restartHistory = new();
    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _stopping;

    public CTranslate2GrpcHost(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.EnableCTranslate2)
        {
            return;
        }

        // WHY: gRPC over localhost uses HTTP/2 without TLS by default.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        lock (_lock)
        {
            if (_process is { HasExited: false })
            {
                return;
            }
        }

        StartProcess(settings);
        await WaitForReadyAsync(settings, cancellationToken).ConfigureAwait(false);
        EnsureMonitor(settings);
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stopping = true;
        }

        _monitorCts?.Cancel();
        try
        {
            _monitorTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown wait failures.
        }

        lock (_lock)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(true);
                }
            }
            catch
            {
                // Ignore kill failures during shutdown.
            }

            _process?.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _monitorCts?.Dispose();
    }

    private void StartProcess(AppSettings settings)
    {
        var projectDir = ResolveDirectory(settings.CTranslate2GrpcProjectDir);
        var script = string.IsNullOrWhiteSpace(settings.CTranslate2GrpcServerScript) ? "server.py" : settings.CTranslate2GrpcServerScript.Trim();
        var scriptPath = Path.Combine(projectDir, script);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"CTranslate2 gRPC server not found: {scriptPath}");
        }

        var uvPath = string.IsNullOrWhiteSpace(settings.CTranslate2GrpcUvPath) ? "uv" : settings.CTranslate2GrpcUvPath.Trim();
        var host = string.IsNullOrWhiteSpace(settings.CTranslate2GrpcHost) ? "127.0.0.1" : settings.CTranslate2GrpcHost.Trim();
        var port = settings.CTranslate2GrpcPort <= 0 ? 50061 : settings.CTranslate2GrpcPort;
        var modelId = string.IsNullOrWhiteSpace(settings.CTranslate2ModelId)
            ? "entai2965/nllb-200-distilled-600M-ctranslate2"
            : settings.CTranslate2ModelId.Trim();
        var modelDir = string.IsNullOrWhiteSpace(settings.CTranslate2ModelDir) ? null : ResolvePath(settings.CTranslate2ModelDir);
        var device = string.IsNullOrWhiteSpace(settings.CTranslate2Device) ? "cpu" : settings.CTranslate2Device.Trim();
        var precision = string.IsNullOrWhiteSpace(settings.CTranslate2Precision) ? "int8" : settings.CTranslate2Precision.Trim();

        var startInfo = new ProcessStartInfo
        {
            FileName = uvPath,
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectDir);
        startInfo.ArgumentList.Add("python");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--model-id");
        startInfo.ArgumentList.Add(modelId);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--precision");
        startInfo.ArgumentList.Add(precision);
        if (!string.IsNullOrWhiteSpace(modelDir))
        {
            startInfo.ArgumentList.Add("--model-dir");
            startInfo.ArgumentList.Add(modelDir);
        }
        if (settings.EnableCTranslate2AutoDownload)
        {
            startInfo.ArgumentList.Add("--auto-download");
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger?.Info($"[CT2Grpc] {args.Data}");
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger?.Info($"[CT2Grpc] {args.Data}");
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start CTranslate2 gRPC server process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_lock)
        {
            _process?.Dispose();
            _process = process;
            _stopping = false;
        }
    }

    private async Task WaitForReadyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(settings);
        var timeoutMs = Math.Max(1000, settings.CTranslate2GrpcReadyTimeoutMs);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var channel = GrpcChannel.ForAddress(endpoint);
                var client = new TranslationService.TranslationServiceClient(channel);
                var reply = await client.HealthAsync(new HealthRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (reply.Ready)
                {
                    _logger?.Info($"CTranslate2 gRPC ready: {reply.Message}");
                    return;
                }
            }
            catch
            {
                // NOTE: Keep retrying until timeout; server may still be warming up.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("CTranslate2 gRPC server did not become ready in time.");
    }

    private void EnsureMonitor(AppSettings settings)
    {
        if (_monitorCts != null)
        {
            return;
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(settings, _monitorCts.Token));
    }

    private async Task MonitorLoopAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process;
            lock (_lock)
            {
                process = _process;
            }

            if (process == null)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            lock (_lock)
            {
                if (_stopping)
                {
                    return;
                }
            }

            _logger?.Info($"CTranslate2 gRPC exited with code {process.ExitCode}.");
            if (!CanRestart(settings))
            {
                _logger?.Info("CTranslate2 gRPC restart limit reached.");
                return;
            }

            try
            {
                StartProcess(settings);
                await WaitForReadyAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Info($"CTranslate2 gRPC restart failed: {ex.Message}");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool CanRestart(AppSettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromSeconds(Math.Max(1, settings.CTranslate2GrpcRestartWindowSeconds));
        _restartHistory.RemoveAll(time => now - time > window);
        if (_restartHistory.Count >= settings.CTranslate2GrpcRestartMax)
        {
            return false;
        }

        _restartHistory.Add(now);
        return true;
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CTranslate2GrpcEndpoint))
        {
            return settings.CTranslate2GrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.CTranslate2GrpcHost) ? "127.0.0.1" : settings.CTranslate2GrpcHost.Trim();
        var port = settings.CTranslate2GrpcPort <= 0 ? 50061 : settings.CTranslate2GrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("CTranslate2 gRPC project directory is not set.");
        }

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"CTranslate2 gRPC project directory not found: {resolved}");
        }

        return resolved;
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
}

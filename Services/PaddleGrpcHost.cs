using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.OcrGrpc;

namespace Hotkey_Translator.Services;

public sealed class PaddleGrpcHost : IDisposable
{
    private readonly AppLogger? _logger;
    private readonly object _lock = new();
    private readonly List<DateTimeOffset> _restartHistory = new();
    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _stopping;

    public PaddleGrpcHost(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.EnablePaddleGrpcHost)
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
        var projectDir = ResolveDirectory(settings.PaddleGrpcProjectDir);
        var script = string.IsNullOrWhiteSpace(settings.PaddleGrpcServerScript) ? "server.py" : settings.PaddleGrpcServerScript.Trim();
        var scriptPath = Path.Combine(projectDir, script);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Paddle gRPC server not found: {scriptPath}");
        }

        var uvPath = string.IsNullOrWhiteSpace(settings.PaddleGrpcUvPath) ? "uv" : settings.PaddleGrpcUvPath.Trim();
        var host = string.IsNullOrWhiteSpace(settings.PaddleGrpcHost) ? "127.0.0.1" : settings.PaddleGrpcHost.Trim();
        var port = settings.PaddleGrpcPort <= 0 ? 50051 : settings.PaddleGrpcPort;
        var modelDir = string.IsNullOrWhiteSpace(settings.PaddleModelDir) ? null : ResolvePath(settings.PaddleModelDir);
        var device = string.IsNullOrWhiteSpace(settings.PaddleDevice) ? "cpu" : settings.PaddleDevice.Trim();
        if (string.Equals(device, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            // WHY: PaddleOCR v5 GPU-only path is enforced; override cpu to gpu:0 for host startup.
            device = "gpu:0";
        }
        var language = ResolvePaddleLanguage(settings);
        var detModel = string.IsNullOrWhiteSpace(settings.PaddleTextDetectionModelName)
            ? "PP-OCRv5_mobile_det"
            : settings.PaddleTextDetectionModelName.Trim();
        var recModel = string.IsNullOrWhiteSpace(settings.PaddleTextRecognitionModelName)
            ? "PP-OCRv5_server_rec"
            : settings.PaddleTextRecognitionModelName.Trim();

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
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--lang");
        startInfo.ArgumentList.Add(language);
        startInfo.ArgumentList.Add("--det-model");
        startInfo.ArgumentList.Add(detModel);
        startInfo.ArgumentList.Add("--rec-model");
        startInfo.ArgumentList.Add(recModel);
        if (!string.IsNullOrWhiteSpace(modelDir))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(modelDir);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger?.Info($"[PaddleGrpc] {args.Data}");
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger?.Info($"[PaddleGrpc] {args.Data}");
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Paddle gRPC server process.");
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
        var timeoutMs = Math.Max(1000, settings.PaddleGrpcReadyTimeoutMs);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var channel = GrpcChannel.ForAddress(endpoint);
                var client = new OcrService.OcrServiceClient(channel);
                var reply = await client.HealthAsync(new HealthRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (reply.Ready)
                {
                    _logger?.Info($"Paddle gRPC ready: {reply.Message}");
                    return;
                }
            }
            catch
            {
                // NOTE: Keep retrying until timeout; server may still be warming up.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Paddle gRPC server did not become ready in time.");
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

            _logger?.Info($"Paddle gRPC exited with code {process.ExitCode}.");
            if (!CanRestart(settings))
            {
                _logger?.Info("Paddle gRPC restart limit reached.");
                return;
            }

            try
            {
                StartProcess(settings);
                await WaitForReadyAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Info($"Paddle gRPC restart failed: {ex.Message}");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool CanRestart(AppSettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromSeconds(Math.Max(1, settings.PaddleGrpcRestartWindowSeconds));
        _restartHistory.RemoveAll(time => now - time > window);
        if (_restartHistory.Count >= settings.PaddleGrpcRestartMax)
        {
            return false;
        }

        _restartHistory.Add(now);
        return true;
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PaddleGrpcEndpoint))
        {
            return settings.PaddleGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.PaddleGrpcHost) ? "127.0.0.1" : settings.PaddleGrpcHost.Trim();
        var port = settings.PaddleGrpcPort <= 0 ? 50051 : settings.PaddleGrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Paddle gRPC project directory is not set.");
        }

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Paddle gRPC project directory not found: {resolved}");
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

    private static string ResolvePaddleLanguage(AppSettings settings)
    {
        var source = settings.SourceLanguage?.Trim() ?? string.Empty;
        return source.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "japan" : "en";
    }
}

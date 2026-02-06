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

public sealed class LlamaGrpcHost : IDisposable
{
    private readonly AppLogger? _logger;
    private readonly object _lock = new();
    private readonly List<DateTimeOffset> _restartHistory = new();
    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _stopping;

    public LlamaGrpcHost(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.EnableLlamaCppTranslation)
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
        var projectDir = ResolveDirectory(settings.LlamaGrpcProjectDir);
        var script = string.IsNullOrWhiteSpace(settings.LlamaGrpcServerScript) ? "server.py" : settings.LlamaGrpcServerScript.Trim();
        var scriptPath = Path.Combine(projectDir, script);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Llama gRPC server not found: {scriptPath}");
        }

        var uvPath = string.IsNullOrWhiteSpace(settings.LlamaGrpcUvPath) ? "uv" : settings.LlamaGrpcUvPath.Trim();
        var host = string.IsNullOrWhiteSpace(settings.LlamaGrpcHost) ? "127.0.0.1" : settings.LlamaGrpcHost.Trim();
        var port = settings.LlamaGrpcPort <= 0 ? 50071 : settings.LlamaGrpcPort;
        var llamaServer = string.IsNullOrWhiteSpace(settings.LlamaServerPath) ? "llama-server" : settings.LlamaServerPath.Trim();
        var modelPath = string.IsNullOrWhiteSpace(settings.LlamaModelPath)
            ? "Models\\HY-MT1.5-1.8B-Q4_K_M.gguf"
            : settings.LlamaModelPath.Trim();

        _logger?.Info("Starting Llama gRPC (llama-server over HTTP).");

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
        startInfo.ArgumentList.Add("--llama-server");
        startInfo.ArgumentList.Add(llamaServer);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("--llama-host");
        startInfo.ArgumentList.Add(settings.LlamaHost);
        startInfo.ArgumentList.Add("--llama-port");
        startInfo.ArgumentList.Add(settings.LlamaPort.ToString());
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add(settings.LlamaContextSize.ToString());
        startInfo.ArgumentList.Add("--gpu-layers");
        startInfo.ArgumentList.Add(settings.LlamaGpuLayers.ToString());
        startInfo.ArgumentList.Add("--threads");
        startInfo.ArgumentList.Add(settings.LlamaThreads.ToString());
        startInfo.ArgumentList.Add("--parallel");
        startInfo.ArgumentList.Add(settings.LlamaParallel.ToString());
        startInfo.ArgumentList.Add("--batch-size");
        startInfo.ArgumentList.Add(settings.LlamaBatchSize.ToString());
        startInfo.ArgumentList.Add("--max-tokens");
        startInfo.ArgumentList.Add(settings.LlamaMaxTokens.ToString());
        startInfo.ArgumentList.Add("--temperature");
        startInfo.ArgumentList.Add(settings.LlamaTemperature.ToString("0.###"));
        startInfo.ArgumentList.Add("--top-p");
        startInfo.ArgumentList.Add(settings.LlamaTopP.ToString("0.###"));
        startInfo.ArgumentList.Add("--top-k");
        startInfo.ArgumentList.Add(settings.LlamaTopK.ToString());
        startInfo.ArgumentList.Add("--repeat-penalty");
        startInfo.ArgumentList.Add(settings.LlamaRepeatPenalty.ToString("0.###"));
        if (!string.IsNullOrWhiteSpace(settings.LlamaSystemPrompt))
        {
            startInfo.ArgumentList.Add("--system-prompt");
            startInfo.ArgumentList.Add(settings.LlamaSystemPrompt);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger?.Info($"[LlamaGrpc] {args.Data}");
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger?.Info($"[LlamaGrpc] {args.Data}");
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Llama gRPC server process.");
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
        var timeoutMs = Math.Max(1000, settings.LlamaGrpcReadyTimeoutMs);
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
                    _logger?.Info($"Llama gRPC ready: {reply.Message}");
                    return;
                }
            }
            catch
            {
                // NOTE: Keep retrying until timeout; server may still be warming up.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Llama gRPC server did not become ready in time.");
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

            _logger?.Info($"Llama gRPC exited with code {process.ExitCode}.");
            if (!CanRestart(settings))
            {
                _logger?.Info("Llama gRPC restart limit reached.");
                return;
            }

            try
            {
                StartProcess(settings);
                await WaitForReadyAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Info($"Llama gRPC restart failed: {ex.Message}");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool CanRestart(AppSettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromSeconds(Math.Max(1, settings.LlamaGrpcRestartWindowSeconds));
        _restartHistory.RemoveAll(time => now - time > window);
        if (_restartHistory.Count >= settings.LlamaGrpcRestartMax)
        {
            return false;
        }

        _restartHistory.Add(now);
        return true;
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LlamaGrpcEndpoint))
        {
            return settings.LlamaGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.LlamaGrpcHost) ? "127.0.0.1" : settings.LlamaGrpcHost.Trim();
        var port = settings.LlamaGrpcPort <= 0 ? 50071 : settings.LlamaGrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Llama gRPC project directory is not set.");
        }

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Llama gRPC project directory not found: {resolved}");
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

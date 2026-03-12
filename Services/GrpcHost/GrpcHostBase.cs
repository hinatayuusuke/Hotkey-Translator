using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.GrpcHost;

internal abstract class GrpcHostBase : IGrpcHostLifecycle, IDisposable
{
    private readonly object _sync = new();
    private readonly List<DateTimeOffset> _restartHistory = new();
    private readonly Func<AppLogger?> _loggerAccessor;
    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _stopping;

    protected GrpcHostBase(Func<AppLogger?>? loggerAccessor = null)
    {
        _loggerAccessor = loggerAccessor ?? (() => null);
    }

    protected AppLogger? Logger => _loggerAccessor();

    protected abstract string HostId { get; }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    protected bool TryGetProcessExitCode(out int exitCode)
    {
        lock (_sync)
        {
            if (_process is not { HasExited: true })
            {
                exitCode = 0;
                return false;
            }

            exitCode = _process.ExitCode;
            return true;
        }
    }

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!IsEnabled(settings))
        {
            return;
        }

        // WHY: gRPC over localhost uses HTTP/2 without TLS by default.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                return;
            }
        }

        Logger?.Info($"stage=grpc_host host={HostId} event=start.");
        try
        {
            await StartProcessAndProbeReadyAsync(settings, cancellationToken).ConfigureAwait(false);
            EnsureMonitor(settings);
            Logger?.Info($"stage=grpc_host host={HostId} event=ready.");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        Logger?.Info($"stage=grpc_host host={HostId} event=stop.");
        lock (_sync)
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

        _monitorTask = null;
        _monitorCts?.Dispose();
        _monitorCts = null;

        lock (_sync)
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

        OnAfterStop();
    }

    public void Dispose()
    {
        Stop();
    }

    protected Process StartProcessWithLogging(ProcessStartInfo startInfo, string outputTag)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            OnProcessOutputLine(args.Data, isError: false);
            Logger?.Info($"[{outputTag}] {args.Data}");
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            OnProcessOutputLine(args.Data, isError: true);
            Logger?.Info($"[{outputTag}] {args.Data}");
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {HostId} process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    protected virtual Task OnBeforeStartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected virtual void OnAfterStop()
    {
    }

    protected virtual void OnProcessOutputLine(string line, bool isError)
    {
    }

    protected abstract bool IsEnabled(AppSettings settings);

    protected abstract Task<Process> StartProcessCoreAsync(AppSettings settings, CancellationToken cancellationToken);

    protected abstract Task WaitForReadyCoreAsync(AppSettings settings, CancellationToken cancellationToken);

    protected abstract GrpcHostRestartPolicy GetRestartPolicy(AppSettings settings);

    private async Task StartProcessAndProbeReadyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await OnBeforeStartAsync(settings, cancellationToken).ConfigureAwait(false);
        var process = await StartProcessCoreAsync(settings, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _process?.Dispose();
            _process = process;
            _stopping = false;
        }

        await WaitForReadyCoreAsync(settings, cancellationToken).ConfigureAwait(false);
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
            lock (_sync)
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

            lock (_sync)
            {
                if (_stopping)
                {
                    return;
                }
            }

            Logger?.Info($"stage=grpc_host host={HostId} event=exit code={process.ExitCode}.");
            if (!CanRestart(settings))
            {
                Logger?.Info($"stage=grpc_host host={HostId} event=restart_limit_reached.");
                return;
            }

            try
            {
                Logger?.Info($"stage=grpc_host host={HostId} event=restart.");
                await StartProcessAndProbeReadyAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger?.Info($"stage=grpc_host host={HostId} event=restart_failed message={ex.Message}");
                var retryDelayMs = Math.Max(50, GetRestartPolicy(settings).RetryDelayMs);
                await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool CanRestart(AppSettings settings)
    {
        var policy = GetRestartPolicy(settings);
        var maxRestarts = Math.Max(0, policy.MaxRestarts);
        if (maxRestarts == 0)
        {
            return false;
        }

        var window = policy.Window <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : policy.Window;
        var now = DateTimeOffset.UtcNow;
        _restartHistory.RemoveAll(time => now - time > window);
        if (_restartHistory.Count >= maxRestarts)
        {
            return false;
        }

        _restartHistory.Add(now);
        return true;
    }
}

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;

namespace Hotkey_Translator.Services.GrpcHost;

internal static class GrpcReadyProbe
{
    public static async Task WaitAsync(int timeoutMs, Func<int?> getExitCode,
        Func<DateTime, CancellationToken, Task<bool>> health, CancellationToken cancellationToken)
    {
        timeoutMs = Math.Max(1000, timeoutMs);
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ready.CancelAfter(timeoutMs);
        using var monitor = CancellationTokenSource.CreateLinkedTokenSource(ready.Token);
        var exitWatcher = WatchExitAsync();
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (getExitCode() is int code) throw new HostProcessExitedException(code);
                ready.Token.ThrowIfCancellationRequested();
                var remaining = TimeSpan.FromMilliseconds(timeoutMs) - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"Ready timeout ({timeoutMs} ms).");
                using var rpc = CancellationTokenSource.CreateLinkedTokenSource(ready.Token);
                var duration = remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2);
                rpc.CancelAfter(duration);
                try
                {
                    // WHY: An RPC deadline alone does not enforce the entire startup budget or process exit.
                    if (await health(DateTime.UtcNow.Add(duration), rpc.Token).WaitAsync(rpc.Token).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (getExitCode() is int exitCode) throw new HostProcessExitedException(exitCode);
                        ready.Token.ThrowIfCancellationRequested();
                        return;
                    }
                }
                catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded) { }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && rpc.IsCancellationRequested) { }
                catch (OperationCanceledException) when (rpc.IsCancellationRequested) { }
                await Task.Delay(200, ready.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (getExitCode() is int code) throw new HostProcessExitedException(code);
            throw new TimeoutException($"Ready timeout ({timeoutMs} ms).");
        }
        finally
        {
            monitor.Cancel();
            await exitWatcher.ConfigureAwait(false);
        }

        async Task WatchExitAsync()
        {
            try
            {
                while (!monitor.IsCancellationRequested)
                {
                    if (getExitCode() != null) { ready.Cancel(); return; }
                    await Task.Delay(50, monitor.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (monitor.IsCancellationRequested) { }
        }
    }
}

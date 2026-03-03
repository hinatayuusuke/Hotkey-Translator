using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services.Hook.Contracts;

namespace Hotkey_Translator.Services.Hook;

internal sealed class GraphicsHookClientService : IDisposable
{
    private const string FixedHookHostRelativePath = "Native\\HookHost\\bin\\HookHost.exe";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly GraphicsHookOverlayV2CommandWriter _overlayV2Writer = new();
    private readonly GraphicsHookConfigWriter _configWriter = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _hostProcess;
    private string _lastPipeName = "hotkey_translator_hook";
    private int _attachedPid;
    private GraphicsHookApiKind _attachedApi = GraphicsHookApiKind.Dx11;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;

    public GraphicsHookClientService(Func<AppLogger?> loggerAccessor)
    {
        _loggerAccessor = loggerAccessor;
    }

    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (!settings.EnableGraphicsHookPipeline)
            {
                await DetachInternalAsync("disabled", cancellationToken).ConfigureAwait(false);
                return;
            }

            _lastPipeName = settings.GraphicsHookPipeName;

            if (_attachedPid > 0 &&
                _attachedPid == settings.FixedCaptureWindowProcessId &&
                _attachedApi != settings.GraphicsHookApi)
            {
                // WHY: Host keeps one injected backend per PID. API switch must detach first to avoid api_mismatch_existing.
                await DetachInternalAsync("api_changed", cancellationToken).ConfigureAwait(false);
            }

            if (!CanAttach(settings, out var attachReason))
            {
                _loggerAccessor()?.Info($"stage=graphics_hook event=attach_skip reason={attachReason}.");
                if (settings.GraphicsHookFallbackOnError)
                {
                    await DetachInternalAsync($"attach_skip:{attachReason}", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (!EnsureHostProcess(settings))
            {
                _loggerAccessor()?.Error("stage=graphics_hook event=host_start_failed.");
                if (settings.GraphicsHookFallbackOnError)
                {
                    await DetachInternalAsync("host_start_failed", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (!await EnsurePipeConnectedAsync(settings, cancellationToken).ConfigureAwait(false))
            {
                _loggerAccessor()?.Error("stage=graphics_hook event=pipe_connect_failed.");
                if (settings.GraphicsHookFallbackOnError)
                {
                    await DetachInternalAsync("pipe_connect_failed", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            EnsureReceiveLoop();

            var effectiveOverlayEnabled =
                settings.GraphicsHookApi == GraphicsHookApiKind.Dx11 &&
                settings.GraphicsHookOverlayEnabled;

            var attachRequest = new GraphicsHookAttachRequest(
                settings.FixedCaptureWindowProcessId,
                settings.GraphicsHookApi,
                settings.GraphicsHookCaptureFpsLimit,
                effectiveOverlayEnabled);

            await SendCommandAsync(new GraphicsHookCommandEnvelope("attach", attachRequest), cancellationToken).ConfigureAwait(false);
            _attachedPid = settings.FixedCaptureWindowProcessId;
            _attachedApi = settings.GraphicsHookApi;
            // WHY: Publishing config via shared memory lets runtime/UI changes take effect even if the pipe is slow/unavailable.
            _configWriter.TryWrite(_attachedPid, _attachedApi, settings.GraphicsHookCaptureFpsLimit, effectiveOverlayEnabled);
            _loggerAccessor()?.Info(
                $"stage=graphics_hook event=attach_requested pid={_attachedPid} api={_attachedApi} fps_limit={settings.GraphicsHookCaptureFpsLimit} overlay={effectiveOverlayEnabled}.");
        }
        catch (OperationCanceledException)
        {
            // NOTE: Settings apply cancellation is expected during shutdown or rapid reconfiguration.
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Graphics hook apply failed.");
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DetachInternalAsync("stop", cancellationToken).ConfigureAwait(false);
            await ShutdownHostProcessAsync().ConfigureAwait(false);
            DisposePipe();
        }
        finally
        {
            _sync.Release();
        }
    }

    public bool TryWriteOverlayV2(
        int pid,
        uint canvasW,
        uint canvasH,
        ReadOnlySpan<GraphicsHookOverlayV2CommandWriter.TextBlockV2> blocks,
        byte[] textBlob,
        int textBytes,
        out string? failureReason,
        uint flags = 0)
    {
        failureReason = null;
        if (_disposed)
        {
            failureReason = "disposed";
            return false;
        }

        if (pid <= 0 || pid != _attachedPid)
        {
            failureReason = $"pid_mismatch(attached={_attachedPid}, requested={pid})";
            return false;
        }

        if (_attachedApi != GraphicsHookApiKind.Dx11)
        {
            failureReason = $"overlay_not_supported(api={_attachedApi})";
            return false;
        }

        // WHY: v2 overlay is best-effort; don't block attach/detach or settings apply.
        if (!_sync.Wait(0))
        {
            failureReason = "sync_busy";
            return false;
        }

        try
        {
            var wrote = _overlayV2Writer.TryWrite(pid, _attachedApi, canvasW, canvasH, blocks, textBlob, textBytes, flags);
            if (!wrote)
            {
                failureReason = "writer_failed";
            }

            return wrote;
        }
        finally
        {
            _sync.Release();
        }
    }

    public bool TryPublishRuntimeConfig(int pid, int captureFpsLimit, bool overlayEnabled, out string? failureReason)
    {
        failureReason = null;
        if (_disposed)
        {
            failureReason = "disposed";
            return false;
        }

        if (pid <= 0)
        {
            failureReason = $"invalid_pid({pid})";
            return false;
        }

        if (pid != _attachedPid)
        {
            failureReason = $"pid_mismatch(attached={_attachedPid}, requested={pid})";
            return false;
        }

        // WHY: Runtime config publish is best-effort and should not contend with attach/detach/settings apply.
        if (!_sync.Wait(0))
        {
            failureReason = "sync_busy";
            return false;
        }

        try
        {
            var effectiveOverlayEnabled = _attachedApi == GraphicsHookApiKind.Dx11 && overlayEnabled;
            var wrote = _configWriter.TryWrite(pid, _attachedApi, captureFpsLimit, effectiveOverlayEnabled);
            if (!wrote)
            {
                failureReason = "writer_failed";
            }

            return wrote;
        }
        finally
        {
            _sync.Release();
        }
    }

    private bool CanAttach(AppSettings settings, out string reason)
    {
        if (settings.CaptureMode != CaptureMode.ActiveWindow)
        {
            reason = "capture_mode_not_active_window";
            return false;
        }

        if (!settings.EnableFixedCaptureWindow || settings.FixedCaptureWindowProcessId <= 0)
        {
            reason = "fixed_window_not_bound";
            return false;
        }

        reason = "ok";
        return true;
    }

    private bool EnsureHostProcess(AppSettings settings)
    {
        if (_hostProcess is { HasExited: false })
        {
            return true;
        }

        var hostPath = ResolveHostPath();
        if (!File.Exists(hostPath))
        {
            _loggerAccessor()?.Error($"Graphics HookHost executable not found: {hostPath}");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(hostPath) ?? Environment.CurrentDirectory
            };

            _hostProcess = Process.Start(startInfo);
            if (_hostProcess == null)
            {
                _loggerAccessor()?.Error("Failed to start Graphics HookHost process.");
                return false;
            }

            _loggerAccessor()?.Info($"stage=graphics_hook event=host_started path=\"{hostPath}\" pid={_hostProcess.Id}.");
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, $"Failed to start Graphics HookHost: {hostPath}");
            return false;
        }
    }

    private async Task<bool> EnsurePipeConnectedAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_pipe is { IsConnected: true } && _writer != null)
        {
            return true;
        }

        DisposePipe();

        try
        {
            _pipe = new NamedPipeClientStream(
                ".",
                settings.GraphicsHookPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            // WHY: Keep timeout short to avoid blocking UI during settings saves when host is unavailable.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            await _pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            _reader = new StreamReader(_pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            _loggerAccessor()?.Info($"stage=graphics_hook event=pipe_connected name={settings.GraphicsHookPipeName}.");
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, $"Failed to connect Graphics hook pipe: {settings.GraphicsHookPipeName}");
            DisposePipe();
            return false;
        }
    }

    private void EnsureReceiveLoop()
    {
        if (_receiveTask != null || _reader == null)
        {
            return;
        }

        _receiveCts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_reader == null)
                {
                    return;
                }

                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                {
                    return;
                }

                HandleHostLine(line);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _loggerAccessor()?.Error(ex, "stage=graphics_hook event=receive_failed.");
                return;
            }
        }
    }

    private void HandleHostLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                return;
            }

            var type = typeProp.GetString();
            if (!string.Equals(type, "hookState", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!doc.RootElement.TryGetProperty("payload", out var payload))
            {
                return;
            }

            var state = payload.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;
            var reason = payload.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;
            var frameMap = payload.TryGetProperty("frameMap", out var frameMapProp) ? frameMapProp.GetString() : null;

            var pid = payload.TryGetProperty("pid", out var pidProp) && pidProp.TryGetInt32(out var parsedPid)
                ? parsedPid
                : _attachedPid;

            _loggerAccessor()?.Info(
                $"stage=graphics_hook event=hook_state pid={pid} state={state ?? "unknown"} reason={reason ?? "unknown"} frameMap=\"{frameMap ?? string.Empty}\".");

            if (!string.IsNullOrWhiteSpace(frameMap) && pid > 0)
            {
                HookFrameMapRegistry.Set(pid, frameMap);
            }

            if (pid > 0 && string.Equals(state, "Detached", StringComparison.OrdinalIgnoreCase))
            {
                HookFrameMapRegistry.Clear(pid);
            }

            if (pid > 0 &&
                string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase) &&
                reason != null &&
                reason.StartsWith("attach_failed", StringComparison.OrdinalIgnoreCase))
            {
                HookFrameMapRegistry.Clear(pid);
                if (pid == _attachedPid)
                {
                    // WHY: Host rejected attach (e.g., api_not_implemented). Reset local attachment to avoid writing stale mappings.
                    _attachedPid = 0;
                    _attachedApi = GraphicsHookApiKind.Dx11;
                    _overlayV2Writer.Reset();
                    _configWriter.Reset();
                }
            }
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, $"stage=graphics_hook event=host_json_parse_failed line=\"{line}\".");
        }
    }

    private async Task SendCommandAsync(GraphicsHookCommandEnvelope command, CancellationToken cancellationToken)
    {
        if (_writer == null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(command, JsonOptions);
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private void SendCommandSync(GraphicsHookCommandEnvelope command)
    {
        if (_writer == null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(command, JsonOptions);
        _writer.WriteLine(json);
    }

    private async Task DetachInternalAsync(string reason, CancellationToken cancellationToken)
    {
        if (_attachedPid > 0)
        {
            try
            {
                await SendCommandAsync(
                        new GraphicsHookCommandEnvelope("detach", new GraphicsHookDetachRequest(_attachedPid)),
                        cancellationToken)
                    .ConfigureAwait(false);
                _loggerAccessor()?.Info($"stage=graphics_hook event=detach_requested pid={_attachedPid} reason={reason}.");
            }
            catch (Exception ex)
            {
                _loggerAccessor()?.Error(ex, "Failed to send Graphics hook detach command.");
            }
        }

        HookFrameMapRegistry.Clear(_attachedPid);
        _attachedPid = 0;
        _attachedApi = GraphicsHookApiKind.Dx11;
        _overlayV2Writer.Reset();
        _configWriter.Reset();
    }

    private async Task ShutdownHostProcessAsync()
    {
        if (_hostProcess == null)
        {
            return;
        }

        try
        {
            if (_hostProcess.HasExited)
            {
                _hostProcess.Dispose();
                _hostProcess = null;
                return;
            }
        }
        catch
        {
            return;
        }

        var sent = await TrySendShutdownCommandAsync().ConfigureAwait(false);
        if (!sent)
        {
            _loggerAccessor()?.Info("stage=graphics_hook event=host_shutdown_send_skipped.");
        }

        await WaitOrKillHostProcessAsync().ConfigureAwait(false);
    }

    private async Task<bool> TrySendShutdownCommandAsync()
    {
        var command = new GraphicsHookCommandEnvelope("shutdown", new GraphicsHookShutdownRequest());
        try
        {
            if (_writer != null)
            {
                await SendCommandAsync(command, CancellationToken.None).ConfigureAwait(false);
                _loggerAccessor()?.Info("stage=graphics_hook event=host_shutdown_requested via=existing_pipe.");
                return true;
            }
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "stage=graphics_hook event=host_shutdown_request_failed via=existing_pipe.");
        }

        if (string.IsNullOrWhiteSpace(_lastPipeName))
        {
            return false;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", _lastPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var json = JsonSerializer.Serialize(command, JsonOptions);
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            _loggerAccessor()?.Info($"stage=graphics_hook event=host_shutdown_requested via=probe_pipe name={_lastPipeName}.");
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, $"stage=graphics_hook event=host_shutdown_request_failed via=probe_pipe name={_lastPipeName}.");
            return false;
        }
    }

    private async Task WaitOrKillHostProcessAsync()
    {
        if (_hostProcess == null)
        {
            return;
        }

        try
        {
            if (_hostProcess.HasExited)
            {
                _hostProcess.Dispose();
                _hostProcess = null;
                return;
            }
        }
        catch
        {
            return;
        }

        var exited = false;
        try
        {
            // WHY: Host shutdown is expected to complete quickly; bounded wait prevents UI shutdown hangs.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            await _hostProcess.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            exited = true;
        }
        catch (OperationCanceledException)
        {
            exited = false;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "stage=graphics_hook event=host_wait_exit_failed.");
            exited = false;
        }

        if (!exited)
        {
            try
            {
                if (!_hostProcess.HasExited)
                {
                    _hostProcess.Kill(entireProcessTree: true);
                    _hostProcess.WaitForExit(1000);
                    _loggerAccessor()?.Info("stage=graphics_hook event=host_killed reason=shutdown_timeout.");
                }
            }
            catch (Exception ex)
            {
                _loggerAccessor()?.Error(ex, "stage=graphics_hook event=host_kill_failed.");
            }
        }

        try
        {
            _hostProcess.Dispose();
        }
        catch
        {
        }
        finally
        {
            _hostProcess = null;
        }
    }

    private static string ResolveHostPath()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, FixedHookHostRelativePath));
    }

    private void DisposePipe()
    {
        try
        {
            _receiveCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _pipe?.Dispose();
        }
        catch
        {
            // NOTE: Best-effort cleanup path.
        }
        finally
        {
            _writer = null;
            _reader = null;
            _pipe = null;
            _receiveCts?.Dispose();
            _receiveCts = null;
            _receiveTask = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _attachedPid = 0;
        _attachedApi = GraphicsHookApiKind.Dx11;
        TryForceTerminateHostProcessOnDispose();
        DisposePipe();
        _overlayV2Writer.Dispose();
        _configWriter.Dispose();
        _sync.Dispose();
    }

    private void TryForceTerminateHostProcessOnDispose()
    {
        if (_hostProcess == null)
        {
            return;
        }

        try
        {
            if (_hostProcess.HasExited)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        try
        {
            _hostProcess.Kill(entireProcessTree: true);
            _hostProcess.WaitForExit(500);
        }
        catch
        {
            // NOTE: Dispose must stay best-effort.
        }
        finally
        {
            try
            {
                _hostProcess.Dispose();
            }
            catch
            {
            }

            _hostProcess = null;
        }
    }
}



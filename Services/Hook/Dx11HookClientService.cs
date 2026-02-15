using System;
using System.Buffers.Binary;
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

internal sealed class Dx11HookClientService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dx11HookOverlayCommandWriter _overlayWriter = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _hostProcess;
    private int _attachedPid;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;

    public Dx11HookClientService(Func<AppLogger?> loggerAccessor)
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

            if (!settings.EnableDx11HookPipeline)
            {
                await DetachInternalAsync("disabled", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!CanAttach(settings, out var attachReason))
            {
                _loggerAccessor()?.Info($"stage=dx11_hook event=attach_skip reason={attachReason}.");
                if (settings.Dx11HookFallbackOnError)
                {
                    await DetachInternalAsync($"attach_skip:{attachReason}", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (!EnsureHostProcess(settings))
            {
                _loggerAccessor()?.Error("stage=dx11_hook event=host_start_failed.");
                if (settings.Dx11HookFallbackOnError)
                {
                    await DetachInternalAsync("host_start_failed", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (!await EnsurePipeConnectedAsync(settings, cancellationToken).ConfigureAwait(false))
            {
                _loggerAccessor()?.Error("stage=dx11_hook event=pipe_connect_failed.");
                if (settings.Dx11HookFallbackOnError)
                {
                    await DetachInternalAsync("pipe_connect_failed", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            EnsureReceiveLoop();

            var attachRequest = new Dx11HookAttachRequest(
                settings.FixedCaptureWindowProcessId,
                settings.Dx11HookCaptureFpsLimit,
                settings.Dx11HookOverlayEnabled);

            await SendCommandAsync(new Dx11HookCommandEnvelope("attach", attachRequest), cancellationToken).ConfigureAwait(false);
            _attachedPid = settings.FixedCaptureWindowProcessId;
            _loggerAccessor()?.Info(
                $"stage=dx11_hook event=attach_requested pid={_attachedPid} fps_limit={settings.Dx11HookCaptureFpsLimit} overlay={settings.Dx11HookOverlayEnabled}.");
        }
        catch (OperationCanceledException)
        {
            // NOTE: Settings apply cancellation is expected during shutdown or rapid reconfiguration.
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "DX11 hook apply failed.");
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
            DisposePipe();
        }
        finally
        {
            _sync.Release();
        }
    }

    public void TrySendOverlayUpdate(int pid, IReadOnlyList<Dx11HookOverlayRect> rects)
    {
        if (_disposed)
        {
            return;
        }

        if (pid <= 0 || pid != _attachedPid)
        {
            return;
        }

        if (_pipe is not { IsConnected: true } || _writer == null)
        {
            return;
        }

        if (!_sync.Wait(0))
        {
            // WHY: Overlay updates are best-effort; drop when attach/detach/settings apply is running.
            return;
        }

        try
        {
            const int maxRects = 512;
            var count = Math.Min(rects.Count, maxRects);
            var payload = PackOverlayRectCommands(rects, count);

            if (_overlayWriter.TryWrite(pid, count, payload))
            {
                return;
            }

            var b64 = payload.Length == 0 ? string.Empty : Convert.ToBase64String(payload);
            SendCommandSync(new Dx11HookCommandEnvelope("overlayUpdate", new Dx11HookOverlayUpdateRequest(pid, count, b64)));
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "stage=dx11_hook event=overlay_update_failed.");
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

        var hostPath = ResolveHostPath(settings.Dx11HookHostPath);
        if (!File.Exists(hostPath))
        {
            _loggerAccessor()?.Error($"DX11 HookHost executable not found: {hostPath}");
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
                _loggerAccessor()?.Error("Failed to start DX11 HookHost process.");
                return false;
            }

            _loggerAccessor()?.Info($"stage=dx11_hook event=host_started path=\"{hostPath}\" pid={_hostProcess.Id}.");
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, $"Failed to start DX11 HookHost: {hostPath}");
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
                settings.Dx11HookPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            // WHY: Keep timeout short to avoid blocking UI during settings saves when host is unavailable.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            await _pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            _reader = new StreamReader(_pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            _loggerAccessor()?.Info($"stage=dx11_hook event=pipe_connected name={settings.Dx11HookPipeName}.");
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, $"Failed to connect DX11 hook pipe: {settings.Dx11HookPipeName}");
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
                _loggerAccessor()?.Error(ex, "stage=dx11_hook event=receive_failed.");
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
                $"stage=dx11_hook event=hook_state pid={pid} state={state ?? "unknown"} reason={reason ?? "unknown"} frameMap=\"{frameMap ?? string.Empty}\".");

            if (!string.IsNullOrWhiteSpace(frameMap) && pid > 0)
            {
                HookFrameMapRegistry.Set(pid, frameMap);
            }

            if (pid > 0 && string.Equals(state, "Detached", StringComparison.OrdinalIgnoreCase))
            {
                HookFrameMapRegistry.Clear(pid);
            }
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, $"stage=dx11_hook event=host_json_parse_failed line=\"{line}\".");
        }
    }

    private async Task SendCommandAsync(Dx11HookCommandEnvelope command, CancellationToken cancellationToken)
    {
        if (_writer == null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(command, JsonOptions);
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private void SendCommandSync(Dx11HookCommandEnvelope command)
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
                        new Dx11HookCommandEnvelope("detach", new Dx11HookDetachRequest(_attachedPid)),
                        cancellationToken)
                    .ConfigureAwait(false);
                _loggerAccessor()?.Info($"stage=dx11_hook event=detach_requested pid={_attachedPid} reason={reason}.");
            }
            catch (Exception ex)
            {
                _loggerAccessor()?.Error(ex, "Failed to send DX11 detach command.");
            }
        }

        HookFrameMapRegistry.Clear(_attachedPid);
        _attachedPid = 0;
        _overlayWriter.Reset();
    }

    private static byte[] PackOverlayRectCommands(IReadOnlyList<Dx11HookOverlayRect> rects, int count)
    {
        if (count <= 0)
        {
            return Array.Empty<byte>();
        }

        // NOTE: Must match Native/HookCommon/HookIpcProtocol.h OverlayRectCommand binary layout.
        const int bytesPerRect = 24;
        var bytes = new byte[count * bytesPerRect];
        var span = bytes.AsSpan();
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            var r = rects[i];
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), BitConverter.SingleToInt32Bits(r.X));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 4, 4), BitConverter.SingleToInt32Bits(r.Y));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 8, 4), BitConverter.SingleToInt32Bits(r.W));
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 12, 4), BitConverter.SingleToInt32Bits(r.H));
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 16, 4), r.Argb);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 20, 4), r.Thickness);
            offset += bytesPerRect;
        }

        return bytes;
    }

    private static string ResolveHostPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var cwdCandidate = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, configuredPath));
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        var baseDirCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
        if (File.Exists(baseDirCandidate))
        {
            return baseDirCandidate;
        }

        // WHY: Return deterministic path even before host binary is built.
        return cwdCandidate;
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
        DisposePipe();
        _overlayWriter.Dispose();
        _sync.Dispose();
    }
}

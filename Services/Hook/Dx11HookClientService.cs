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

internal sealed class Dx11HookClientService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private Process? _hostProcess;
    private int _attachedPid;
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

    private async Task SendCommandAsync(Dx11HookCommandEnvelope command, CancellationToken cancellationToken)
    {
        if (_writer == null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(command, JsonOptions);
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
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

        _attachedPid = 0;
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
            _writer?.Dispose();
            _pipe?.Dispose();
        }
        catch
        {
            // NOTE: Best-effort cleanup path.
        }
        finally
        {
            _writer = null;
            _pipe = null;
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
        _sync.Dispose();
    }
}

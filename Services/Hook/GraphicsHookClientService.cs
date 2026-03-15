using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services.Hook.Contracts;

namespace Hotkey_Translator.Services.Hook;

internal sealed class GraphicsHookClientService : IDisposable
{
    private const string FixedHookHostRelativePathX64 = "Native\\HookHost\\bin\\HookHost.exe";
    private const string FixedHookHostRelativePathX86 = "Native\\HookHost\\bin\\x86\\HookHost.exe";
    private const string FixedHookAgentDx9RelativePathX64 = "Native\\HookHost\\bin\\HookAgentDx9.dll";
    private const string FixedHookAgentDx9RelativePathX86 = "Native\\HookHost\\bin\\x86\\HookAgentDx9.dll";
    private const string FixedHookAgentDx11RelativePathX64 = "Native\\HookHost\\bin\\HookAgentDx11.dll";
    private const string FixedHookAgentDx11RelativePathX86 = "Native\\HookHost\\bin\\x86\\HookAgentDx11.dll";
    private const string FixedHookAgentVulkanRelativePathX64 = "Native\\HookHost\\bin\\HookAgentVulkan.dll";
    private const string FixedHookAgentVulkanRelativePathX86 = "Native\\HookHost\\bin\\x86\\HookAgentVulkan.dll";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const ushort ImageFileMachineUnknown = 0x0000;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly LauncherSessionTargetState _launcherSessionTargetState;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly GraphicsHookOverlayV2CommandWriter _overlayV2Writer = new();
    private readonly GraphicsHookConfigWriter _configWriter = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _hostProcess;
    private string _hostPath = string.Empty;
    private string _lastPipeName = "hotkey_translator_hook";
    private int _attachedPid;
    private GraphicsHookApiKind _attachedApi = GraphicsHookApiKind.Dx11;
    private uint _runtimeConfigFlags;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;

    private enum ProcessBitness
    {
        Unknown = 0,
        X86,
        X64
    }

    private readonly struct HostSelection
    {
        public HostSelection(string hostPath, string agentPath, ProcessBitness targetBitness, GraphicsHookApiKind api)
        {
            HostPath = hostPath;
            AgentPath = agentPath;
            TargetBitness = targetBitness;
            Api = api;
        }

        public string HostPath { get; }
        public string AgentPath { get; }
        public ProcessBitness TargetBitness { get; }
        public GraphicsHookApiKind Api { get; }
    }

    public GraphicsHookClientService(
        Func<AppLogger?> loggerAccessor,
        LauncherSessionTargetState launcherSessionTargetState)
    {
        _loggerAccessor = loggerAccessor;
        _launcherSessionTargetState = launcherSessionTargetState;
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
            var targetPid = ResolveTargetProcessId(settings);

            if (_attachedPid > 0 &&
                _attachedPid == targetPid &&
                _attachedApi != settings.GraphicsHookApi)
            {
                // WHY: Host keeps one injected backend per PID. API switch must detach first to avoid api_mismatch_existing.
                await DetachInternalAsync("api_changed", cancellationToken).ConfigureAwait(false);
            }

            if (_attachedPid > 0 &&
                _attachedPid != targetPid)
            {
                // WHY: Launcher handoff can move capture from a bootstrap PID to the real render PID.
                // Keep only one active attachment in the host so runtime/shared-state stay aligned with the committed target.
                await DetachInternalAsync("pid_changed", cancellationToken).ConfigureAwait(false);
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

            if (!TryResolveHostSelection(settings, targetPid, out var hostSelection, out var hostResolveReason))
            {
                _loggerAccessor()?.Error(
                    $"stage=graphics_hook event=attach_skip reason={hostResolveReason ?? "host_selection_failed"} pid={targetPid} api={settings.GraphicsHookApi}.");
                if (settings.GraphicsHookFallbackOnError)
                {
                    await DetachInternalAsync($"attach_skip:{hostResolveReason ?? "host_selection_failed"}", cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (!EnsureHostProcess(hostSelection))
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
                IsHookOverlaySupportedApi(settings.GraphicsHookApi) &&
                settings.GraphicsHookOverlayEnabled;
            var configFlags = BuildConfigFlags(settings);

            var attachRequest = new GraphicsHookAttachRequest(
                targetPid,
                settings.GraphicsHookApi,
                settings.GraphicsHookCaptureFpsLimit,
                effectiveOverlayEnabled,
                configFlags);

            await SendCommandAsync(new GraphicsHookCommandEnvelope("attach", attachRequest), cancellationToken).ConfigureAwait(false);
            _attachedPid = targetPid;
            _attachedApi = settings.GraphicsHookApi;
            _runtimeConfigFlags = configFlags;
            // WHY: Publishing config via shared memory lets runtime/UI changes take effect even if the pipe is slow/unavailable.
            _configWriter.TryWrite(
                _attachedPid,
                _attachedApi,
                settings.GraphicsHookCaptureFpsLimit,
                effectiveOverlayEnabled,
                _runtimeConfigFlags);
            _loggerAccessor()?.Info(
                $"stage=graphics_hook event=attach_requested pid={_attachedPid} api={_attachedApi} bitness={FormatBitness(hostSelection.TargetBitness)} host=\"{hostSelection.HostPath}\" agent=\"{hostSelection.AgentPath}\" fps_limit={settings.GraphicsHookCaptureFpsLimit} overlay={effectiveOverlayEnabled}.");
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

        if (!IsHookOverlaySupportedApi(_attachedApi))
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
            var effectiveOverlayEnabled = IsHookOverlaySupportedApi(_attachedApi) && overlayEnabled;
            var wrote = _configWriter.TryWrite(pid, _attachedApi, captureFpsLimit, effectiveOverlayEnabled, _runtimeConfigFlags);
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

        if (ResolveTargetProcessId(settings) <= 0)
        {
            reason = "fixed_window_not_bound";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool IsHookOverlaySupportedApi(GraphicsHookApiKind api)
    {
        return api is GraphicsHookApiKind.Dx9 or GraphicsHookApiKind.Dx11 or GraphicsHookApiKind.Vulkan;
    }

    private int ResolveTargetProcessId(AppSettings settings)
    {
        return _launcherSessionTargetState.ResolveEffectiveProcessId(settings);
    }

    private bool EnsureHostProcess(HostSelection hostSelection)
    {
        if (_hostProcess is { HasExited: false })
        {
            if (string.Equals(_hostPath, hostSelection.HostPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // WHY: x86/x64 を跨ぐと既存Hostでは注入できないため、bitness変更時はHostを入れ替える。
            TryForceTerminateHostProcessOnDispose();
            DisposePipe();
        }

        var hostPath = hostSelection.HostPath;
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

            _hostPath = hostPath;
            _loggerAccessor()?.Info(
                $"stage=graphics_hook event=host_started path=\"{hostPath}\" pid={_hostProcess.Id} bitness={FormatBitness(hostSelection.TargetBitness)} api={hostSelection.Api}.");
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, $"Failed to start Graphics HookHost: {hostPath}");
            return false;
        }
    }

    private bool TryResolveHostSelection(AppSettings settings, int pid, out HostSelection selection, out string? failureReason)
    {
        var targetBitness = GetProcessBitness(pid, out var bitnessReason);
        if (RequiresTargetBitness(settings.GraphicsHookApi) && targetBitness == ProcessBitness.Unknown)
        {
            // WHY: Dx9/Dx11/Vulkan need same-bitness host/agent selection; guessing here tends to fail later as Remote_LoadLibraryW_failed.
            failureReason = $"target_bitness_unknown({bitnessReason ?? "unknown"})";
            selection = default;
            return false;
        }

        var hostPath = ResolveHostPath(targetBitness);
        var agentPath = ResolveAgentPath(targetBitness, settings.GraphicsHookApi);

        if (!File.Exists(hostPath))
        {
            failureReason = $"host_missing({hostPath})";
            selection = default;
            return false;
        }

        if (!string.IsNullOrEmpty(agentPath) && !File.Exists(agentPath))
        {
            failureReason = $"agent_missing({agentPath})";
            selection = default;
            return false;
        }

        selection = new HostSelection(hostPath, agentPath, targetBitness, settings.GraphicsHookApi);
        failureReason = null;
        _loggerAccessor()?.Info(
            $"stage=graphics_hook event=host_selection pid={pid} api={settings.GraphicsHookApi} target_bitness={FormatBitness(targetBitness)} host=\"{hostPath}\" agent=\"{agentPath}\" reason={bitnessReason ?? "none"}.");
        return true;
    }

    private static bool RequiresTargetBitness(GraphicsHookApiKind api)
    {
        return api is GraphicsHookApiKind.Dx9 or GraphicsHookApiKind.Dx11 or GraphicsHookApiKind.Vulkan;
    }

    private static string ResolveHostPath(ProcessBitness targetBitness)
    {
        // WHY: HookHost itself must match the target bitness because the current injector does not support cross-bitness remote calls.
        if (targetBitness == ProcessBitness.X86)
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, FixedHookHostRelativePathX86));
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, FixedHookHostRelativePathX64));
    }

    private static string ResolveAgentPath(ProcessBitness targetBitness, GraphicsHookApiKind api)
    {
        var useX86 = targetBitness == ProcessBitness.X86;
        var relativePath = api switch
        {
            GraphicsHookApiKind.Dx9 => useX86 ? FixedHookAgentDx9RelativePathX86 : FixedHookAgentDx9RelativePathX64,
            GraphicsHookApiKind.Dx11 => useX86 ? FixedHookAgentDx11RelativePathX86 : FixedHookAgentDx11RelativePathX64,
            GraphicsHookApiKind.Vulkan => useX86 ? FixedHookAgentVulkanRelativePathX86 : FixedHookAgentVulkanRelativePathX64,
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(relativePath))
        {
            return string.Empty;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
    }

    private static string FormatBitness(ProcessBitness bitness)
    {
        return bitness switch
        {
            ProcessBitness.X86 => "x86",
            ProcessBitness.X64 => "x64",
            _ => "unknown"
        };
    }

    private static ProcessBitness GetProcessBitness(int pid, out string? reason)
    {
        reason = null;
        if (pid <= 0)
        {
            reason = "invalid_pid";
            return ProcessBitness.Unknown;
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (processHandle == IntPtr.Zero)
        {
            reason = $"open_process_failed({Marshal.GetLastWin32Error()})";
            return ProcessBitness.Unknown;
        }

        try
        {
            if (TryGetProcessBitnessWithWow64Process2(processHandle, out var wow64v2Bitness, out var wow64v2Reason))
            {
                reason = wow64v2Reason;
                return wow64v2Bitness;
            }

            if (TryGetProcessBitnessWithWow64Process(processHandle, out var wow64Bitness, out var wow64Reason))
            {
                reason = wow64Reason;
                return wow64Bitness;
            }

            reason = "wow64_query_failed";
            return ProcessBitness.Unknown;
        }
        finally
        {
            _ = CloseHandle(processHandle);
        }
    }

    private static bool TryGetProcessBitnessWithWow64Process2(IntPtr processHandle, out ProcessBitness bitness, out string reason)
    {
        bitness = ProcessBitness.Unknown;
        reason = "wow64process2_unavailable";

        try
        {
            if (!IsWow64Process2(processHandle, out var processMachine, out var nativeMachine))
            {
                reason = $"wow64process2_failed({Marshal.GetLastWin32Error()})";
                return false;
            }

            if (processMachine == ImageFileMachineI386)
            {
                bitness = ProcessBitness.X86;
                reason = "wow64process2_i386";
                return true;
            }

            if (processMachine == ImageFileMachineUnknown &&
                (nativeMachine == ImageFileMachineAmd64 || nativeMachine == ImageFileMachineArm64))
            {
                bitness = ProcessBitness.X64;
                reason = "wow64process2_native64";
                return true;
            }

            reason = $"wow64process2_unknown(pm={processMachine},nm={nativeMachine})";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            reason = "wow64process2_not_found";
            return false;
        }
    }

    private static bool TryGetProcessBitnessWithWow64Process(IntPtr processHandle, out ProcessBitness bitness, out string reason)
    {
        bitness = ProcessBitness.Unknown;
        reason = "wow64process_unavailable";

        if (!IsWow64Process(processHandle, out var isWow64))
        {
            reason = $"wow64process_failed({Marshal.GetLastWin32Error()})";
            return false;
        }

        if (!Environment.Is64BitOperatingSystem)
        {
            bitness = ProcessBitness.X86;
            reason = "wow64process_os32";
            return true;
        }

        bitness = isWow64 ? ProcessBitness.X86 : ProcessBitness.X64;
        reason = isWow64 ? "wow64process_wow64" : "wow64process_native64";
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

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
        _runtimeConfigFlags = 0;
        _overlayV2Writer.Reset();
        _configWriter.Reset();
    }

    private static uint BuildConfigFlags(AppSettings settings)
    {
        var flags = 0u;
        if (settings.EnableGraphicsHookPerfDiagLog)
        {
            flags |= GraphicsHookConfigWriter.ConfigFlagEnablePerfDiagLog;
        }

        if (settings.EnableGraphicsHookDiagFileSink)
        {
            flags |= GraphicsHookConfigWriter.ConfigFlagEnableDiagFileSink;
        }

        return flags;
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
            _hostPath = string.Empty;
        }
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
        _runtimeConfigFlags = 0;
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
            _hostPath = string.Empty;
        }
    }
}



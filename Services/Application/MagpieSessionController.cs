using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Application;

internal sealed class MagpieSessionController : IDisposable
{
    private const string FixedMagpieCoreRelativePath = "Tools\\Magpie\\Magpie.Core.exe";
    private readonly WindowBindingService _windowBindingService;
    private readonly IMagpieProcessService _processService;
    private readonly IMagpieIpcClient _ipcClient;
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Action<string> _appendLog;

    private IntPtr _targetHwnd;
    private Rect _sourceClientBounds;
    private Rect _monitorBounds;
    private static readonly uint MagpieScalingChangedMessage = RegisterWindowMessage("MagpieScalingChanged");

    public MagpieSessionController(
        WindowBindingService windowBindingService,
        IMagpieProcessService processService,
        IMagpieIpcClient ipcClient,
        Func<AppLogger?> loggerAccessor,
        Action<string> appendLog)
    {
        _windowBindingService = windowBindingService;
        _processService = processService;
        _ipcClient = ipcClient;
        _loggerAccessor = loggerAccessor;
        _appendLog = appendLog;
    }

    public event Action<bool>? ActiveStateChanged;

    public bool IsActive { get; private set; }

    public uint MagpieScalingChangedMessageId => MagpieScalingChangedMessage;

    public async Task ToggleAsync(AppSettings settings, Func<Task> persistSettingsAsync)
    {
        if (IsActive)
        {
            StopScaling("hotkey");
            return;
        }

        if (!settings.EnableMirrorFullscreenMode)
        {
            _appendLog("Mirror fullscreen is disabled in settings.");
            return;
        }

        if (await TryStartScalingAsync(settings, persistSettingsAsync).ConfigureAwait(true))
        {
            _appendLog("Mirror fullscreen started.");
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        if (!settings.EnableMirrorFullscreenMode && IsActive)
        {
            StopScaling("settings");
        }
    }

    public Rect? MapScreenRect(Rect sourceRect)
    {
        if (!IsActive || sourceRect.IsEmpty || sourceRect.Width <= 0 || sourceRect.Height <= 0)
        {
            return null;
        }

        if (!TryRefreshMappingContext())
        {
            return null;
        }

        var scaleX = _monitorBounds.Width / _sourceClientBounds.Width;
        var scaleY = _monitorBounds.Height / _sourceClientBounds.Height;
        if (scaleX <= 0 || scaleY <= 0)
        {
            return null;
        }

        var mapped = new Rect(
            _monitorBounds.X + (sourceRect.X - _sourceClientBounds.X) * scaleX,
            _monitorBounds.Y + (sourceRect.Y - _sourceClientBounds.Y) * scaleY,
            sourceRect.Width * scaleX,
            sourceRect.Height * scaleY);
        var clipped = Rect.Intersect(mapped, _monitorBounds);
        if (clipped.IsEmpty || clipped.Width <= 0 || clipped.Height <= 0)
        {
            return null;
        }

        return clipped;
    }

    public bool TryGetScalingWindowHandle(out IntPtr scalingWindowHandle)
    {
        scalingWindowHandle = FindWindow(ScalingWindowClassName, null);
        return scalingWindowHandle != IntPtr.Zero;
    }

    public bool TrySyncExternalStop(string source, long reasonCode)
    {
        // NOTE: MagpieScalingChanged event=0 uses lParam=0 when scaling truly ended.
        // lParam=1 means temporary source focus loss and should not be treated as a terminal stop.
        if (reasonCode != 0)
        {
            LogInfo(
                $"stage=magpie_session event=state_sync_skip source={source} reason_code={reasonCode} active={(IsActive ? 1 : 0)}.");
            return false;
        }

        if (!IsActive)
        {
            return false;
        }

        SetActive(false);
        _targetHwnd = IntPtr.Zero;
        _sourceClientBounds = Rect.Empty;
        _monitorBounds = Rect.Empty;
        _appendLog("Mirror fullscreen stopped.");
        LogInfo($"stage=magpie_session event=state_sync active=0 source={source} reason_code={reasonCode}.");
        return true;
    }

    public void Shutdown()
    {
        if (IsActive)
        {
            StopScaling("shutdown");
        }

        if (!_ipcClient.TryExit(out var exitReason))
        {
            LogInfo($"stage=magpie_ipc event=exit result=failed reason=\"{exitReason ?? "unknown"}\".");
        }
        else
        {
            LogInfo("stage=magpie_ipc event=exit result=ok.");
        }

        if (!_processService.StopProcess(out var stopReason))
        {
            LogInfo($"stage=magpie_process event=stop result=failed reason=\"{stopReason ?? "unknown"}\".");
        }
    }

    public void Dispose()
    {
        Shutdown();
    }

    private async Task<bool> TryStartScalingAsync(AppSettings settings, Func<Task> persistSettingsAsync)
    {
        if (!TryResolveTargetWindow(settings, out var targetHwnd, out var boundForeground, out var reason))
        {
            _appendLog($"Mirror fullscreen start failed: {reason ?? "Failed to resolve target window."}");
            return false;
        }

        if (boundForeground)
        {
            _appendLog(
                $"Capture window locked: hwnd=0x{settings.FixedCaptureWindowHandle:X} pid={settings.FixedCaptureWindowProcessId} class=\"{settings.FixedCaptureWindowClassName}\" title=\"{settings.FixedCaptureWindowTitle}\".");
            await persistSettingsAsync().ConfigureAwait(true);
        }

        if (!_windowBindingService.TryGetWindowClientScreenRect(targetHwnd, out var sourceClientBounds))
        {
            _appendLog("Mirror fullscreen start failed: Failed to resolve source client bounds.");
            return false;
        }

        if (!TryGetMonitorBounds(targetHwnd, out var monitorBounds, out reason))
        {
            _appendLog($"Mirror fullscreen start failed: {reason ?? "Failed to resolve monitor bounds from target window."}");
            return false;
        }

        var corePath = ResolveMagpieCorePath();
        var configPath = ResolveMagpieConfigPath(corePath);
        LogInfo(
            $"stage=magpie_path event=resolve core=\"{corePath}\" config=\"{configPath}\".");
        if (!_processService.EnsureStarted(corePath, configPath, out reason))
        {
            _appendLog($"Mirror fullscreen start failed: {reason ?? "Failed to start Magpie.Core."}");
            LogInfo(
                $"stage=magpie_process event=start result=failed core=\"{corePath}\" config=\"{configPath}\" reason=\"{reason ?? "unknown"}\".");
            return false;
        }

        LogInfo($"stage=magpie_process event=start result=ok core=\"{corePath}\" config=\"{configPath}\".");

        if (!_ipcClient.TryWaitCoreWindow(TimeSpan.FromSeconds(5), out var coreHwnd))
        {
            _appendLog("Mirror fullscreen start failed: Magpie core window wait timeout.");
            _ = _processService.StopProcess(out _);
            return false;
        }

        LogInfo($"stage=magpie_ipc event=core_ready hwnd=0x{coreHwnd.ToInt64():X}.");

        if (!_ipcClient.TryStartScaling(targetHwnd.ToInt64(), settings.MagpieProfileIndex, windowedMode: false, out reason))
        {
            _appendLog($"Mirror fullscreen start failed: {reason ?? "IPC start failed."}");
            LogInfo($"stage=magpie_ipc event=start result=failed reason=\"{reason ?? "unknown"}\".");
            _ = _processService.StopProcess(out _);
            return false;
        }
        LogInfo(
            $"stage=magpie_ipc event=start result=ok hwnd=0x{targetHwnd.ToInt64():X} profile={settings.MagpieProfileIndex}.");

        _targetHwnd = targetHwnd;
        _sourceClientBounds = sourceClientBounds;
        _monitorBounds = monitorBounds;
        SetActive(true);
        LogInfo(
            $"stage=magpie_session event=started hwnd=0x{targetHwnd.ToInt64():X} src=[{_sourceClientBounds.X:0.##},{_sourceClientBounds.Y:0.##},{_sourceClientBounds.Width:0.##},{_sourceClientBounds.Height:0.##}] monitor=[{_monitorBounds.X:0.##},{_monitorBounds.Y:0.##},{_monitorBounds.Width:0.##},{_monitorBounds.Height:0.##}] profile={settings.MagpieProfileIndex}.");
        return true;
    }

    private bool TryResolveTargetWindow(AppSettings settings, out IntPtr targetHwnd, out bool boundForeground, out string? reason)
    {
        targetHwnd = IntPtr.Zero;
        boundForeground = false;
        reason = null;
        if (_windowBindingService.TryResolveWindowHandle(settings, out targetHwnd, out reason))
        {
            return true;
        }

        if (_windowBindingService.TryBindForegroundWindow(settings, out var spec, out reason))
        {
            targetHwnd = new IntPtr(spec.Hwnd);
            boundForeground = true;
            return true;
        }

        return false;
    }

    private void StopScaling(string source)
    {
        if (!_ipcClient.TryStopScaling(out var reason))
        {
            _appendLog($"Mirror fullscreen stop warning: {reason ?? "IPC stop failed."} Fallback to process stop.");
            _ = _processService.StopProcess(out _);
            LogInfo($"stage=magpie_ipc event=stop result=failed source={source} reason=\"{reason ?? "unknown"}\".");
        }
        else
        {
            LogInfo($"stage=magpie_ipc event=stop result=ok source={source}.");
        }

        SetActive(false);
        _targetHwnd = IntPtr.Zero;
        _sourceClientBounds = Rect.Empty;
        _monitorBounds = Rect.Empty;
        _appendLog("Mirror fullscreen stopped.");
    }

    private bool TryRefreshMappingContext()
    {
        if (_targetHwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!_windowBindingService.TryGetWindowClientScreenRect(_targetHwnd, out var latestSourceBounds))
        {
            LogInfo("stage=overlay_map event=refresh result=failed reason=source_bounds.");
            return false;
        }

        if (!TryGetMonitorBounds(_targetHwnd, out var latestMonitorBounds, out _))
        {
            LogInfo("stage=overlay_map event=refresh result=failed reason=monitor_bounds.");
            return false;
        }

        _sourceClientBounds = latestSourceBounds;
        _monitorBounds = latestMonitorBounds;
        return true;
    }

    private static string ResolveMagpieCorePath()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, FixedMagpieCoreRelativePath));
    }

    private static string ResolveMagpieConfigPath(string corePath)
    {
        var directory = Path.GetDirectoryName(corePath) ?? string.Empty;
        return Path.Combine(directory, "config.json");
    }

    private void SetActive(bool value)
    {
        if (IsActive == value)
        {
            return;
        }

        IsActive = value;
        ActiveStateChanged?.Invoke(value);
        LogInfo($"stage=magpie_session event=state active={(value ? 1 : 0)}.");
    }

    private void LogInfo(string message)
    {
        _loggerAccessor()?.Info(message);
    }

    private static bool TryGetMonitorBounds(IntPtr hwnd, out Rect bounds, out string? reason)
    {
        bounds = Rect.Empty;
        reason = null;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            reason = "MonitorFromWindow returned null.";
            return false;
        }

        var info = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref info))
        {
            reason = $"GetMonitorInfo failed (err={Marshal.GetLastWin32Error()}).";
            return false;
        }

        var width = Math.Max(0, info.Monitor.Right - info.Monitor.Left);
        var height = Math.Max(0, info.Monitor.Bottom - info.Monitor.Top);
        if (width <= 0 || height <= 0)
        {
            reason = "Resolved monitor bounds are empty.";
            return false;
        }

        bounds = new Rect(info.Monitor.Left, info.Monitor.Top, width, height);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    private const int MonitorDefaultToNearest = 2;
    private const string ScalingWindowClassName = "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

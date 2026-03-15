using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Hook;

internal sealed class LauncherSessionTargetState
{
    private readonly object _sync = new();
    private readonly Func<AppLogger?> _loggerAccessor;
    private Snapshot _current;

    public LauncherSessionTargetState(Func<AppLogger?> loggerAccessor)
    {
        _loggerAccessor = loggerAccessor;
    }

    public bool TryGetSnapshot(out Snapshot snapshot)
    {
        lock (_sync)
        {
            snapshot = _current;
            return snapshot.ProcessId > 0;
        }
    }

    public int ResolveEffectiveProcessId(AppSettings settings)
    {
        if (TryGetRuntimeTarget(settings, out var snapshot))
        {
            return snapshot.ProcessId;
        }

        return settings.EnableFixedCaptureWindow && settings.FixedCaptureWindowProcessId > 0
            ? settings.FixedCaptureWindowProcessId
            : 0;
    }

    public bool TryResolveEffectiveWindowHandle(AppSettings settings, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (TryGetRuntimeTarget(settings, out var snapshot))
        {
            if (snapshot.Hwnd == IntPtr.Zero)
            {
                return false;
            }

            hwnd = snapshot.Hwnd;
            return true;
        }

        if (settings.EnableFixedCaptureWindow && settings.FixedCaptureWindowHandle != 0)
        {
            hwnd = new IntPtr(settings.FixedCaptureWindowHandle);
            return hwnd != IntPtr.Zero;
        }

        return false;
    }

    public void SetProvisional(int processId, string processName, string source)
    {
        if (processId <= 0)
        {
            return;
        }

        var snapshot = new Snapshot(
            processId,
            IntPtr.Zero,
            processName ?? string.Empty,
            string.Empty,
            string.Empty,
            false,
            source ?? string.Empty);
        SetSnapshot(snapshot, "launcher_runtime_target_set", "provisional");
    }

    public void UpdateCandidate(ObservedWindowCandidate candidate, string source)
    {
        if (candidate.ProcessId <= 0)
        {
            return;
        }

        var snapshot = new Snapshot(
            candidate.ProcessId,
            candidate.Hwnd,
            candidate.ProcessName ?? string.Empty,
            candidate.WindowClass ?? string.Empty,
            candidate.WindowTitle ?? string.Empty,
            false,
            source ?? string.Empty);
        SetSnapshot(snapshot, "launcher_runtime_target_set", "candidate");
    }

    public void Confirm(LauncherTargetResolutionResult resolution, string source)
    {
        if (resolution.ProcessId <= 0)
        {
            return;
        }

        var snapshot = new Snapshot(
            resolution.ProcessId,
            resolution.Hwnd,
            resolution.ProcessName ?? string.Empty,
            resolution.WindowClass ?? string.Empty,
            resolution.WindowTitle ?? string.Empty,
            true,
            source ?? string.Empty);
        SetSnapshot(snapshot, "launcher_runtime_target_confirmed", resolution.Reason);
    }

    public void Clear(string reason)
    {
        lock (_sync)
        {
            if (_current.ProcessId <= 0)
            {
                return;
            }

            var previous = _current;
            _current = default;
            _loggerAccessor()?.Info(
                $"stage=graphics_hook event=launcher_runtime_target_cleared pid={previous.ProcessId} hwnd=0x{previous.Hwnd.ToInt64():X} " +
                $"confirmed={(previous.IsConfirmed ? 1 : 0)} reason={Sanitize(reason)}.");
        }
    }

    private bool TryGetRuntimeTarget(AppSettings settings, out Snapshot snapshot)
    {
        snapshot = default;
        if (!IsLauncherRuntimeEligible(settings))
        {
            return false;
        }

        lock (_sync)
        {
            if (_current.ProcessId <= 0)
            {
                return false;
            }

            snapshot = _current;
            return true;
        }
    }

    private void SetSnapshot(Snapshot snapshot, string eventName, string reason)
    {
        lock (_sync)
        {
            if (_current.ProcessId == snapshot.ProcessId &&
                _current.Hwnd == snapshot.Hwnd &&
                _current.IsConfirmed == snapshot.IsConfirmed &&
                string.Equals(_current.Source, snapshot.Source, StringComparison.Ordinal) &&
                string.Equals(_current.ProcessName, snapshot.ProcessName, StringComparison.Ordinal) &&
                string.Equals(_current.WindowClass, snapshot.WindowClass, StringComparison.Ordinal) &&
                string.Equals(_current.WindowTitle, snapshot.WindowTitle, StringComparison.Ordinal))
            {
                return;
            }

            _current = snapshot;
            _loggerAccessor()?.Info(
                $"stage=graphics_hook event={eventName} pid={snapshot.ProcessId} hwnd=0x{snapshot.Hwnd.ToInt64():X} " +
                $"confirmed={(snapshot.IsConfirmed ? 1 : 0)} source={Sanitize(snapshot.Source)} reason={Sanitize(reason)}.");
        }
    }

    private static bool IsLauncherRuntimeEligible(AppSettings settings)
    {
        return settings.EnableGraphicsHookPipeline &&
               settings.EnableGraphicsHookLauncher &&
               settings.CaptureMode == CaptureMode.ActiveWindow;
    }

    private static string Sanitize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Replace('"', '\'');
    }

    internal readonly record struct Snapshot(
        int ProcessId,
        IntPtr Hwnd,
        string ProcessName,
        string WindowClass,
        string WindowTitle,
        bool IsConfirmed,
        string Source);
}

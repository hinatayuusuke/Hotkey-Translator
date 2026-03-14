using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Hotkey_Translator.Services.Hook;

internal sealed class LauncherDiscoveryResolver
{
    private static readonly TimeSpan DiscoveryPollInterval = TimeSpan.FromMilliseconds(100);
    private readonly Func<AppLogger?> _loggerAccessor;

    public LauncherDiscoveryResolver(Func<AppLogger?> loggerAccessor)
    {
        _loggerAccessor = loggerAccessor;
    }

    public async Task<LauncherTargetResolutionResult> DiscoverAsync(
        string expectedExeName,
        string? expectedExePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var lastObservedPid = 0;
        ObservedWindowCandidate? bestCandidate = null;
        _loggerAccessor()?.Info(
            $"stage=graphics_hook event=discovery_start exe=\"{SanitizeForLog(expectedExeName)}\" timeoutMs={(int)timeout.TotalMilliseconds}.");

        while (DateTime.UtcNow - startedAtUtc < timeout && !cancellationToken.IsCancellationRequested)
        {
            var foreground = TryGetForegroundWindowCandidate(expectedExeName, expectedExePath);
            if (foreground != null)
            {
                var candidate = foreground.Value;
                if (candidate.ProcessId != lastObservedPid)
                {
                    lastObservedPid = candidate.ProcessId;
                    _loggerAccessor()?.Info(
                        $"stage=graphics_hook event=discovery_candidate pid={candidate.ProcessId} hwnd=0x{candidate.Hwnd.ToInt64():X} class=\"{SanitizeForLog(candidate.WindowClass)}\" classLen={candidate.WindowClassLength} title=\"{SanitizeForLog(candidate.WindowTitle)}\" titleLen={candidate.WindowTitleLength} exe=\"{SanitizeForLog(candidate.ExePath)}\" monitorSized={(candidate.IsMonitorSized ? 1 : 0)}.");
                }

                bestCandidate = candidate;

                if (candidate.IsMonitorSized)
                {
                    return new LauncherTargetResolutionResult(
                        true,
                        candidate.ProcessId,
                        candidate.Hwnd,
                        candidate.ProcessName,
                        candidate.ExePath,
                        candidate.WindowClass,
                        candidate.WindowTitle,
                        candidate.Width,
                        candidate.Height,
                        true,
                        "discovery_foreground_monitor_sized");
                }
            }

            await Task.Delay(DiscoveryPollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (bestCandidate != null)
        {
            var candidate = bestCandidate.Value;
            return new LauncherTargetResolutionResult(
                true,
                candidate.ProcessId,
                candidate.Hwnd,
                candidate.ProcessName,
                candidate.ExePath,
                candidate.WindowClass,
                candidate.WindowTitle,
                candidate.Width,
                candidate.Height,
                false,
                "discovery_foreground_window");
        }

        return LauncherTargetResolutionResult.Failed("discovery_timeout");
    }

    private static ObservedWindowCandidate? TryGetForegroundWindowCandidate(string expectedExeName, string? expectedExePath)
    {
        foreach (var window in LauncherTargetResolverSupport.EnumerateTopLevelWindows())
        {
            if (!window.IsForeground)
            {
                continue;
            }

            using var process = TryGetProcess(window.ProcessId);
            if (process == null)
            {
                return null;
            }

            var exePath = TryGetProcessPath(process);
            if (!MatchesExpectedTarget(process.ProcessName, exePath, expectedExeName, expectedExePath))
            {
                return null;
            }

            return new ObservedWindowCandidate(
                window.Hwnd,
                window.ProcessId,
                process.ProcessName,
                exePath,
                window.WindowClass,
                window.WindowClassLength,
                window.WindowTitle,
                window.WindowTitleLength,
                window.Width,
                window.Height,
                true,
                window.IsMonitorSized,
                false,
                false,
                "discovery_foreground_match");
        }

        return null;
    }

    private static bool MatchesExpectedTarget(
        string processName,
        string exePath,
        string expectedExeName,
        string? expectedExePath)
    {
        if (!string.IsNullOrWhiteSpace(expectedExePath) &&
            !string.IsNullOrWhiteSpace(exePath) &&
            string.Equals(exePath, expectedExePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(processName, expectedExeName, StringComparison.OrdinalIgnoreCase);
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SanitizeForLog(string value)
    {
        return (value ?? string.Empty).Replace('"', '\'');
    }
}

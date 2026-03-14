using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Hook;

internal sealed class LauncherTargetResolver
{
    private static readonly TimeSpan FastPathPollInterval = TimeSpan.FromMilliseconds(50);
    private readonly Func<AppLogger?> _loggerAccessor;

    public LauncherTargetResolver(Func<AppLogger?> loggerAccessor)
    {
        _loggerAccessor = loggerAccessor;
    }

    public async Task<LauncherTargetResolutionResult> ResolveAsync(
        GraphicsHookLauncherTargetSignature signature,
        string expectedExeName,
        string? expectedExePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signature);

        var seenPids = new HashSet<int>();
        var startedAtUtc = DateTime.UtcNow;
        _loggerAccessor()?.Info(
            $"stage=graphics_hook event=signature_reuse_start exe=\"{SanitizeForLog(expectedExeName)}\" timeoutMs={(int)timeout.TotalMilliseconds}.");

        while (DateTime.UtcNow - startedAtUtc < timeout && !cancellationToken.IsCancellationRequested)
        {
            var processes = CaptureMatchingProcesses(signature, expectedExeName, expectedExePath);
            foreach (var process in processes)
            {
                if (!seenPids.Add(process.ProcessId))
                {
                    continue;
                }

                _loggerAccessor()?.Info(
                    $"stage=graphics_hook event=signature_candidate pid={process.ProcessId} exe=\"{SanitizeForLog(process.ExePath)}\" reason={process.MatchReason}.");
            }

            var windowCandidates = EnumerateMatchingWindows(processes, signature);
            var bestWindow = SelectBestWindow(windowCandidates, signature);
            if (bestWindow != null)
            {
                return new LauncherTargetResolutionResult(
                    true,
                    bestWindow.Value.ProcessId,
                    bestWindow.Value.Hwnd,
                    bestWindow.Value.ProcessName,
                    bestWindow.Value.ExePath,
                    bestWindow.Value.WindowClass,
                    bestWindow.Value.WindowTitle,
                    bestWindow.Value.Width,
                    bestWindow.Value.Height,
                    true,
                    bestWindow.Value.MatchReason);
            }

            await Task.Delay(FastPathPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return LauncherTargetResolutionResult.Failed("signature_resolve_timeout");
    }

    private List<ObservedProcessCandidate> CaptureMatchingProcesses(
        GraphicsHookLauncherTargetSignature signature,
        string expectedExeName,
        string? expectedExePath)
    {
        var matches = new List<ObservedProcessCandidate>();
        foreach (var process in Process.GetProcessesByName(expectedExeName))
        {
            try
            {
                var exePath = TryGetProcessPath(process);
                var pathMatches = !string.IsNullOrWhiteSpace(signature.ExePathSuffix) &&
                    !string.IsNullOrWhiteSpace(exePath) &&
                    exePath.EndsWith(signature.ExePathSuffix, StringComparison.OrdinalIgnoreCase);
                var expectedPathMatches = !string.IsNullOrWhiteSpace(expectedExePath) &&
                    !string.IsNullOrWhiteSpace(exePath) &&
                    string.Equals(exePath, expectedExePath, StringComparison.OrdinalIgnoreCase);
                var reason = expectedPathMatches
                    ? "signature_expected_path_match"
                    : pathMatches
                        ? "signature_path_suffix_match"
                        : "signature_name_match";
                matches.Add(new ObservedProcessCandidate(process.Id, process.ProcessName, exePath, reason));
            }
            catch
            {
                // WHY: Launcher fast-path polling is best-effort; inaccessible processes should not abort matching.
            }
            finally
            {
                process.Dispose();
            }
        }

        return matches;
    }

    private static List<ObservedWindowCandidate> EnumerateMatchingWindows(
        List<ObservedProcessCandidate> processes,
        GraphicsHookLauncherTargetSignature signature)
    {
        var processByPid = new Dictionary<int, ObservedProcessCandidate>();
        foreach (var process in processes)
        {
            processByPid[process.ProcessId] = process;
        }

        var candidates = new List<ObservedWindowCandidate>();
        foreach (var window in LauncherTargetResolverSupport.EnumerateTopLevelWindows())
        {
            if (!processByPid.TryGetValue(window.ProcessId, out var process))
            {
                continue;
            }

            var classMatch = MatchesAny(window.WindowClass, signature.WindowClassAllowList);
            var titleMatch = ContainsAny(window.WindowTitle, signature.WindowTitleContainsAny);
            candidates.Add(new ObservedWindowCandidate(
                window.Hwnd,
                window.ProcessId,
                process.ProcessName,
                process.ExePath,
                window.WindowClass,
                window.WindowTitle,
                window.Width,
                window.Height,
                window.IsForeground,
                window.IsMonitorSized,
                classMatch,
                titleMatch,
                process.MatchReason));
        }

        return candidates;
    }

    private static ObservedWindowCandidate? SelectBestWindow(
        List<ObservedWindowCandidate> candidates,
        GraphicsHookLauncherTargetSignature signature)
    {
        ObservedWindowCandidate? best = null;
        var bestScore = int.MinValue;

        foreach (var candidate in candidates)
        {
            var score = 0;
            if (candidate.IsMonitorSized)
            {
                score += 100;
            }

            if (candidate.ClassMatch)
            {
                score += 80;
            }

            if (candidate.TitleMatch)
            {
                score += 40;
            }

            if (candidate.IsForeground)
            {
                score += 20;
            }

            if (candidate.Width >= signature.MinClientWidth && candidate.Height >= signature.MinClientHeight)
            {
                score += 10;
            }

            if (score <= bestScore)
            {
                continue;
            }

            best = candidate with
            {
                MatchReason = candidate.ClassMatch || candidate.TitleMatch
                    ? "signature_window_match"
                    : "signature_monitor_sized_window_match"
            };
            bestScore = score;
        }

        return bestScore >= 100 ? best : null;
    }

    private static bool MatchesAny(string value, IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (!IsUsableMetadataValue(candidate))
            {
                continue;
            }

            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAny(string value, IReadOnlyList<string> fragments)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var fragment in fragments)
        {
            if (!IsUsableMetadataValue(fragment))
            {
                continue;
            }

            if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static bool IsUsableMetadataValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }
}

internal readonly record struct LauncherTargetResolutionResult(
    bool Success,
    int ProcessId,
    IntPtr Hwnd,
    string ProcessName,
    string ExePath,
    string WindowClass,
    string WindowTitle,
    int Width,
    int Height,
    bool MonitorSizedWindowObserved,
    string Reason)
{
    public static LauncherTargetResolutionResult Failed(string reason)
    {
        return new LauncherTargetResolutionResult(
            false,
            0,
            IntPtr.Zero,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0,
            false,
            reason);
    }
}

internal static class LauncherTargetResolverSupport
{
    public static List<ObservedTopLevelWindow> EnumerateTopLevelWindows()
    {
        var foreground = GetForegroundWindow();
        var windows = new List<ObservedTopLevelWindow>();
        EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || !IsWindowVisible(hWnd) || IsIconic(hWnd))
            {
                return true;
            }

            if (GetWindow(hWnd, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            if (GetAncestor(hWnd, GaRoot) != hWnd)
            {
                return true;
            }

            if (!GetWindowRect(hWnd, out var rect))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var nativePid);
            windows.Add(new ObservedTopLevelWindow(
                hWnd,
                unchecked((int)nativePid),
                GetClassNameValue(hWnd),
                GetWindowTitleValue(hWnd),
                Math.Max(0, rect.Right - rect.Left),
                Math.Max(0, rect.Bottom - rect.Top),
                hWnd == foreground,
                IsMonitorSizedWindow(rect)));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static bool IsMonitorSizedWindow(Rect rect)
    {
        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        return rect.Left == info.rcMonitor.Left &&
               rect.Top == info.rcMonitor.Top &&
               rect.Right == info.rcMonitor.Right &&
               rect.Bottom == info.rcMonitor.Bottom;
    }

    private static string GetClassNameValue(IntPtr hWnd)
    {
        var builder = new StringBuilder(256);
        _ = GetClassNameW(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetWindowTitleValue(IntPtr hWnd)
    {
        var length = GetWindowTextLengthW(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowTextW(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    internal readonly record struct ObservedTopLevelWindow(
        IntPtr Hwnd,
        int ProcessId,
        string WindowClass,
        string WindowTitle,
        int Width,
        int Height,
        bool IsForeground,
        bool IsMonitorSized);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect lprc, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
}

internal readonly record struct ObservedProcessCandidate(
    int ProcessId,
    string ProcessName,
    string ExePath,
    string MatchReason);

internal readonly record struct ObservedWindowCandidate(
    IntPtr Hwnd,
    int ProcessId,
    string ProcessName,
    string ExePath,
    string WindowClass,
    string WindowTitle,
    int Width,
    int Height,
    bool IsForeground,
    bool IsMonitorSized,
    bool ClassMatch,
    bool TitleMatch,
    string MatchReason);

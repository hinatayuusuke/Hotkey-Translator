using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hotkey_Translator.Services.Hook;

internal sealed class GraphicsHookLauncherProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan FastProbeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SlowProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FastProbeWindow = TimeSpan.FromSeconds(2);
    private readonly Func<AppLogger?> _loggerAccessor;

    public GraphicsHookLauncherProbeService(Func<AppLogger?> loggerAccessor)
    {
        _loggerAccessor = loggerAccessor;
    }

    public async Task ProbeAsync(int bootstrapPid, string? expectedProcessName, CancellationToken cancellationToken)
    {
        if (bootstrapPid <= 0)
        {
            return;
        }

        var normalizedExpectedName = (expectedProcessName ?? string.Empty).Trim();
        var seenChildren = new HashSet<int>();
        var seenExpectedNamePids = new HashSet<int>();
        var seenWindows = new HashSet<nint>();
        WindowCandidate? bestCandidate = null;
        var startedAtUtc = DateTime.UtcNow;

        _loggerAccessor()?.Info(
            $"stage=graphics_hook event=launcher_probe_start bootstrapPid={bootstrapPid} expected=\"{SanitizeForLog(normalizedExpectedName)}\" timeoutMs={(int)ProbeTimeout.TotalMilliseconds}.");

        while (DateTime.UtcNow - startedAtUtc < ProbeTimeout && !cancellationToken.IsCancellationRequested)
        {
            var processes = CaptureProcesses();
            var descendants = BuildDescendantSet(bootstrapPid, processes);
            var expectedNamePids = CaptureExpectedNamePids(normalizedExpectedName);

            foreach (var pid in descendants)
            {
                if (pid == bootstrapPid || !seenChildren.Add(pid))
                {
                    continue;
                }

                if (processes.TryGetValue(pid, out var processInfo))
                {
                    _loggerAccessor()?.Info(
                        $"stage=graphics_hook event=launcher_probe_child bootstrapPid={bootstrapPid} pid={pid} parentPid={processInfo.ParentPid} name=\"{SanitizeForLog(processInfo.ExeFile)}\".");
                }
            }

            foreach (var pid in expectedNamePids)
            {
                if (!seenExpectedNamePids.Add(pid))
                {
                    continue;
                }

                var parentPid = processes.TryGetValue(pid, out var processInfo)
                    ? processInfo.ParentPid
                    : 0;
                _loggerAccessor()?.Info(
                    $"stage=graphics_hook event=launcher_probe_name_match bootstrapPid={bootstrapPid} pid={pid} parentPid={parentPid} expected=\"{SanitizeForLog(normalizedExpectedName)}\" descendant={BoolToInt(descendants.Contains(pid))}.");
            }

            foreach (var window in EnumerateTopLevelWindows())
            {
                var isDescendant = descendants.Contains(window.ProcessId);
                var expectedNameMatch =
                    expectedNamePids.Contains(window.ProcessId) ||
                    (!string.IsNullOrWhiteSpace(normalizedExpectedName) &&
                     processes.TryGetValue(window.ProcessId, out var processInfo) &&
                     string.Equals(
                         StripExtension(processInfo.ExeFile),
                         normalizedExpectedName,
                         StringComparison.OrdinalIgnoreCase));

                if (!isDescendant && !expectedNameMatch)
                {
                    continue;
                }

                if (seenWindows.Add(window.Hwnd))
                {
                    _loggerAccessor()?.Info(
                        $"stage=graphics_hook event=launcher_probe_window bootstrapPid={bootstrapPid} pid={window.ProcessId} hwnd=0x{window.Hwnd.ToInt64():X} visible={BoolToInt(window.Visible)} owner=0x{window.Owner.ToInt64():X} class=\"{SanitizeForLog(window.ClassName)}\" title=\"{SanitizeForLog(window.Title)}\" rect=[{window.Left},{window.Top},{window.Width},{window.Height}] descendant={BoolToInt(isDescendant)} expectedNameMatch={BoolToInt(expectedNameMatch)}.");
                }

                var candidate = BuildCandidate(window, bootstrapPid, isDescendant, expectedNameMatch);
                if (bestCandidate == null || candidate.Score > bestCandidate.Value.Score)
                {
                    bestCandidate = candidate;
                }
            }

            var elapsed = DateTime.UtcNow - startedAtUtc;
            var delay = elapsed < FastProbeWindow ? FastProbeInterval : SlowProbeInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (bestCandidate == null)
        {
            _loggerAccessor()?.Info(
                $"stage=graphics_hook event=launcher_probe_result bootstrapPid={bootstrapPid} result=no_window descendants={Math.Max(0, seenChildren.Count)} expectedNamePids={Math.Max(0, seenExpectedNamePids.Count)}.");
            return;
        }

        var best = bestCandidate.Value;
        _loggerAccessor()?.Info(
            $"stage=graphics_hook event=launcher_probe_result bootstrapPid={bootstrapPid} selectedPid={best.ProcessId} hwnd=0x{best.Hwnd.ToInt64():X} score={best.Score} reason={best.Reason} title=\"{SanitizeForLog(best.Title)}\" class=\"{SanitizeForLog(best.ClassName)}\".");

        if (best.ProcessId != bootstrapPid)
        {
            _loggerAccessor()?.Info(
                $"stage=graphics_hook event=launcher_probe_mismatch attachPid={bootstrapPid} windowPid={best.ProcessId} hwnd=0x{best.Hwnd.ToInt64():X} severity=high reason={best.Reason}.");
        }
    }

    private static WindowCandidate BuildCandidate(
        WindowInfo window,
        int bootstrapPid,
        bool isDescendant,
        bool expectedNameMatch)
    {
        var score = 0;
        if (window.ProcessId == bootstrapPid)
        {
            score += 40;
        }

        if (isDescendant)
        {
            score += 100;
        }

        if (expectedNameMatch)
        {
            score += 40;
        }

        if (window.Visible)
        {
            score += 20;
        }

        if (window.Width > 0 && window.Height > 0)
        {
            score += 20;
        }

        if (window.Owner == IntPtr.Zero)
        {
            score += 10;
        }

        var reason = isDescendant
            ? (window.ProcessId == bootstrapPid ? "bootstrap_window" : "descendant_window")
            : "expected_name_window";

        return new WindowCandidate(window.Hwnd, window.ProcessId, window.Title, window.ClassName, score, reason);
    }

    private static Dictionary<int, ProcessInfo> CaptureProcesses()
    {
        var result = new Dictionary<int, ProcessInfo>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
            };

            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                var pid = unchecked((int)entry.th32ProcessID);
                result[pid] = new ProcessInfo(
                    ParentPid: unchecked((int)entry.th32ParentProcessID),
                    ExeFile: entry.szExeFile ?? string.Empty);
                entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    private static HashSet<int> BuildDescendantSet(int rootPid, IReadOnlyDictionary<int, ProcessInfo> processes)
    {
        var descendants = new HashSet<int> { rootPid };
        var pending = new Queue<int>();
        pending.Enqueue(rootPid);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var pair in processes)
            {
                if (pair.Value.ParentPid != current || !descendants.Add(pair.Key))
                {
                    continue;
                }

                pending.Enqueue(pair.Key);
            }
        }

        return descendants;
    }

    private static HashSet<int> CaptureExpectedNamePids(string normalizedExpectedName)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(normalizedExpectedName))
        {
            return result;
        }

        try
        {
            foreach (var process in Process.GetProcessesByName(normalizedExpectedName))
            {
                try
                {
                    result.Add(process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // WHY: Probe is diagnostics only. Failure to enumerate by name should not abort launcher flow.
        }

        return result;
    }

    private static List<WindowInfo> EnumerateTopLevelWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out var nativePid);
            _ = GetWindowRect(hWnd, out var rect);
            windows.Add(new WindowInfo(
                Hwnd: (nint)hWnd,
                ProcessId: unchecked((int)nativePid),
                Title: GetWindowTextValue(hWnd),
                ClassName: GetClassNameValue(hWnd),
                Visible: IsWindowVisible(hWnd),
                Left: rect.Left,
                Top: rect.Top,
                Width: Math.Max(0, rect.Right - rect.Left),
                Height: Math.Max(0, rect.Bottom - rect.Top),
                Owner: (nint)GetWindow(hWnd, GwOwner)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string GetWindowTextValue(IntPtr hWnd)
    {
        var builder = new StringBuilder(512);
        _ = GetWindowTextW(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetClassNameValue(IntPtr hWnd)
    {
        var builder = new StringBuilder(256);
        _ = GetClassNameW(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string StripExtension(string exeFile)
    {
        if (string.IsNullOrWhiteSpace(exeFile))
        {
            return string.Empty;
        }

        return exeFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeFile[..^4]
            : exeFile;
    }

    private static string SanitizeForLog(string value)
    {
        return (value ?? string.Empty).Replace('"', '\'');
    }

    private static int BoolToInt(bool value)
    {
        return value ? 1 : 0;
    }

    private readonly record struct ProcessInfo(int ParentPid, string ExeFile);

    private readonly record struct WindowInfo(
        nint Hwnd,
        int ProcessId,
        string Title,
        string ClassName,
        bool Visible,
        int Left,
        int Top,
        int Width,
        int Height,
        nint Owner);

    private readonly record struct WindowCandidate(
        nint Hwnd,
        int ProcessId,
        string Title,
        string ClassName,
        int Score,
        string Reason);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private const uint Th32csSnapProcess = 0x00000002;
    private const uint GwOwner = 4;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}

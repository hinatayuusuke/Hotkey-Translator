[CmdletBinding()]
param(
    [int]$ProcessId,
    [string]$ProcessName,
    [string]$TitleContains,
    [switch]$IncludeInvisible
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class TopLevelWindowProbe
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public sealed class WindowInfo
    {
        public IntPtr Hwnd { get; set; }
        public int ProcessId { get; set; }
        public bool Visible { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public IntPtr Owner { get; set; }
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError=true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError=true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError=true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_OWNER = 4;

    public static List<WindowInfo> Enumerate()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hWnd, lParam) =>
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);

            var title = new StringBuilder(512);
            GetWindowText(hWnd, title, title.Capacity);

            var className = new StringBuilder(256);
            GetClassName(hWnd, className, className.Capacity);

            RECT rect;
            var hasRect = GetWindowRect(hWnd, out rect);
            var width = hasRect ? Math.Max(0, rect.Right - rect.Left) : 0;
            var height = hasRect ? Math.Max(0, rect.Bottom - rect.Top) : 0;

            result.Add(new WindowInfo
            {
                Hwnd = hWnd,
                ProcessId = unchecked((int)pid),
                Visible = IsWindowVisible(hWnd),
                Title = title.ToString(),
                ClassName = className.ToString(),
                Left = hasRect ? rect.Left : 0,
                Top = hasRect ? rect.Top : 0,
                Width = width,
                Height = height,
                Owner = GetWindow(hWnd, GW_OWNER)
            });

            return true;
        }, IntPtr.Zero);

        return result;
    }
}
'@

$windows = [TopLevelWindowProbe]::Enumerate()

$rows = foreach ($window in $windows) {
    if (-not $IncludeInvisible -and -not $window.Visible) {
        continue
    }

    if ($ProcessId -gt 0 -and $window.ProcessId -ne $ProcessId) {
        continue
    }

    $process = $null
    $resolvedProcessName = ''
    try {
        $process = Get-Process -Id $window.ProcessId -ErrorAction Stop
        $resolvedProcessName = $process.ProcessName
    }
    catch {
        $resolvedProcessName = '<exited>'
    }

    if (-not [string]::IsNullOrWhiteSpace($ProcessName) -and
        $resolvedProcessName -notlike "*$ProcessName*") {
        continue
    }

    if (-not [string]::IsNullOrWhiteSpace($TitleContains) -and
        $window.Title -notlike "*$TitleContains*") {
        continue
    }

    [pscustomobject]@{
        HWND = ('0x{0:X}' -f $window.Hwnd.ToInt64())
        PID = $window.ProcessId
        ProcessName = $resolvedProcessName
        Visible = $window.Visible
        Title = $window.Title
        ClassName = $window.ClassName
        Rect = ('[{0},{1},{2},{3}]' -f $window.Left, $window.Top, $window.Width, $window.Height)
        Owner = ('0x{0:X}' -f $window.Owner.ToInt64())
        MainWindowHandle = if ($process -ne $null) { '0x{0:X}' -f $process.MainWindowHandle.ToInt64() } else { '0x0' }
    }
}

# WHY: Stable sort makes it easier to compare launcher PID and final window PID across repeated runs.
$rows |
    Sort-Object PID, HWND |
    Format-Table -AutoSize

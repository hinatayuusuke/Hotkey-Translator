using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class WindowBindingService
{
    public bool TryBindForegroundWindow(AppSettings settings, out FixedCaptureWindowSpec spec, out string? reason)
    {
        spec = new FixedCaptureWindowSpec();
        reason = null;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            reason = "No foreground window.";
            return false;
        }

        if (!TryGetProcessId(hwnd, out var processId))
        {
            reason = "Failed to get process ID of the foreground window.";
            return false;
        }

        if (processId == Environment.ProcessId)
        {
            reason = "Foreground window is Hotkey-Translator itself.";
            return false;
        }

        if (!TryBuildSpec(hwnd, out spec, out reason))
        {
            return false;
        }

        ApplySpec(settings, spec);
        settings.EnableFixedCaptureWindow = true;
        return true;
    }

    public void ClearBinding(AppSettings settings)
    {
        settings.EnableFixedCaptureWindow = false;
        settings.FixedCaptureWindowHandle = 0;
        settings.FixedCaptureWindowProcessId = 0;
        settings.FixedCaptureWindowProcessName = string.Empty;
        settings.FixedCaptureWindowClassName = string.Empty;
        settings.FixedCaptureWindowTitle = string.Empty;
    }

    public bool TryResolveWindowHandle(AppSettings settings, out IntPtr hwnd, out string? reason)
    {
        hwnd = IntPtr.Zero;
        reason = null;

        if (!settings.EnableFixedCaptureWindow || settings.CaptureMode != CaptureMode.ActiveWindow)
        {
            reason = "Fixed capture window is disabled.";
            return false;
        }

        var hasMetadata = settings.FixedCaptureWindowProcessId > 0 ||
                          !string.IsNullOrWhiteSpace(settings.FixedCaptureWindowProcessName) ||
                          !string.IsNullOrWhiteSpace(settings.FixedCaptureWindowClassName) ||
                          !string.IsNullOrWhiteSpace(settings.FixedCaptureWindowTitle);

        var stored = new IntPtr(settings.FixedCaptureWindowHandle);
        var storedUsable = stored != IntPtr.Zero &&
                           IsWindow(stored) &&
                           MatchesStoredIdentity(stored, settings) &&
                           IsCaptureWindowViable(stored, out _);
        if (storedUsable)
        {
            // WHY: Some titles recreate HWND on fullscreen transitions while leaving stale windows alive.
            // Prefer metadata re-resolution when a better top-level candidate exists.
            if (hasMetadata &&
                TryFindWindowByMetadata(settings, out var refreshed) &&
                refreshed != IntPtr.Zero &&
                refreshed != stored)
            {
                hwnd = refreshed;
                if (TryBuildSpec(refreshed, out var refreshedSpec, out _))
                {
                    ApplySpec(settings, refreshedSpec);
                }
                else
                {
                    settings.FixedCaptureWindowHandle = refreshed.ToInt64();
                }

                return true;
            }

            hwnd = stored;
            return true;
        }

        if (!hasMetadata)
        {
            reason = "Fixed capture metadata is empty.";
            return false;
        }

        if (TryFindWindowByMetadata(settings, out var resolved))
        {
            hwnd = resolved;
            if (TryBuildSpec(resolved, out var spec, out _))
            {
                // WHY: Keep metadata fresh because hwnd can be recreated after app-side reloads.
                ApplySpec(settings, spec);
            }
            else
            {
                settings.FixedCaptureWindowHandle = resolved.ToInt64();
            }

            return true;
        }

        reason = "Stored target is invalid and metadata lookup failed.";
        return false;
    }

    private static bool MatchesStoredIdentity(IntPtr hwnd, AppSettings settings)
    {
        if (settings.FixedCaptureWindowProcessId > 0 && (!TryGetProcessId(hwnd, out var processId) || processId != settings.FixedCaptureWindowProcessId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(settings.FixedCaptureWindowClassName))
        {
            var className = GetWindowClassName(hwnd);
            if (!string.Equals(className, settings.FixedCaptureWindowClassName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryFindWindowByMetadata(AppSettings settings, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        var bestScore = int.MinValue;
        var bestTopLevel = false;
        var bestClientArea = -1.0;
        var bestHandle = IntPtr.Zero;

        _ = EnumWindows((window, _) =>
        {
            if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window))
            {
                return true;
            }

            if (!TryGetProcessId(window, out var processId))
            {
                return true;
            }

            if (!IsCaptureWindowViable(window, out var clientRect))
            {
                return true;
            }

            var score = 0;
            if (settings.FixedCaptureWindowProcessId > 0)
            {
                if (processId != settings.FixedCaptureWindowProcessId)
                {
                    return true;
                }

                score += 8;
            }

            var className = GetWindowClassName(window);
            if (!string.IsNullOrWhiteSpace(settings.FixedCaptureWindowClassName))
            {
                if (!string.Equals(className, settings.FixedCaptureWindowClassName, StringComparison.Ordinal))
                {
                    return true;
                }

                score += 4;
            }

            var title = GetWindowTitle(window);
            if (!string.IsNullOrWhiteSpace(settings.FixedCaptureWindowTitle))
            {
                if (!string.Equals(title, settings.FixedCaptureWindowTitle, StringComparison.Ordinal))
                {
                    return true;
                }

                score += 2;
            }

            if (!string.IsNullOrWhiteSpace(settings.FixedCaptureWindowProcessName))
            {
                var processName = GetProcessName(processId);
                if (!string.Equals(processName, settings.FixedCaptureWindowProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                score += 1;
            }

            var isTopLevel = IsTopLevelWindow(window);
            var clientArea = clientRect.Width * clientRect.Height;
            if (score > bestScore ||
                (score == bestScore && isTopLevel && !bestTopLevel) ||
                (score == bestScore && isTopLevel == bestTopLevel && clientArea > bestClientArea))
            {
                bestScore = score;
                bestTopLevel = isTopLevel;
                bestClientArea = clientArea;
                bestHandle = window;
            }

            return true;
        }, IntPtr.Zero);

        hwnd = bestHandle;
        return hwnd != IntPtr.Zero;
    }

    private static bool IsCaptureWindowViable(IntPtr hwnd, out System.Windows.Rect clientRect)
    {
        clientRect = default;
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        if (!TryGetClientScreenRect(hwnd, out clientRect))
        {
            return false;
        }

        var virtualScreen = GetVirtualScreenRect();
        return !System.Windows.Rect.Intersect(clientRect, virtualScreen).IsEmpty;
    }

    private static bool IsTopLevelWindow(IntPtr hwnd)
    {
        // WHY: Fullscreen transitions may leave stale helper/owned windows behind; prefer root top-level windows.
        return GetAncestor(hwnd, GaRoot) == hwnd && GetWindow(hwnd, GwOwner) == IntPtr.Zero;
    }

    private static bool TryGetClientScreenRect(IntPtr hwnd, out System.Windows.Rect rect)
    {
        rect = default;
        if (!GetClientRect(hwnd, out var client))
        {
            return false;
        }

        var w = client.Right - client.Left;
        var h = client.Bottom - client.Top;
        if (w <= 0 || h <= 0)
        {
            return false;
        }

        var tl = new NativePoint(0, 0);
        var br = new NativePoint(w, h);
        if (!ClientToScreen(hwnd, ref tl) || !ClientToScreen(hwnd, ref br))
        {
            return false;
        }

        var outW = br.X - tl.X;
        var outH = br.Y - tl.Y;
        if (outW <= 0 || outH <= 0)
        {
            return false;
        }

        rect = new System.Windows.Rect(tl.X, tl.Y, outW, outH);
        return true;
    }

    private static System.Windows.Rect GetVirtualScreenRect()
    {
        var left = GetSystemMetrics(SystemMetric.XVirtualScreen);
        var top = GetSystemMetrics(SystemMetric.YVirtualScreen);
        var width = GetSystemMetrics(SystemMetric.CxVirtualScreen);
        var height = GetSystemMetrics(SystemMetric.CyVirtualScreen);
        return new System.Windows.Rect(left, top, Math.Max(0, width), Math.Max(0, height));
    }

    private static bool TryBuildSpec(IntPtr hwnd, out FixedCaptureWindowSpec spec, out string? reason)
    {
        spec = new FixedCaptureWindowSpec();
        reason = null;

        if (!TryGetProcessId(hwnd, out var processId))
        {
            reason = "Failed to get process ID.";
            return false;
        }

        var className = GetWindowClassName(hwnd);
        var title = GetWindowTitle(hwnd);
        var processName = GetProcessName(processId);

        spec = new FixedCaptureWindowSpec
        {
            Hwnd = hwnd.ToInt64(),
            ProcessId = processId,
            ProcessName = processName,
            ClassName = className,
            WindowTitle = title
        };
        return true;
    }

    private static void ApplySpec(AppSettings settings, FixedCaptureWindowSpec spec)
    {
        settings.FixedCaptureWindowHandle = spec.Hwnd;
        settings.FixedCaptureWindowProcessId = spec.ProcessId;
        settings.FixedCaptureWindowProcessName = spec.ProcessName;
        settings.FixedCaptureWindowClassName = spec.ClassName;
        settings.FixedCaptureWindowTitle = spec.WindowTitle;
    }

    private static bool TryGetProcessId(IntPtr hwnd, out int processId)
    {
        _ = GetWindowThreadProcessId(hwnd, out var rawProcessId);
        processId = unchecked((int)rawProcessId);
        return processId > 0;
    }

    private static string GetProcessName(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(512);
        _ = GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(SystemMetric smIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private const uint GaRoot = 2;
    private const uint GwOwner = 4;

    private enum SystemMetric
    {
        XVirtualScreen = 76,
        YVirtualScreen = 77,
        CxVirtualScreen = 78,
        CyVirtualScreen = 79
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}

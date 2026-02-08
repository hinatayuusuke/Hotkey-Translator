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

        var stored = new IntPtr(settings.FixedCaptureWindowHandle);
        if (stored != IntPtr.Zero && IsWindow(stored) && MatchesStoredIdentity(stored, settings))
        {
            hwnd = stored;
            return true;
        }

        if (settings.FixedCaptureWindowProcessId <= 0 &&
            string.IsNullOrWhiteSpace(settings.FixedCaptureWindowProcessName) &&
            string.IsNullOrWhiteSpace(settings.FixedCaptureWindowClassName) &&
            string.IsNullOrWhiteSpace(settings.FixedCaptureWindowTitle))
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

            if (score > bestScore)
            {
                bestScore = score;
                bestHandle = window;
            }

            return true;
        }, IntPtr.Zero);

        hwnd = bestHandle;
        return hwnd != IntPtr.Zero;
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
}

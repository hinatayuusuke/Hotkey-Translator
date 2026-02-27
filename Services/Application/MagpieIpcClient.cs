using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hotkey_Translator.Services.Application;

internal interface IMagpieIpcClient
{
    bool TryStartScaling(long targetHwnd, int profileIndex, bool windowedMode, out string? reason);
    bool TryStopScaling(out string? reason);
    bool TryExit(out string? reason);
    bool TryWaitCoreWindow(TimeSpan timeout, out nint coreHwnd);
}

internal sealed class MagpieIpcClient : IMagpieIpcClient
{
    private const string CoreWindowClassName = "WNDCLS_Magpie_Core_CLI_Message";
    private const int SendTimeoutMs = 2000;
    private const uint SendMessageFlags = 0x0002; // SMTO_ABORTIFHUNG

    public bool TryStartScaling(long targetHwnd, int profileIndex, bool windowedMode, out string? reason)
    {
        var messageName = windowedMode
            ? "Magpie_Core_CLI_Message_Start_WindowedMode"
            : "Magpie_Core_CLI_Message_Start";
        return TrySendCoreMessage(
            messageName,
            new UIntPtr(unchecked((uint)Math.Max(0, profileIndex))),
            new IntPtr(targetHwnd),
            out reason);
    }

    public bool TryStopScaling(out string? reason)
    {
        return TrySendCoreMessage("Magpie_Core_CLI_Message_Stop", UIntPtr.Zero, IntPtr.Zero, out reason);
    }

    public bool TryExit(out string? reason)
    {
        return TrySendCoreMessage("Magpie_Core_CLI_Message_Exit", UIntPtr.Zero, IntPtr.Zero, out reason);
    }

    public bool TryWaitCoreWindow(TimeSpan timeout, out nint coreHwnd)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed <= timeout)
        {
            var found = FindWindow(CoreWindowClassName, null);
            if (found != IntPtr.Zero)
            {
                coreHwnd = found;
                return true;
            }

            Thread.Sleep(50);
        }

        coreHwnd = IntPtr.Zero;
        return false;
    }

    private bool TrySendCoreMessage(
        string messageName,
        UIntPtr wParam,
        IntPtr lParam,
        out string? reason)
    {
        reason = null;
        var coreWindow = FindWindow(CoreWindowClassName, null);
        if (coreWindow == IntPtr.Zero)
        {
            reason = "Magpie core message window not found.";
            return false;
        }

        var messageId = RegisterWindowMessage(messageName);
        if (messageId == 0)
        {
            reason = $"RegisterWindowMessage failed: {messageName}.";
            return false;
        }

        if (!SendMessageTimeout(
                coreWindow,
                messageId,
                wParam,
                lParam,
                SendMessageFlags,
                SendTimeoutMs,
                out _))
        {
            var error = Marshal.GetLastWin32Error();
            reason = $"SendMessageTimeout failed (err={error}) for {messageName}.";
            return false;
        }

        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);
}

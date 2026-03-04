using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Hotkey_Translator.Services.Hook;

internal sealed class GraphicsHookLauncherService
{
    private const uint CreateSuspended = 0x00000004;

    public bool TryLaunchSuspended(
        string exePath,
        string? args,
        out SuspendedProcess? process,
        out string? failureReason)
    {
        process = null;
        failureReason = null;

        var normalizedPath = (exePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            failureReason = "exe_path_empty";
            return false;
        }

        if (!File.Exists(normalizedPath))
        {
            failureReason = $"exe_not_found: {normalizedPath}";
            return false;
        }

        var workingDirectory = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            failureReason = $"working_directory_not_resolved: {normalizedPath}";
            return false;
        }

        var commandLine = new StringBuilder(BuildCommandLine(normalizedPath, args));
        var startupInfo = new StartupInfoW
        {
            cb = (uint)Marshal.SizeOf<StartupInfoW>()
        };

        if (!CreateProcessW(
                normalizedPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateSuspended,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInfo))
        {
            failureReason = BuildWin32FailureReason("create_process_failed");
            return false;
        }

        process = SuspendedProcess.Create(processInfo);
        return true;
    }

    private static string BuildCommandLine(string exePath, string? args)
    {
        var trimmedArgs = (args ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmedArgs))
        {
            return Quote(exePath);
        }

        // WHY: Keep argv[0] stable even for paths containing spaces.
        return $"{Quote(exePath)} {trimmedArgs}";
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (value.StartsWith('"') && value.EndsWith('"'))
        {
            return value;
        }

        var escaped = value.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string BuildWin32FailureReason(string prefix)
    {
        var code = Marshal.GetLastWin32Error();
        var message = new Win32Exception(code).Message;
        return $"{prefix}: {message} (code={code})";
    }

    internal sealed class SuspendedProcess : IDisposable
    {
        private IntPtr _processHandle;
        private IntPtr _threadHandle;
        private bool _disposed;

        private SuspendedProcess(ProcessInfoW processInfo)
        {
            ProcessId = checked((int)processInfo.dwProcessId);
            ThreadId = checked((int)processInfo.dwThreadId);
            _processHandle = processInfo.hProcess;
            _threadHandle = processInfo.hThread;
        }

        internal static SuspendedProcess Create(ProcessInfoW processInfo)
        {
            return new SuspendedProcess(processInfo);
        }

        public int ProcessId { get; }

        public int ThreadId { get; }

        public bool Resume(out string? failureReason)
        {
            failureReason = null;
            if (_disposed || _threadHandle == IntPtr.Zero)
            {
                failureReason = "resume_failed: disposed";
                return false;
            }

            var resumed = ResumeThread(_threadHandle);
            if (resumed == uint.MaxValue)
            {
                failureReason = BuildWin32FailureReason("resume_failed");
                return false;
            }

            return true;
        }

        public bool TryTerminate(uint exitCode, out string? failureReason)
        {
            failureReason = null;
            if (_disposed || _processHandle == IntPtr.Zero)
            {
                failureReason = "terminate_failed: disposed";
                return false;
            }

            if (!TerminateProcess(_processHandle, exitCode))
            {
                failureReason = BuildWin32FailureReason("terminate_failed");
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_threadHandle != IntPtr.Zero)
            {
                CloseHandle(_threadHandle);
                _threadHandle = IntPtr.Zero;
            }

            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoW
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInfoW
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoW lpStartupInfo,
        out ProcessInfoW lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

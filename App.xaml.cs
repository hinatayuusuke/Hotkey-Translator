using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Hotkey_Translator;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\HotkeyTranslator.SingleInstance";
    private const int SwRestore = 9;

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            TryActivateExistingInstance();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static void TryActivateExistingInstance()
    {
        using var current = Process.GetCurrentProcess();
        var currentPath = GetProcessPath(current);

        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            try
            {
                if (process.Id == current.Id || process.SessionId != current.SessionId)
                {
                    continue;
                }

                if (!string.Equals(GetProcessPath(process), currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                {
                    continue;
                }

                // WHY: Restoring before foreground activation avoids leaving the existing instance minimized.
                ShowWindowAsync(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
                break;
            }
            catch
            {
                // NOTE: Another process can exit while we enumerate candidates. Best effort is enough here.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string? GetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

using System;
using System.Diagnostics;
using System.IO;

namespace Hotkey_Translator.Services.Application;

internal interface IMagpieProcessService : IDisposable
{
    bool IsRunning { get; }
    bool EnsureStarted(string executablePath, string configPath, out string? reason);
    bool StopProcess(out string? reason);
}

internal sealed class MagpieProcessService : IMagpieProcessService
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public bool EnsureStarted(string executablePath, string configPath, out string? reason)
    {
        reason = null;
        if (IsRunning)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            reason = "Magpie core path is empty.";
            return false;
        }

        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullExecutablePath))
        {
            reason = $"Magpie core not found: {fullExecutablePath}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(configPath))
        {
            reason = "Magpie config path is empty.";
            return false;
        }

        var fullConfigPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullConfigPath))
        {
            reason = $"Magpie config not found: {fullConfigPath}.";
            return false;
        }

        var workingDirectory = Path.GetDirectoryName(fullExecutablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            reason = $"Magpie working directory is invalid: {workingDirectory}.";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullExecutablePath,
            Arguments = $"\"{fullConfigPath}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _process?.Dispose();
            _process = Process.Start(startInfo);
            if (_process == null)
            {
                reason = "Failed to start Magpie.Core process.";
                return false;
            }

            if (_process.HasExited)
            {
                reason = $"Magpie.Core exited immediately with code {_process.ExitCode}.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public bool StopProcess(out string? reason)
    {
        reason = null;
        if (_process == null)
        {
            return true;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }

        return true;
    }

    public void Dispose()
    {
        _ = StopProcess(out _);
    }
}

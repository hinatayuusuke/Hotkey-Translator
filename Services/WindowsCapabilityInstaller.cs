using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hotkey_Translator.Services;

internal enum CapabilityInstallStatus
{
    Succeeded = 0,
    FailedExitCode = 1,
    PermissionDenied = 2,
    UnsupportedEnvironment = 3,
    Canceled = 4
}

internal readonly record struct CapabilityInstallResult(
    CapabilityInstallStatus Status,
    int ExitCode,
    string Command,
    string Output,
    string Error);

internal sealed class WindowsCapabilityInstaller
{
    private const string ElevatorExeName = "WinRtLanguagePackElevator.exe";
    private const string ElevatorDllName = "WinRtLanguagePackElevator.dll";
    private const int ElevatorExitSuccess = 0;
    private const int ElevatorExitInvalidArgs = 2;
    private const int ElevatorExitUnsupported = 3;
    private const int ElevatorExitInstallFailed = 4;

    private readonly Func<AppLogger?> _loggerAccessor;

    public WindowsCapabilityInstaller(Func<AppLogger?> loggerAccessor)
    {
        _loggerAccessor = loggerAccessor;
    }

    public async Task<CapabilityInstallResult> InstallOcrLanguageCapabilityAsync(string localeTag, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.UnsupportedEnvironment,
                -1,
                string.Empty,
                string.Empty,
                "Unsupported OS.");
        }

        var capabilityName = $"Language.OCR~~~{localeTag}~0.0.1.0";
        _loggerAccessor()?.Info($"stage=winrt_ocr_lang_pack event=install_start locale={localeTag} capability={capabilityName}.");
        if (!IsSafeCapabilityName(capabilityName))
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.FailedExitCode,
                -1,
                capabilityName,
                string.Empty,
                "Unsafe capability name.");
        }

        var helperLaunch = ResolveElevatorLaunchInfo();
        if (helperLaunch is null)
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.UnsupportedEnvironment,
                -1,
                Path.Combine(AppContext.BaseDirectory, ElevatorExeName),
                string.Empty,
                $"{ElevatorExeName} / {ElevatorDllName} not found.");
        }

        // WHY: Keep the main process non-elevated and request UAC only for capability install.
        var elevatedResult = await RunElevatedHelperAsync(
                helperLaunch.Value.command,
                helperLaunch.Value.arguments,
                capabilityName,
                cancellationToken)
            .ConfigureAwait(false);
        if (elevatedResult.Status == CapabilityInstallStatus.Succeeded)
        {
            _loggerAccessor()?.Info(
                $"stage=winrt_ocr_lang_pack event=install_ok method=runas locale={localeTag} exit={elevatedResult.ExitCode}.");
            return elevatedResult;
        }

        _loggerAccessor()?.Info(
            $"stage=winrt_ocr_lang_pack event=install_failed locale={localeTag} method=runas exit={elevatedResult.ExitCode}.");

        return elevatedResult;
    }

    private static (string command, string arguments)? ResolveElevatorLaunchInfo()
    {
        var baseDir = AppContext.BaseDirectory;
        var exePath = Path.Combine(baseDir, ElevatorExeName);
        if (File.Exists(exePath))
        {
            return (exePath, string.Empty);
        }

        var dllPath = Path.Combine(baseDir, ElevatorDllName);
        if (File.Exists(dllPath))
        {
            return ("dotnet", $"\"{dllPath}\"");
        }

        return null;
    }

    private static bool IsSafeCapabilityName(string capabilityName)
    {
        if (!capabilityName.StartsWith("Language.OCR~~~", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in capabilityName)
        {
            if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '~')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static async Task<CapabilityInstallResult> RunElevatedHelperAsync(
        string command,
        string commandArguments,
        string capabilityName,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = $"{commandArguments} --capability {capabilityName}".Trim(),
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            }
        };

        try
        {
            if (!process.Start())
            {
                return new CapabilityInstallResult(
                    CapabilityInstallStatus.FailedExitCode,
                    -1,
                    command,
                    string.Empty,
                    "Failed to start elevated helper process.");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode == ElevatorExitSuccess)
            {
                return new CapabilityInstallResult(
                    CapabilityInstallStatus.Succeeded,
                    process.ExitCode,
                    $"{command} {process.StartInfo.Arguments}",
                    string.Empty,
                    string.Empty);
            }

            return new CapabilityInstallResult(
                ClassifyElevatorFailure(process.ExitCode),
                process.ExitCode,
                $"{command} {process.StartInfo.Arguments}",
                string.Empty,
                $"Elevated helper returned exit code {process.ExitCode}.");
        }
        catch (Win32Exception win32Ex) when (win32Ex.NativeErrorCode == 1223)
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.Canceled,
                -1,
                $"{command} {process.StartInfo.Arguments}",
                string.Empty,
                "Elevation consent was canceled by user.");
        }
        catch (OperationCanceledException)
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.Canceled,
                -1,
                $"{command} {process.StartInfo.Arguments}",
                string.Empty,
                "Canceled.");
        }
        catch (Exception ex)
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.FailedExitCode,
                -1,
                $"{command} {process.StartInfo.Arguments}",
                string.Empty,
                ex.Message);
        }
    }

    private static CapabilityInstallStatus ClassifyElevatorFailure(int exitCode)
    {
        return exitCode switch
        {
            ElevatorExitUnsupported => CapabilityInstallStatus.UnsupportedEnvironment,
            ElevatorExitInstallFailed => CapabilityInstallStatus.FailedExitCode,
            ElevatorExitInvalidArgs => CapabilityInstallStatus.FailedExitCode,
            _ => CapabilityInstallStatus.FailedExitCode
        };
    }
}

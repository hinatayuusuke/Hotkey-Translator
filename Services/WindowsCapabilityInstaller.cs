using System;
using System.Diagnostics;
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

        var powershellArgs =
            $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-WindowsCapability -Online -Name '{capabilityName}'\"";
        var psResult = await RunProcessAsync("powershell", powershellArgs, cancellationToken).ConfigureAwait(false);
        if (psResult.Status == CapabilityInstallStatus.Succeeded)
        {
            _loggerAccessor()?.Info(
                $"stage=winrt_ocr_lang_pack event=install_ok method=powershell locale={localeTag} exit={psResult.ExitCode}.");
            return psResult;
        }

        var dismArgs = $"/Online /Add-Capability /CapabilityName:{capabilityName}";
        var dismResult = await RunProcessAsync("dism", dismArgs, cancellationToken).ConfigureAwait(false);
        if (dismResult.Status == CapabilityInstallStatus.Succeeded)
        {
            _loggerAccessor()?.Info(
                $"stage=winrt_ocr_lang_pack event=install_ok method=dism locale={localeTag} exit={dismResult.ExitCode}.");
            return dismResult;
        }

        _loggerAccessor()?.Info(
            $"stage=winrt_ocr_lang_pack event=install_failed locale={localeTag} " +
            $"powershellExit={psResult.ExitCode} dismExit={dismResult.ExitCode}.");
        return dismResult.Status switch
        {
            CapabilityInstallStatus.PermissionDenied => dismResult,
            CapabilityInstallStatus.Canceled => dismResult,
            _ => psResult.Status == CapabilityInstallStatus.PermissionDenied ? psResult : dismResult
        };
    }

    private static async Task<CapabilityInstallResult> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return new CapabilityInstallResult(
                    CapabilityInstallStatus.FailedExitCode,
                    -1,
                    $"{fileName} {arguments}",
                    string.Empty,
                    "Failed to start process.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return new CapabilityInstallResult(
                    CapabilityInstallStatus.Succeeded,
                    process.ExitCode,
                    $"{fileName} {arguments}",
                    output,
                    error);
            }

            var status = ClassifyFailure(process.ExitCode, output, error);
            return new CapabilityInstallResult(
                status,
                process.ExitCode,
                $"{fileName} {arguments}",
                output,
                error);
        }
        catch (OperationCanceledException)
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.Canceled,
                -1,
                $"{fileName} {arguments}",
                string.Empty,
                "Canceled.");
        }
        catch (Exception ex)
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.FailedExitCode,
                -1,
                $"{fileName} {arguments}",
                string.Empty,
                ex.Message);
        }
    }

    private static CapabilityInstallStatus ClassifyFailure(int exitCode, string output, string error)
    {
        var combined = $"{output}\n{error}".ToLowerInvariant();
        if (combined.Contains("0x80070005", StringComparison.Ordinal) ||
            combined.Contains("access is denied", StringComparison.Ordinal) ||
            combined.Contains("requires elevation", StringComparison.Ordinal))
        {
            return CapabilityInstallStatus.PermissionDenied;
        }

        if (exitCode == 740)
        {
            return CapabilityInstallStatus.PermissionDenied;
        }

        return CapabilityInstallStatus.FailedExitCode;
    }
}

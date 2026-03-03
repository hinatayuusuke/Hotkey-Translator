using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
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

internal readonly record struct CapabilityInstallProgress(int Percent, string Message);

internal readonly record struct CapabilityInstallResult(
    CapabilityInstallStatus Status,
    int ExitCode,
    string Command,
    string Output,
    string Error);

internal sealed class WindowsCapabilityInstaller
{
    private const string ElevatorExeName = "WinRtLanguagePackElevator.exe";
    private const int ElevatorExitSuccess = 0;
    private const int ElevatorExitInvalidArgs = 2;
    private const int ElevatorExitUnsupported = 3;
    private const int ElevatorExitInstallFailed = 4;

    private readonly Func<AppLogger?> _loggerAccessor;

    public WindowsCapabilityInstaller(Func<AppLogger?> loggerAccessor)
    {
        _loggerAccessor = loggerAccessor;
    }

    public async Task<CapabilityInstallResult> InstallOcrLanguageCapabilityAsync(
        string localeTag,
        CancellationToken cancellationToken,
        IProgress<CapabilityInstallProgress>? progress = null)
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
        progress?.Report(new CapabilityInstallProgress(0, "Preparing language pack installation..."));
        if (!IsSafeCapabilityName(capabilityName))
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.FailedExitCode,
                -1,
                capabilityName,
                string.Empty,
                "Unsafe capability name.");
        }

        var helperPath = ResolveElevatorPath();
        if (!File.Exists(helperPath))
        {
            return new CapabilityInstallResult(
                CapabilityInstallStatus.UnsupportedEnvironment,
                -1,
                helperPath,
                string.Empty,
                $"{ElevatorExeName} not found.");
        }

        // WHY: Keep the main process non-elevated and request UAC only for capability install.
        var elevatedResult = await RunElevatedHelperAsync(
                helperPath,
                string.Empty,
                capabilityName,
                cancellationToken,
                progress)
            .ConfigureAwait(false);
        if (elevatedResult.Status == CapabilityInstallStatus.Succeeded)
        {
            progress?.Report(new CapabilityInstallProgress(100, "OCR language pack installation completed."));
            _loggerAccessor()?.Info(
                $"stage=winrt_ocr_lang_pack event=install_ok method=runas locale={localeTag} exit={elevatedResult.ExitCode}.");
            return elevatedResult;
        }

        _loggerAccessor()?.Info(
            $"stage=winrt_ocr_lang_pack event=install_failed locale={localeTag} method=runas exit={elevatedResult.ExitCode}.");

        return elevatedResult;
    }

    private static string ResolveElevatorPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Tools", "WinRtLanguagePackElevator", ElevatorExeName);
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
        CancellationToken cancellationToken,
        IProgress<CapabilityInstallProgress>? progress)
    {
        var pipeName = $"hotkey_translator_winrt_ocr_{Guid.NewGuid():N}";
        await using var pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var pipeReadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipeReadTask = ReadProgressFromPipeAsync(pipeServer, progress, pipeReadCts.Token);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = $"{commandArguments} --capability {capabilityName} --pipe {pipeName}".Trim(),
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            }
        };

        try
        {
            if (!process.Start())
            {
                pipeReadCts.Cancel();
                await DrainPipeTaskAsync(pipeReadTask).ConfigureAwait(false);
                return new CapabilityInstallResult(
                    CapabilityInstallStatus.FailedExitCode,
                    -1,
                    command,
                    string.Empty,
                    "Failed to start elevated helper process.");
            }

            progress?.Report(new CapabilityInstallProgress(0, "Waiting for UAC approval..."));
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            pipeReadCts.Cancel();
            await DrainPipeTaskAsync(pipeReadTask).ConfigureAwait(false);
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
            pipeReadCts.Cancel();
            await DrainPipeTaskAsync(pipeReadTask).ConfigureAwait(false);
            return new CapabilityInstallResult(
                CapabilityInstallStatus.Canceled,
                -1,
                $"{command} {process.StartInfo.Arguments}",
                string.Empty,
                "Elevation consent was canceled by user.");
        }
        catch (OperationCanceledException)
        {
            pipeReadCts.Cancel();
            await DrainPipeTaskAsync(pipeReadTask).ConfigureAwait(false);
            return new CapabilityInstallResult(
                CapabilityInstallStatus.Canceled,
                -1,
                $"{command} {process.StartInfo.Arguments}",
                string.Empty,
                "Canceled.");
        }
        catch (Exception ex)
        {
            pipeReadCts.Cancel();
            await DrainPipeTaskAsync(pipeReadTask).ConfigureAwait(false);
            return new CapabilityInstallResult(
                CapabilityInstallStatus.FailedExitCode,
                -1,
                $"{command} {process.StartInfo.Arguments}",
                string.Empty,
                ex.Message);
        }
    }

    private static async Task DrainPipeTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // WHY: Progress channel failures must not change install result classification.
        }
    }

    private static async Task ReadProgressFromPipeAsync(
        NamedPipeServerStream pipeServer,
        IProgress<CapabilityInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (progress == null)
        {
            return;
        }

        try
        {
            await pipeServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipeServer);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ProgressPacket? packet;
                try
                {
                    packet = JsonSerializer.Deserialize<ProgressPacket>(line);
                }
                catch
                {
                    continue;
                }

                if (packet == null)
                {
                    continue;
                }

                progress.Report(
                    new CapabilityInstallProgress(
                        Math.Clamp(packet.Percent, 0, 100),
                        packet.Message ?? string.Empty));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
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

    private sealed record ProgressPacket(int Percent, string Message);
}

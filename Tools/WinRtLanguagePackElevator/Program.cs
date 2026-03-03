using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const int ExitSuccess = 0;
const int ExitInvalidArgs = 2;
const int ExitUnsupported = 3;
const int ExitInstallFailed = 4;
var percentRegex = new Regex(@"(?<!\d)(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.CultureInvariant | RegexOptions.Compiled);

if (!OperatingSystem.IsWindows())
{
    return ExitUnsupported;
}

if (!TryParseArgs(args, out var capabilityName, out var pipeName))
{
    return ExitInvalidArgs;
}

// SECURITY: Only allow the OCR language capability format expected by the app.
if (!Regex.IsMatch(capabilityName, "^Language\\.OCR~~~[A-Za-z0-9-]+~0\\.0\\.1\\.0$", RegexOptions.CultureInvariant))
{
    return ExitInvalidArgs;
}

using var progressWriter = await TryOpenProgressPipeAsync(pipeName).ConfigureAwait(false);
var reporter = new ProgressReporter(progressWriter);
await reporter.ReportAsync(0, "Waiting for UAC and installer startup...").ConfigureAwait(false);

var dismArgs = $"/Online /Add-Capability /CapabilityName:{capabilityName}";
var dism = await RunDismWithProgressAsync(dismArgs, reporter, percentRegex).ConfigureAwait(false);
if (dism.exitCode == 0)
{
    await reporter.ReportAsync(100, "OCR language pack install completed.").ConfigureAwait(false);
    return ExitSuccess;
}

await reporter.ReportAsync(
        reporter.LastReportedPercent >= 0 ? reporter.LastReportedPercent : 0,
        "OCR language pack install failed.")
    .ConfigureAwait(false);
return ExitInstallFailed;

static bool TryParseArgs(string[] args, out string capabilityName, out string? pipeName)
{
    capabilityName = string.Empty;
    pipeName = null;

    for (var i = 0; i < args.Length; i++)
    {
        var token = args[i];
        if (string.Equals(token, "--capability", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                return false;
            }

            capabilityName = args[++i]?.Trim() ?? string.Empty;
            continue;
        }

        if (string.Equals(token, "--pipe", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                return false;
            }

            pipeName = args[++i]?.Trim();
            continue;
        }

        return false;
    }

    return capabilityName.Length > 0;
}

static async Task<(int exitCode, string output, string error)> RunDismWithProgressAsync(
    string arguments,
    ProgressReporter reporter,
    Regex percentRegex)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "dism",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        }
    };

    if (!process.Start())
    {
        return (-1, string.Empty, "Failed to start DISM process.");
    }

    var outputBuilder = new StringBuilder();
    var errorTask = process.StandardError.ReadToEndAsync();

    while (true)
    {
        var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        if (line == null)
        {
            break;
        }

        outputBuilder.AppendLine(line);
        if (TryExtractPercent(line, percentRegex, out var percent))
        {
            await reporter.ReportAsync(percent, $"Installing OCR language pack... {percent}%").ConfigureAwait(false);
        }
    }

    await process.WaitForExitAsync().ConfigureAwait(false);
    var error = await errorTask.ConfigureAwait(false);
    return (process.ExitCode, outputBuilder.ToString(), error);
}

static bool TryExtractPercent(string line, Regex percentRegex, out int percent)
{
    var match = percentRegex.Match(line);
    if (!match.Success)
    {
        percent = 0;
        return false;
    }

    var token = match.Groups[1].Value;
    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
    {
        percent = Math.Clamp((int)Math.Round(raw), 0, 100);
        return true;
    }

    percent = 0;
    return false;
}

static async Task<StreamWriter?> TryOpenProgressPipeAsync(string? pipeName)
{
    if (string.IsNullOrWhiteSpace(pipeName))
    {
        return null;
    }

    try
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(1500).ConfigureAwait(false);
        return new StreamWriter(client, new UTF8Encoding(false), leaveOpen: false)
        {
            AutoFlush = true
        };
    }
    catch
    {
        return null;
    }
}

file sealed class ProgressReporter
{
    private readonly StreamWriter? _writer;

    public ProgressReporter(StreamWriter? writer)
    {
        _writer = writer;
    }

    public int LastReportedPercent { get; private set; } = -1;

    public async Task ReportAsync(int percent, string message)
    {
        if (_writer == null)
        {
            return;
        }

        percent = Math.Clamp(percent, 0, 100);
        if (percent == LastReportedPercent && string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        LastReportedPercent = percent;
        var payload = JsonSerializer.Serialize(new ProgressPacket(percent, message));
        await _writer.WriteLineAsync(payload).ConfigureAwait(false);
    }

    private sealed record ProgressPacket(int Percent, string Message);
}

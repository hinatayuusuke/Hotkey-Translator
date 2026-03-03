using System.Diagnostics;
using System.Text.RegularExpressions;

const int ExitSuccess = 0;
const int ExitInvalidArgs = 2;
const int ExitUnsupported = 3;
const int ExitInstallFailed = 4;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Unsupported OS.");
    return ExitUnsupported;
}

if (!TryParseCapability(args, out var capabilityName))
{
    Console.Error.WriteLine("Usage: WinRtLanguagePackElevator --capability Language.OCR~~~<locale>~0.0.1.0");
    return ExitInvalidArgs;
}

// SECURITY: Only allow the OCR language capability format expected by the app.
if (!Regex.IsMatch(capabilityName, "^Language\\.OCR~~~[A-Za-z0-9-]+~0\\.0\\.1\\.0$", RegexOptions.CultureInvariant))
{
    Console.Error.WriteLine("Invalid capability name.");
    return ExitInvalidArgs;
}

var powershellArgs = $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-WindowsCapability -Online -Name '{capabilityName}'\"";
var ps = await RunProcessAsync("powershell", powershellArgs).ConfigureAwait(false);
if (ps.exitCode == 0)
{
    return ExitSuccess;
}

var dismArgs = $"/Online /Add-Capability /CapabilityName:{capabilityName}";
var dism = await RunProcessAsync("dism", dismArgs).ConfigureAwait(false);
if (dism.exitCode == 0)
{
    return ExitSuccess;
}

Console.Error.WriteLine($"PowerShell failed ({ps.exitCode}): {ps.error}");
Console.Error.WriteLine($"DISM failed ({dism.exitCode}): {dism.error}");
return ExitInstallFailed;

static bool TryParseCapability(string[] args, out string capabilityName)
{
    capabilityName = string.Empty;
    if (args.Length != 2)
    {
        return false;
    }

    if (!string.Equals(args[0], "--capability", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    capabilityName = args[1]?.Trim() ?? string.Empty;
    return capabilityName.Length > 0;
}

static async Task<(int exitCode, string output, string error)> RunProcessAsync(string fileName, string arguments)
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
        }
    };

    if (!process.Start())
    {
        return (-1, string.Empty, "Failed to start process.");
    }

    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    var output = await outputTask.ConfigureAwait(false);
    var error = await errorTask.ConfigureAwait(false);
    return (process.ExitCode, output, error);
}

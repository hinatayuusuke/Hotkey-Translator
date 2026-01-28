using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

var mode = CaptureMode.Screen;
var argIndex = 0;
if (args.Length > 0 && TryParseMode(args[0], out var parsedMode))
{
    mode = parsedMode;
    argIndex = 1;
}

if (args.Length > argIndex + 3)
{
    PrintUsage();
    return;
}

var durationSeconds = 5;
var intervalMs = 500;
var outputDir = Environment.CurrentDirectory;
string? singleOutput = null;

var logger = new AppLogger(Console.WriteLine);
var provider = new DxgiDuplicationProvider(logger);
var settings = new AppSettings { EnableDxgiCapture = true };

if (!provider.IsEnabled(settings))
{
    Console.WriteLine("DXGI capture is disabled in settings.");
    return;
}

if (args.Length > argIndex)
{
    if (IsSingleCaptureArg(args[argIndex], out var outputPath))
    {
        singleOutput = outputPath;
        argIndex++;
    }
    else if (TryParseInt(args[argIndex], out var parsedDuration))
    {
        durationSeconds = parsedDuration;
        argIndex++;
    }
}

if (args.Length > argIndex && singleOutput == null)
{
    if (TryParseInt(args[argIndex], out var parsedInterval))
    {
        intervalMs = parsedInterval;
        argIndex++;
    }
}

if (args.Length > argIndex)
{
    outputDir = args[argIndex];
    argIndex++;
}

durationSeconds = Math.Clamp(durationSeconds, 1, 3600);
intervalMs = Math.Clamp(intervalMs, 50, 10_000);

if (!string.IsNullOrWhiteSpace(outputDir))
{
    Directory.CreateDirectory(outputDir);
}

provider.SetResidentEnabled(true);
try
{
    if (singleOutput != null)
    {
        if (!provider.TryCapture(mode, out var frame, out var error))
        {
            Console.WriteLine($"DXGI capture failed: {error}");
            return;
        }

        using (frame)
        {
            SaveFrame(frame, singleOutput);
        }

        return;
    }

    var stopwatch = Stopwatch.StartNew();
    var sequence = 0;
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
    {
        if (!provider.TryCapture(mode, out var frame, out var error))
        {
            Console.WriteLine($"DXGI capture failed: {error}");
        }
        else
        {
            using (frame)
            {
                var path = BuildSequencePath(outputDir, mode, sequence++);
                SaveFrame(frame, path);
            }
        }

        System.Threading.Thread.Sleep(intervalMs);
    }
}
finally
{
    provider.SetResidentEnabled(false);
}

static bool TryParseMode(string value, out CaptureMode mode)
{
    mode = CaptureMode.Screen;

    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    switch (value.Trim().ToLowerInvariant())
    {
        case "screen":
        case "full":
        case "desktop":
            mode = CaptureMode.Screen;
            return true;
        case "window":
        case "active":
        case "activewindow":
            mode = CaptureMode.ActiveWindow;
            return true;
        default:
            return false;
    }
}

static bool IsSingleCaptureArg(string value, out string outputPath)
{
    outputPath = string.Empty;
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var extension = Path.GetExtension(value);
    if (!string.IsNullOrWhiteSpace(extension) && extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
    {
        outputPath = value;
        return true;
    }

    return false;
}

static bool TryParseInt(string value, out int result)
{
    return int.TryParse(value, out result);
}

static string BuildSequencePath(string outputDir, CaptureMode mode, int sequence)
{
    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    return Path.Combine(outputDir, $"dxgi_{mode.ToString().ToLowerInvariant()}_{stamp}_{sequence:000}.png");
}

static void SaveFrame(CaptureFrame frame, string outputPath)
{
    var fullPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    frame.Bitmap.Save(fullPath, ImageFormat.Png);
    Console.WriteLine($"Saved: {fullPath}");
    Console.WriteLine($"Mode: {frame.ProviderKind} | Bounds: {frame.Bounds} | Size: {frame.Bitmap.Width}x{frame.Bitmap.Height}");
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project Tools/DxgiCaptureSmokeTest -- [screen|window] [durationSeconds] [intervalMs] [outputDir]");
    Console.WriteLine("  dotnet run --project Tools/DxgiCaptureSmokeTest -- [screen|window] [outputFile.png]");
    Console.WriteLine("Defaults:");
    Console.WriteLine("  durationSeconds=5 intervalMs=500 outputDir=.");
}

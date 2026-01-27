using System;
using System.Drawing.Imaging;
using System.IO;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

var mode = CaptureMode.Screen;
var outputPathIndex = 0;
if (args.Length > 0 && TryParseMode(args[0], out var parsedMode))
{
    mode = parsedMode;
    outputPathIndex = 1;
}

if (args.Length > outputPathIndex + 1)
{
    PrintUsage();
    return;
}

var outputPath = args.Length > outputPathIndex
    ? args[outputPathIndex]
    : BuildDefaultPath(mode);

var logger = new AppLogger(Console.WriteLine);
var provider = new DxgiDuplicationProvider(logger);
var settings = new AppSettings { EnableDxgiCapture = true };

if (!provider.IsEnabled(settings))
{
    Console.WriteLine("DXGI capture is disabled in settings.");
    return;
}

if (!provider.TryCapture(mode, out var frame, out var error))
{
    Console.WriteLine($"DXGI capture failed: {error}");
    return;
}

using (frame)
{
    var fullPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    frame.Bitmap.Save(fullPath, ImageFormat.Png);
    Console.WriteLine($"Saved: {fullPath}");
    Console.WriteLine($"Mode: {mode} | Provider: {frame.ProviderKind}");
    Console.WriteLine($"Bounds: {frame.Bounds} | Size: {frame.Bitmap.Width}x{frame.Bitmap.Height}");
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

static string BuildDefaultPath(CaptureMode mode)
{
    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    return Path.Combine(Environment.CurrentDirectory, $"dxgi_{mode.ToString().ToLowerInvariant()}_{stamp}.png");
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project Tools/DxgiCaptureSmokeTest -- [screen|window] [outputPath]");
    Console.WriteLine("  dotnet run --project Tools/DxgiCaptureSmokeTest -- [outputPath]");
}

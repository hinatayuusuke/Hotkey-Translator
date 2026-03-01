using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class PaddleOcrProvider : IOcrProvider
{
    private const string ScriptName = "paddle_ocr_bridge.py";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly AppLogger? _logger;

    public PaddleOcrProvider(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var projectDir = ResolveDirectory(settings.PaddleProjectDir);
        var scriptPath = Path.Combine(projectDir, ScriptName);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Paddle OCR script not found: {scriptPath}");
        }

        var uvPath = string.IsNullOrWhiteSpace(settings.PaddleUvPath) ? "uv" : settings.PaddleUvPath.Trim();
        var language = ResolvePaddleLanguage(settings);
        var device = string.IsNullOrWhiteSpace(settings.PaddleDevice) ? "cpu" : settings.PaddleDevice.Trim();
        var modelDir = string.IsNullOrWhiteSpace(settings.PaddleModelDir) ? null : ResolvePath(settings.PaddleModelDir);

        var tempPath = Path.Combine(Path.GetTempPath(), $"paddle-ocr-{Guid.NewGuid():N}.png");
        try
        {
            bitmap.Save(tempPath, ImageFormat.Png);
            var output = await RunUvAsync(uvPath, projectDir, scriptPath, tempPath, language, device, modelDir, cancellationToken)
                .ConfigureAwait(false);
            var json = ExtractJson(output);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException("Paddle OCR returned no JSON output.");
            }

            var response = JsonSerializer.Deserialize<PaddleOcrResponse>(json, JsonOptions);
            if (response?.Lines is null)
            {
                throw new InvalidOperationException("Paddle OCR response is missing lines.");
            }

            var lines = new List<OcrLine>(response.Lines.Count);
            foreach (var line in response.Lines)
            {
                if (line.Box is null || line.Box.Length < 4)
                {
                    continue;
                }

                var rect = new Rect(line.Box[0], line.Box[1], line.Box[2], line.Box[3]);
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    continue;
                }

                var confidence = line.Confidence.HasValue ? (float)line.Confidence.Value : 1.0f;
                lines.Add(new OcrLine(line.Text ?? string.Empty, rect, confidence, 1, rect.Height));
            }

            return new OcrResultModel(lines, bitmap.Width, bitmap.Height);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                _logger?.Info($"Failed to delete temp OCR file: {tempPath}");
            }
        }
    }

    private async Task<string> RunUvAsync(
        string uvPath,
        string projectDir,
        string scriptPath,
        string imagePath,
        string language,
        string device,
        string? modelDir,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = uvPath,
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectDir);
        startInfo.ArgumentList.Add("python");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--image");
        startInfo.ArgumentList.Add(imagePath);
        startInfo.ArgumentList.Add("--lang");
        startInfo.ArgumentList.Add(language);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        if (!string.IsNullOrWhiteSpace(modelDir))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(modelDir);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Paddle OCR process.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // Ignore kill failures during cancellation.
            }
        });

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger?.Info($"Paddle OCR failed with exit code {process.ExitCode}: {error}");
            throw new InvalidOperationException($"Paddle OCR exited with code {process.ExitCode}.");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger?.Info($"Paddle OCR stderr: {error}");
        }

        return output;
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Paddle OCR project directory is not set.");
        }

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Paddle OCR project directory not found: {resolved}");
        }

        return resolved;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        // WHY: Prefer base directory but fall back to current directory when running from bin output.
        var baseCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (Directory.Exists(baseCandidate) || File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private static string ResolvePaddleLanguage(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PaddleLanguage))
        {
            return settings.PaddleLanguage.Trim();
        }

        var source = settings.SourceLanguage?.Trim() ?? string.Empty;
        if (source.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "japan";
        }

        if (source.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            // WHY: PaddleOCR expects language families ("ch"/"chinese_cht"), not BCP-47 script tags.
            if (IsTraditionalChinese(source))
            {
                return "chinese_cht";
            }

            return "ch";
        }

        if (source.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            return "cyrillic";
        }

        return "en";
    }

    private static bool IsTraditionalChinese(string language)
    {
        var normalized = language.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized == "zh-tw"
               || normalized == "zh-hk"
               || normalized == "zh-mo"
               || normalized == "zh-hant"
               || normalized.StartsWith("zh-hant-", StringComparison.Ordinal);
    }

    private static string? ExtractJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return output.Substring(start, end - start + 1);
    }

    private sealed class PaddleOcrResponse
    {
        public List<PaddleOcrLine>? Lines { get; set; }
    }

    private sealed class PaddleOcrLine
    {
        public string? Text { get; set; }
        public double[]? Box { get; set; }
        public double? Confidence { get; set; }
    }
}

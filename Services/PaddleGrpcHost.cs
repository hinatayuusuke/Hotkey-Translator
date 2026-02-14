using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.OcrGrpc;
using Hotkey_Translator.Services.GrpcHost;

namespace Hotkey_Translator.Services;

internal sealed class PaddleGrpcHost : GrpcHostBase
{
    public PaddleGrpcHost(AppLogger? logger = null)
        : base(logger)
    {
    }

    protected override string HostId => "paddle_grpc";

    protected override bool IsEnabled(AppSettings settings)
    {
        return settings.EnablePaddleGrpcHost;
    }

    protected override Task<Process> StartProcessCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var projectDir = ResolveDirectory(settings.PaddleGrpcProjectDir);
        var script = string.IsNullOrWhiteSpace(settings.PaddleGrpcServerScript) ? "server.py" : settings.PaddleGrpcServerScript.Trim();
        var scriptPath = Path.Combine(projectDir, script);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Paddle gRPC server not found: {scriptPath}");
        }

        var uvPath = string.IsNullOrWhiteSpace(settings.PaddleGrpcUvPath) ? "uv" : settings.PaddleGrpcUvPath.Trim();
        var host = string.IsNullOrWhiteSpace(settings.PaddleGrpcHost) ? "127.0.0.1" : settings.PaddleGrpcHost.Trim();
        var port = settings.PaddleGrpcPort <= 0 ? 50051 : settings.PaddleGrpcPort;
        var modelDir = string.IsNullOrWhiteSpace(settings.PaddleModelDir) ? null : ResolvePath(settings.PaddleModelDir);
        var device = string.IsNullOrWhiteSpace(settings.PaddleDevice) ? "cpu" : settings.PaddleDevice.Trim();
        if (string.Equals(device, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            // WHY: PaddleOCR v5 GPU-only path is enforced; override cpu to gpu:0 for host startup.
            device = "gpu:0";
        }

        var language = ResolvePaddleLanguage(settings);
        var detModel = string.IsNullOrWhiteSpace(settings.PaddleTextDetectionModelName)
            ? "PP-OCRv5_mobile_det"
            : settings.PaddleTextDetectionModelName.Trim();
        var recModel = ResolveTextRecognitionModelName(settings);
        var textDetThresh = Math.Clamp(settings.PaddleTextDetThresh, 0.0, 1.0);
        var textDetBoxThresh = Math.Clamp(settings.PaddleTextDetBoxThresh, 0.0, 1.0);
        var textDetUnclipRatio = Math.Clamp(settings.PaddleTextDetUnclipRatio, 0.5, 3.0);
        var textRecScoreThresh = Math.Clamp(settings.PaddleTextRecScoreThresh, 0.0, 1.0);

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
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--lang");
        startInfo.ArgumentList.Add(language);
        startInfo.ArgumentList.Add("--det-model");
        startInfo.ArgumentList.Add(detModel);
        startInfo.ArgumentList.Add("--rec-model");
        startInfo.ArgumentList.Add(recModel);
        startInfo.ArgumentList.Add("--text-det-thresh");
        startInfo.ArgumentList.Add(textDetThresh.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--text-det-box-thresh");
        startInfo.ArgumentList.Add(textDetBoxThresh.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--text-det-unclip-ratio");
        startInfo.ArgumentList.Add(textDetUnclipRatio.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--text-rec-score-thresh");
        startInfo.ArgumentList.Add(textRecScoreThresh.ToString("0.###", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(modelDir))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(modelDir);
        }

        var process = StartProcessWithLogging(startInfo, "PaddleGrpc");
        return Task.FromResult(process);
    }

    protected override async Task WaitForReadyCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(settings);
        var timeoutMs = Math.Max(1000, settings.PaddleGrpcReadyTimeoutMs);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var channel = GrpcChannel.ForAddress(endpoint);
                var client = new OcrService.OcrServiceClient(channel);
                var reply = await client.HealthAsync(new HealthRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (reply.Ready)
                {
                    Logger?.Info($"Paddle gRPC ready: {reply.Message}");
                    return;
                }
            }
            catch
            {
                // NOTE: Keep retrying until timeout; server may still be warming up.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Paddle gRPC server did not become ready in time.");
    }

    protected override GrpcHostRestartPolicy GetRestartPolicy(AppSettings settings)
    {
        return new GrpcHostRestartPolicy(
            Math.Max(1, settings.PaddleGrpcRestartMax),
            TimeSpan.FromSeconds(Math.Max(1, settings.PaddleGrpcRestartWindowSeconds)));
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PaddleGrpcEndpoint))
        {
            return settings.PaddleGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.PaddleGrpcHost) ? "127.0.0.1" : settings.PaddleGrpcHost.Trim();
        var port = settings.PaddleGrpcPort <= 0 ? 50051 : settings.PaddleGrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Paddle gRPC project directory is not set.");
        }

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Paddle gRPC project directory not found: {resolved}");
        }

        return resolved;
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var baseCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (Directory.Exists(baseCandidate) || File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private static string ResolvePaddleLanguage(AppSettings settings)
    {
        var source = settings.SourceLanguage?.Trim() ?? string.Empty;
        return source.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? "japan" : "en";
    }

    private static string ResolveTextRecognitionModelName(AppSettings settings)
    {
        var selected = settings.PaddleTextRecognitionModelName?.Trim();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return "PP-OCRv5_server_rec";
        }

        if (selected.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveRecognitionModelByLanguage(settings.SourceLanguage);
        }

        return selected;
    }

    private static string ResolveRecognitionModelByLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "PP-OCRv5_server_rec";
        }

        var normalized = language.Trim();
        if (normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en_PP-OCRv5_mobile_rec";
        }

        if (normalized.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            return "eslav_PP-OCRv5_mobile_rec";
        }

        return "PP-OCRv5_server_rec";
    }
}

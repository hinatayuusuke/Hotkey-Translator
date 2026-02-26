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

internal sealed class NdlGrpcHost : GrpcHostBase
{
    public NdlGrpcHost(Func<AppLogger?>? loggerAccessor = null)
        : base(loggerAccessor)
    {
    }

    protected override string HostId => "ndl_grpc";

    protected override bool IsEnabled(AppSettings settings)
    {
        return settings.EnableNdlGrpcHost;
    }

    protected override Task<Process> StartProcessCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var projectDir = ResolveDirectory(settings.NdlGrpcProjectDir);
        var script = string.IsNullOrWhiteSpace(settings.NdlGrpcServerScript) ? "server.py" : settings.NdlGrpcServerScript.Trim();
        var scriptPath = Path.Combine(projectDir, script);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"NDLOCR gRPC server not found: {scriptPath}");
        }

        var uvPath = string.IsNullOrWhiteSpace(settings.NdlGrpcUvPath) ? "uv" : settings.NdlGrpcUvPath.Trim();
        var port = settings.NdlGrpcPort <= 0 ? 50053 : settings.NdlGrpcPort;
        var device = NormalizeDevice(settings.NdlDevice);
        var detScoreThreshold = Math.Clamp(settings.NdlDetScoreThreshold, 0.0, 1.0);
        var detConfThreshold = Math.Clamp(settings.NdlDetConfThreshold, 0.0, 1.0);
        var detIouThreshold = Math.Clamp(settings.NdlDetIouThreshold, 0.0, 1.0);
        var modelDir = string.IsNullOrWhiteSpace(settings.NdlModelDir) ? null : ResolvePath(settings.NdlModelDir);
        var configDir = string.IsNullOrWhiteSpace(settings.NdlConfigDir) ? null : ResolvePath(settings.NdlConfigDir);

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
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--det-score-threshold");
        startInfo.ArgumentList.Add(detScoreThreshold.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--det-conf-threshold");
        startInfo.ArgumentList.Add(detConfThreshold.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--det-iou-threshold");
        startInfo.ArgumentList.Add(detIouThreshold.ToString("0.###", CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(modelDir))
        {
            startInfo.ArgumentList.Add("--model-dir");
            startInfo.ArgumentList.Add(modelDir);
        }

        if (!string.IsNullOrWhiteSpace(configDir))
        {
            startInfo.ArgumentList.Add("--config-dir");
            startInfo.ArgumentList.Add(configDir);
        }

        var process = StartProcessWithLogging(startInfo, "NdlGrpc");
        return Task.FromResult(process);
    }

    protected override async Task WaitForReadyCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(settings);
        var timeoutMs = Math.Max(1000, settings.NdlGrpcReadyTimeoutMs);
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
                    Logger?.Info($"NDLOCR gRPC ready: {reply.Message}");
                    return;
                }
            }
            catch
            {
                // NOTE: Keep retrying until timeout; server may still be warming up.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("NDLOCR gRPC server did not become ready in time.");
    }

    protected override GrpcHostRestartPolicy GetRestartPolicy(AppSettings settings)
    {
        return new GrpcHostRestartPolicy(
            Math.Max(1, settings.NdlGrpcRestartMax),
            TimeSpan.FromSeconds(Math.Max(1, settings.NdlGrpcRestartWindowSeconds)));
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.NdlGrpcEndpoint))
        {
            return settings.NdlGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.NdlGrpcHost) ? "127.0.0.1" : settings.NdlGrpcHost.Trim();
        var port = settings.NdlGrpcPort <= 0 ? 50053 : settings.NdlGrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("NDLOCR gRPC project directory is not set.");
        }

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"NDLOCR gRPC project directory not found: {resolved}");
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

    private static string NormalizeDevice(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "cuda" ? "cuda" : "cpu";
    }
}

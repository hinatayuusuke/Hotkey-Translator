using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services.GrpcHost;
using Hotkey_Translator.TranslationGrpc;

namespace Hotkey_Translator.Services;

internal sealed class CTranslate2GrpcHost : GrpcHostBase
{
    public CTranslate2GrpcHost(AppLogger? logger = null)
        : base(logger)
    {
    }

    protected override string HostId => "ct2_grpc";

    protected override bool IsEnabled(AppSettings settings)
    {
        return settings.EnableCTranslate2;
    }

    protected override Task<Process> StartProcessCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var projectDir = ResolveDirectory(settings.CTranslate2GrpcProjectDir);
        var script = string.IsNullOrWhiteSpace(settings.CTranslate2GrpcServerScript) ? "server.py" : settings.CTranslate2GrpcServerScript.Trim();
        var scriptPath = Path.Combine(projectDir, script);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"CTranslate2 gRPC server not found: {scriptPath}");
        }

        var uvPath = string.IsNullOrWhiteSpace(settings.CTranslate2GrpcUvPath) ? "uv" : settings.CTranslate2GrpcUvPath.Trim();
        var host = string.IsNullOrWhiteSpace(settings.CTranslate2GrpcHost) ? "127.0.0.1" : settings.CTranslate2GrpcHost.Trim();
        var port = settings.CTranslate2GrpcPort <= 0 ? 50061 : settings.CTranslate2GrpcPort;
        var modelId = string.IsNullOrWhiteSpace(settings.CTranslate2ModelId)
            ? "entai2965/nllb-200-distilled-600M-ctranslate2"
            : settings.CTranslate2ModelId.Trim();
        var modelDir = string.IsNullOrWhiteSpace(settings.CTranslate2ModelDir) ? null : ResolvePath(settings.CTranslate2ModelDir);
        var device = string.IsNullOrWhiteSpace(settings.CTranslate2Device) ? "cpu" : settings.CTranslate2Device.Trim();
        var precision = string.IsNullOrWhiteSpace(settings.CTranslate2Precision) ? "int8" : settings.CTranslate2Precision.Trim();

        Logger?.Info($"Starting CTranslate2 gRPC (device={device}, precision={precision}).");
        if (settings.EnableCTranslate2AutoDownload && string.IsNullOrWhiteSpace(modelDir))
        {
            Logger?.Info("CTranslate2 auto-download enabled (model cache will be created if missing).");
        }

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
        startInfo.ArgumentList.Add("--model-id");
        startInfo.ArgumentList.Add(modelId);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--precision");
        startInfo.ArgumentList.Add(precision);
        if (!string.IsNullOrWhiteSpace(modelDir))
        {
            startInfo.ArgumentList.Add("--model-dir");
            startInfo.ArgumentList.Add(modelDir);
        }

        if (settings.EnableCTranslate2AutoDownload)
        {
            startInfo.ArgumentList.Add("--auto-download");
        }

        var process = StartProcessWithLogging(startInfo, "CT2Grpc");
        return Task.FromResult(process);
    }

    protected override async Task WaitForReadyCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(settings);
        var timeoutMs = Math.Max(1000, settings.CTranslate2GrpcReadyTimeoutMs);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var channel = GrpcChannel.ForAddress(endpoint);
                var client = new TranslationService.TranslationServiceClient(channel);
                var reply = await client.HealthAsync(new HealthRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (reply.Ready)
                {
                    Logger?.Info($"CTranslate2 gRPC ready: {reply.Message}");
                    return;
                }
            }
            catch
            {
                // NOTE: Keep retrying until timeout; server may still be warming up.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("CTranslate2 gRPC server did not become ready in time.");
    }

    protected override GrpcHostRestartPolicy GetRestartPolicy(AppSettings settings)
    {
        return new GrpcHostRestartPolicy(
            Math.Max(1, settings.CTranslate2GrpcRestartMax),
            TimeSpan.FromSeconds(Math.Max(1, settings.CTranslate2GrpcRestartWindowSeconds)));
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CTranslate2GrpcEndpoint))
        {
            return settings.CTranslate2GrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.CTranslate2GrpcHost) ? "127.0.0.1" : settings.CTranslate2GrpcHost.Trim();
        var port = settings.CTranslate2GrpcPort <= 0 ? 50061 : settings.CTranslate2GrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("CTranslate2 gRPC project directory is not set.");
        }

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"CTranslate2 gRPC project directory not found: {resolved}");
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
}

using System;
using System.Collections.Generic;
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

internal sealed class PaddleVlGrpcHost : GrpcHostBase
{
    public PaddleVlGrpcHost(Func<AppLogger?>? loggerAccessor = null)
        : base(loggerAccessor)
    {
    }

    protected override string HostId => "paddle_vl_grpc";

    protected override bool IsEnabled(AppSettings settings)
    {
        return settings.EnablePaddleVlGrpcHost;
    }

    protected override Task<Process> StartProcessCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var projectDir = ResolveDirectory(settings.PaddleVlGrpcProjectDir);
        var script = string.IsNullOrWhiteSpace(settings.PaddleVlGrpcServerScript) ? "server.py" : settings.PaddleVlGrpcServerScript.Trim();
        var scriptPath = Path.Combine(projectDir, script);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"PaddleOCR-VL gRPC server not found: {scriptPath}");
        }

        var uvPath = string.IsNullOrWhiteSpace(settings.PaddleVlGrpcUvPath) ? "uv" : settings.PaddleVlGrpcUvPath.Trim();
        var host = string.IsNullOrWhiteSpace(settings.PaddleVlGrpcHost) ? "127.0.0.1" : settings.PaddleVlGrpcHost.Trim();
        var port = settings.PaddleVlGrpcPort <= 0 ? 50052 : settings.PaddleVlGrpcPort;
        var device = string.IsNullOrWhiteSpace(settings.PaddleVlDevice) ? "gpu:0" : settings.PaddleVlDevice.Trim();
        var pipelineVersion = string.IsNullOrWhiteSpace(settings.PaddleVlPipelineVersion) ? "v1.5" : settings.PaddleVlPipelineVersion.Trim();
        var maxNewTokens = ClampMaxNewTokens(settings.PaddleVlMaxNewTokens);
        var precision = string.IsNullOrWhiteSpace(settings.PaddleVlPrecision) ? "fp32" : settings.PaddleVlPrecision.Trim().ToLowerInvariant();
        if (!string.Equals(precision, "fp16", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(precision, "fp32", StringComparison.OrdinalIgnoreCase))
        {
            precision = "fp32";
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
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--pipeline-version");
        startInfo.ArgumentList.Add(pipelineVersion);
        startInfo.ArgumentList.Add(settings.PaddleVlEnableHpi ? "--enable-hpi" : "--no-enable-hpi");
        startInfo.ArgumentList.Add("--precision");
        startInfo.ArgumentList.Add(precision);
        AddBooleanOptionalArg(startInfo.ArgumentList, "--use-tensorrt", settings.PaddleVlUseTensorrt);
        AddBooleanOptionalArg(startInfo.ArgumentList, "--merge-layout-blocks", settings.PaddleVlMergeLayoutBlocks);
        AddBooleanOptionalArg(startInfo.ArgumentList, "--use-ocr-for-image-block", settings.PaddleVlUseOcrForImageBlock);
        AddBooleanOptionalArg(startInfo.ArgumentList, "--use-layout-detection", settings.PaddleVlUseLayoutDetection);
        AddBooleanOptionalArg(startInfo.ArgumentList, "--layout-nms", settings.PaddleVlLayoutNms);

        if (settings.PaddleVlMaxPixels is > 0)
        {
            startInfo.ArgumentList.Add("--max-pixels");
            startInfo.ArgumentList.Add(settings.PaddleVlMaxPixels.Value.ToString());
        }

        if (settings.PaddleVlLayoutThreshold is >= 0)
        {
            startInfo.ArgumentList.Add("--layout-threshold");
            startInfo.ArgumentList.Add(settings.PaddleVlLayoutThreshold.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (settings.PaddleVlLayoutUnclipRatio.HasValue)
        {
            startInfo.ArgumentList.Add("--layout-unclip-ratio");
            startInfo.ArgumentList.Add(settings.PaddleVlLayoutUnclipRatio.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(settings.PaddleVlLayoutMergeBboxesMode))
        {
            startInfo.ArgumentList.Add("--layout-merge-bboxes-mode");
            startInfo.ArgumentList.Add(settings.PaddleVlLayoutMergeBboxesMode.Trim().ToLowerInvariant());
        }

        if (settings.PaddleVlLayoutMergeBboxesIouThreshold.HasValue)
        {
            startInfo.ArgumentList.Add("--layout-merge-bboxes-iou-threshold");
            startInfo.ArgumentList.Add(settings.PaddleVlLayoutMergeBboxesIouThreshold.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (maxNewTokens.HasValue)
        {
            startInfo.ArgumentList.Add("--max-new-tokens");
            startInfo.ArgumentList.Add(maxNewTokens.Value.ToString());
        }

        var process = StartProcessWithLogging(startInfo, "PaddleVlGrpc");
        return Task.FromResult(process);
    }

    protected override async Task WaitForReadyCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(settings);
        var timeoutMs = Math.Max(1000, settings.PaddleVlGrpcReadyTimeoutMs);
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
                    Logger?.Info($"PaddleOCR-VL gRPC ready: {reply.Message}");
                    return;
                }
            }
            catch
            {
                // NOTE: Keep retrying until timeout; server may still be warming up.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("PaddleOCR-VL gRPC server did not become ready in time.");
    }

    protected override GrpcHostRestartPolicy GetRestartPolicy(AppSettings settings)
    {
        return new GrpcHostRestartPolicy(
            Math.Max(1, settings.PaddleVlGrpcRestartMax),
            TimeSpan.FromSeconds(Math.Max(1, settings.PaddleVlGrpcRestartWindowSeconds)));
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PaddleVlGrpcEndpoint))
        {
            return settings.PaddleVlGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.PaddleVlGrpcHost) ? "127.0.0.1" : settings.PaddleVlGrpcHost.Trim();
        var port = settings.PaddleVlGrpcPort <= 0 ? 50052 : settings.PaddleVlGrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("PaddleOCR-VL gRPC project directory is not set.");
        }

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"PaddleOCR-VL gRPC project directory not found: {resolved}");
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

    private static int? ClampMaxNewTokens(int? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return Math.Clamp(value.Value, 512, 4096);
    }

    private static void AddBooleanOptionalArg(ICollection<string> args, string flag, bool? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        args.Add(value.Value ? flag : $"--no-{flag[2..]}");
    }
}

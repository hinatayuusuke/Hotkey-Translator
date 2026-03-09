using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.OcrGrpc;
using Hotkey_Translator.Services.GrpcHost;
using Hotkey_Translator.Services.Settings;

namespace Hotkey_Translator.Services;

internal sealed class VisionLlmGrpcHost : GrpcHostBase
{
    private const string FixedProjectRelativePath = "OcrServiceVisionLlm";
    private const string FixedServerScriptName = "server.py";
    private const string FixedUvRelativePath = "Tools\\uv\\uv.exe";
    private const string SharedLlamaServerRelativePath = "TranslationServiceLlama\\LlamaCpp\\llama-server.exe";
    private const string SharedLlamaModelsRelativePath = "TranslationServiceLlama\\LlamaCpp\\Models";
    private readonly object _lock = new();
    private int? _trackedLlamaServerPid;
    private string? _trackedLlamaServerPath;

    public VisionLlmGrpcHost(Func<AppLogger?>? loggerAccessor = null)
        : base(loggerAccessor)
    {
    }

    protected override string HostId => "vision_llm_grpc";

    protected override bool IsEnabled(AppSettings settings)
    {
        return settings.EnableVisionLlmGrpcHost;
    }

    protected override Task<Process> StartProcessCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var projectDir = ResolveProjectDirectory();
        var scriptPath = Path.Combine(projectDir, FixedServerScriptName);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"VisionLLM gRPC server not found: {scriptPath}");
        }

        var uvPath = ResolveUvExecutablePath();
        var llamaServerPath = ResolvePath(SharedLlamaServerRelativePath);
        if (!File.Exists(llamaServerPath))
        {
            throw new FileNotFoundException($"VisionLLM llama-server not found: {llamaServerPath}");
        }

        lock (_lock)
        {
            _trackedLlamaServerPath = Path.GetFullPath(llamaServerPath);
            _trackedLlamaServerPid = null;
        }

        var modelsDir = ResolvePath(SharedLlamaModelsRelativePath);
        var modelPath = Path.Combine(modelsDir, SettingsHostNormalizer.NormalizeVisionLlmModelFileName(settings.VisionLlmSelectedModelFileName));
        var mmprojPath = Path.Combine(modelsDir, SettingsHostNormalizer.NormalizeVisionLlmMmprojFileName(settings.VisionLlmSelectedMmprojFileName));
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"VisionLLM model not found: {modelPath}");
        }

        if (!File.Exists(mmprojPath))
        {
            throw new FileNotFoundException($"VisionLLM mmproj not found: {mmprojPath}");
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
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(settings.VisionLlmGrpcHost) ? "127.0.0.1" : settings.VisionLlmGrpcHost.Trim());
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add((settings.VisionLlmGrpcPort <= 0 ? 50074 : settings.VisionLlmGrpcPort).ToString());
        startInfo.ArgumentList.Add("--llama-server");
        startInfo.ArgumentList.Add(llamaServerPath);
        startInfo.ArgumentList.Add("--llama-host");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(settings.VisionLlmHost) ? "127.0.0.1" : settings.VisionLlmHost.Trim());
        startInfo.ArgumentList.Add("--llama-port");
        startInfo.ArgumentList.Add((settings.VisionLlmPort <= 0 ? 8089 : settings.VisionLlmPort).ToString());
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("--mmproj");
        startInfo.ArgumentList.Add(mmprojPath);
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add(Math.Max(256, settings.VisionLlmContextSize).ToString());
        startInfo.ArgumentList.Add("--gpu-layers");
        startInfo.ArgumentList.Add(Math.Max(0, settings.VisionLlmGpuLayers).ToString());
        startInfo.ArgumentList.Add("--threads");
        startInfo.ArgumentList.Add(Math.Max(1, settings.VisionLlmThreads).ToString());
        startInfo.ArgumentList.Add("--parallel");
        startInfo.ArgumentList.Add(Math.Max(1, settings.VisionLlmParallel).ToString());
        startInfo.ArgumentList.Add("--batch-size");
        startInfo.ArgumentList.Add(Math.Max(1, settings.VisionLlmBatchSize).ToString());
        startInfo.ArgumentList.Add("--max-tokens");
        startInfo.ArgumentList.Add(Math.Max(1, settings.VisionLlmMaxTokens).ToString());
        startInfo.ArgumentList.Add("--max-image-side");
        startInfo.ArgumentList.Add(Math.Max(256, settings.VisionLlmMaxImageSide).ToString());
        startInfo.ArgumentList.Add("--temperature");
        startInfo.ArgumentList.Add(settings.LlamaTemperature.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--top-p");
        startInfo.ArgumentList.Add(settings.LlamaTopP.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--top-k");
        startInfo.ArgumentList.Add(settings.LlamaTopK.ToString());
        startInfo.ArgumentList.Add("--repeat-penalty");
        startInfo.ArgumentList.Add(settings.LlamaRepeatPenalty.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--disable-thinking");
        if (settings.EnableVisionLlmDiagFileLog)
        {
            startInfo.ArgumentList.Add("--diag-log-file");
            startInfo.ArgumentList.Add(ResolveDiagLogPath(settings));
        }

        var process = StartProcessWithLogging(startInfo, "VisionLlmGrpc");
        return Task.FromResult(process);
    }

    protected override async Task WaitForReadyCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(settings);
        var projectDir = ResolveProjectDirectory();
        var configuredTimeoutMs = Math.Max(1000, settings.VisionLlmGrpcReadyTimeoutMs);
        var timeoutMs = GrpcStartupTimeoutPolicy.ResolveReadyTimeoutMs(
            settings.VisionLlmGrpcReadyTimeoutMs,
            projectDir,
            out var bootstrapMode);
        if (bootstrapMode)
        {
            Logger?.Info(
                $"stage=grpc_host host={HostId} event=ready_timeout policy=bootstrap_missing_venv configured_ms={configuredTimeoutMs} effective_ms={timeoutMs}.");
        }

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
                    Logger?.Info($"VisionLLM gRPC ready: {reply.Message}");
                    return;
                }
            }
            catch
            {
                // NOTE: Keep retrying until timeout; server may still be warming up.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("VisionLLM gRPC server did not become ready in time.");
    }

    protected override GrpcHostRestartPolicy GetRestartPolicy(AppSettings settings)
    {
        return new GrpcHostRestartPolicy(
            Math.Max(1, settings.VisionLlmGrpcRestartMax),
            TimeSpan.FromSeconds(Math.Max(1, settings.VisionLlmGrpcRestartWindowSeconds)));
    }

    protected override void OnAfterStop()
    {
        // WHY: uv/python can exit before the child llama-server tears down, leaving the HTTP server orphaned.
        // Track and terminate the child process explicitly as a shutdown fallback.
        TryKillTrackedLlamaServer();
        lock (_lock)
        {
            _trackedLlamaServerPid = null;
        }
    }

    protected override void OnProcessOutputLine(string line, bool isError)
    {
        TryTrackLlamaServerPid(line);
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.VisionLlmGrpcEndpoint))
        {
            return settings.VisionLlmGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.VisionLlmGrpcHost) ? "127.0.0.1" : settings.VisionLlmGrpcHost.Trim();
        var port = settings.VisionLlmGrpcPort <= 0 ? 50074 : settings.VisionLlmGrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveProjectDirectory()
    {
        var resolved = ResolvePath(FixedProjectRelativePath);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"VisionLLM gRPC project directory not found: {resolved}");
        }

        return resolved;
    }

    private static string ResolvePath(string path)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string ResolveUvExecutablePath()
    {
        var resolved = ResolvePath(FixedUvRelativePath);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"uv executable not found: {resolved}");
        }

        return resolved;
    }

    private static string ResolveDiagLogPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.VisionLlmDiagLogPath))
        {
            return Path.GetFullPath(settings.VisionLlmDiagLogPath.Trim());
        }

        var dir = Path.Combine(Path.GetTempPath(), "HotkeyTranslator");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "vision_llm_diag.log");
    }

    private void TryTrackLlamaServerPid(string line)
    {
        const string marker = "vision llama-server pid=";
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return;
        }

        var start = index + marker.Length;
        var end = start;
        while (end < line.Length && char.IsDigit(line[end]))
        {
            end++;
        }

        if (end <= start || !int.TryParse(line[start..end], out var pid))
        {
            return;
        }

        lock (_lock)
        {
            _trackedLlamaServerPid = pid;
        }

        Logger?.Info($"Tracked VisionLLM llama-server pid: {pid}");
    }

    private void TryKillTrackedLlamaServer()
    {
        int? trackedPid;
        lock (_lock)
        {
            trackedPid = _trackedLlamaServerPid;
        }

        // WHY: VisionLLM and local translation share the same llama-server.exe binary.
        // Path-based residual cleanup can therefore terminate the other host's child process.
        if (trackedPid.HasValue)
        {
            TryKillLlamaProcessByPid(trackedPid.Value, _trackedLlamaServerPath);
        }
    }

    private bool TryKillLlamaProcessByPid(int pid, string? trackedPath)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return true;
            }

            if (!IsExpectedLlamaServerProcess(process, trackedPath))
            {
                Logger?.Info($"Skip VisionLLM PID {pid} because it does not match tracked llama-server executable.");
                return false;
            }

            Logger?.Info($"Force-killing tracked VisionLLM llama-server PID {pid}.");
            process.Kill(true);
            process.WaitForExit(2000);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex)
        {
            Logger?.Info($"Failed to kill tracked VisionLLM llama-server PID {pid}: {ex.Message}");
            return false;
        }
    }

    private static bool IsExpectedLlamaServerProcess(Process process, string? trackedPath)
    {
        if (string.IsNullOrWhiteSpace(trackedPath))
        {
            return false;
        }

        try
        {
            var executable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(executable),
                Path.GetFullPath(trackedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

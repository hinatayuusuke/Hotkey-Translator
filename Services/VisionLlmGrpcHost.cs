using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const string ManifestFileName = "model_manifest.json";
    private const string UvSyncStateFileName = ".uv-sync.state";
    private const string FixedUvRelativePath = "Tools\\uv\\uv.exe";
    private const string SharedLlamaServerRelativePath = "TranslationServiceLlama\\LlamaCpp\\llama-server.exe";
    private const string SharedLlamaModelsRelativePath = "TranslationServiceLlama\\LlamaCpp\\Models";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly object _lock = new();
    private int? _trackedLlamaServerPid;
    private string? _trackedLlamaServerPath;
    private AppLogger? _logger => Logger;

    public VisionLlmGrpcHost(Func<AppLogger?>? loggerAccessor = null)
        : base(loggerAccessor)
    {
    }

    protected override string HostId => "vision_llm_grpc";

    protected override bool IsEnabled(AppSettings settings)
    {
        return settings.EnableVisionLlmGrpcHost;
    }

    protected override async Task OnBeforeStartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var projectDir = ResolveProjectDirectory();
        var uvPath = ResolveUvExecutablePath();
        ReportLoadProgress(HostLoadPhase.Environment);
        await EnsurePythonRuntimeAsync(projectDir, uvPath, cancellationToken).ConfigureAwait(false);
        ReportLoadProgress(HostLoadPhase.Validation, settings.VisionLlmSelectedModelFileName);
        await EnsureVisionLlmAssetsAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    protected override Task<Process> StartProcessCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ReportLoadProgress(HostLoadPhase.Environment);
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

        var llamaCppDir = Path.GetDirectoryName(llamaServerPath)
            ?? throw new DirectoryNotFoundException($"VisionLLM llama.cpp directory not found: {llamaServerPath}");
        LlamaCppRuntimeLayout.ValidateRequiredRuntimeFiles(llamaCppDir, "VisionLLM OCR");

        lock (_lock)
        {
            _trackedLlamaServerPath = Path.GetFullPath(llamaServerPath);
            _trackedLlamaServerPid = null;
        }

        var modelsDir = ResolvePath(SharedLlamaModelsRelativePath);
        var modelPath = Path.Combine(
            modelsDir,
            SettingsHostNormalizer.NormalizeVisionLlmModelFileName(settings.VisionLlmSelectedModelFileName));
        var mmprojPath = Path.Combine(
            modelsDir,
            SettingsHostNormalizer.NormalizeVisionLlmMmprojFileName(settings.VisionLlmSelectedMmprojFileName));
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
        if (settings.EnableVisionLlmMtp)
        {
            startInfo.ArgumentList.Add("--enable-mtp");
            startInfo.ArgumentList.Add("--mtp-draft-tokens");
            startInfo.ArgumentList.Add(Math.Clamp(settings.VisionLlmMtpDraftTokens, 1, 16).ToString());
        }
        if (settings.EnableVisionLlmDiagFileLog)
        {
            startInfo.ArgumentList.Add("--diag-log-file");
            startInfo.ArgumentList.Add(ResolveDiagLogPath(settings));
        }

        startInfo.ArgumentList.Add("--ready-timeout-ms");
        startInfo.ArgumentList.Add(Math.Max(1000, settings.VisionLlmGrpcReadyTimeoutMs).ToString());
        ReportLoadProgress(HostLoadPhase.Model, settings.VisionLlmSelectedModelFileName);
        var process = StartProcessWithLogging(startInfo, "VisionLlmGrpc");
        return Task.FromResult(process);
    }

    private async Task EnsurePythonRuntimeAsync(string projectDir, string uvPath, CancellationToken cancellationToken)
    {
        var pyprojectPath = Path.Combine(projectDir, "pyproject.toml");
        var manifestPath = Path.Combine(projectDir, ManifestFileName);
        if (!File.Exists(pyprojectPath))
        {
            throw new FileNotFoundException($"pyproject.toml not found: {pyprojectPath}");
        }

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Vision model manifest not found: {manifestPath}");
        }

        var fingerprint = BuildRuntimeFingerprint(projectDir, pyprojectPath, manifestPath);
        var statePath = Path.Combine(projectDir, UvSyncStateFileName);
        var venvPath = Path.Combine(projectDir, ".venv");
        if (Directory.Exists(venvPath) && File.Exists(statePath))
        {
            var cachedFingerprint = (await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false)).Trim();
            if (string.Equals(cachedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _logger?.Info("Skip uv sync for VisionLLM runtime: fingerprint unchanged.");
                return;
            }
        }

        _logger?.Info("Running uv sync for VisionLLM runtime.");
        var startInfo = new ProcessStartInfo
        {
            FileName = uvPath,
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("sync");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectDir);

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _logger?.Info($"[VisionLlm uv] {args.Data}");
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                RecordStartupError(args.Data);
                _logger?.Info($"[VisionLlm uv] {args.Data}");
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start VisionLLM uv sync.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitForPreparationAsync(process, cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"VisionLLM uv sync failed with exit code {process.ExitCode}.");
        }

        await File.WriteAllTextAsync(statePath, fingerprint, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildRuntimeFingerprint(string projectDir, string pyprojectPath, string manifestPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine(ModelAssetProvisioner.ComputeFileSha256(pyprojectPath));
        var lockPath = Path.Combine(projectDir, "uv.lock");
        builder.AppendLine(File.Exists(lockPath) ? ModelAssetProvisioner.ComputeFileSha256(lockPath) : "<missing-uv-lock>");
        builder.AppendLine(ModelAssetProvisioner.ComputeFileSha256(manifestPath));
        var fingerprintBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(fingerprintBytes);
    }

    private async Task EnsureVisionLlmAssetsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var projectDir = ResolveProjectDirectory();
        var manifestPath = Path.Combine(projectDir, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Vision model manifest not found: {manifestPath}");
        }

        var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<VisionModelManifest>(manifestText, ManifestJsonOptions)
            ?? throw new InvalidDataException($"Invalid Vision model manifest: {manifestPath}");
        if (manifest.Model is null || manifest.Mmproj is null)
        {
            throw new InvalidDataException($"Vision model manifest is missing model or mmproj entries: {manifestPath}");
        }

        var selectedModelFileName = SettingsHostNormalizer.NormalizeVisionLlmModelFileName(settings.VisionLlmSelectedModelFileName);
        var selectedMmprojFileName = SettingsHostNormalizer.NormalizeVisionLlmMmprojFileName(settings.VisionLlmSelectedMmprojFileName);
        var modelPath = Path.Combine(ResolvePath(SharedLlamaModelsRelativePath), selectedModelFileName);
        var mmprojPath = Path.Combine(ResolvePath(SharedLlamaModelsRelativePath), selectedMmprojFileName);
        var useManifestAssets =
            string.Equals(selectedModelFileName, manifest.Model.LocalFileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(selectedMmprojFileName, manifest.Mmproj.LocalFileName, StringComparison.OrdinalIgnoreCase);
        if (!useManifestAssets)
        {
            ModelAssetProvisioner.EnsureExistingNonEmptyFile(modelPath, "Selected VisionLLM model");
            ModelAssetProvisioner.EnsureExistingNonEmptyFile(mmprojPath, "Selected VisionLLM mmproj");
            _logger?.Info($"Using user-selected VisionLLM assets: model={modelPath} mmproj={mmprojPath}");
            return;
        }

        await ModelAssetProvisioner.EnsureAssetAsync(
            manifest.Model.ToDescriptor(),
            modelPath,
            assetTag: "vision_model",
            log: message => _logger?.Info(message),
            cancellationToken,
            progress: value => ReportLoadProgress(value.Phase, value.Asset, value.Bytes, value.TotalBytes)).ConfigureAwait(false);
        await ModelAssetProvisioner.EnsureAssetAsync(
            manifest.Mmproj.ToDescriptor(),
            mmprojPath,
            assetTag: "vision_mmproj",
            log: message => _logger?.Info(message),
            cancellationToken,
            progress: value => ReportLoadProgress(value.Phase, value.Asset, value.Bytes, value.TotalBytes)).ConfigureAwait(false);
    }

    protected override async Task WaitForReadyCoreAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(ResolveEndpoint(settings));
        var client = new OcrService.OcrServiceClient(channel);
        await GrpcReadyProbe.WaitAsync(settings.VisionLlmGrpcReadyTimeoutMs,
            () => TryGetProcessExitCode(out var code) ? code : null,
            async (deadline, token) => (await client.HealthAsync(new HealthRequest(),
                deadline: deadline, cancellationToken: token).ConfigureAwait(false)).Ready,
            cancellationToken).ConfigureAwait(false);
        Logger?.Info("VisionLLM gRPC ready.");
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

    private sealed class VisionModelManifest
    {
        [JsonPropertyName("model")]
        public VisionModelAssetManifest? Model { get; init; }

        [JsonPropertyName("mmproj")]
        public VisionModelAssetManifest? Mmproj { get; init; }
    }

    private sealed class VisionModelAssetManifest
    {
        [JsonPropertyName("local_filename")]
        public string LocalFileName { get; init; } = string.Empty;

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;

        [JsonPropertyName("size_bytes")]
        public long? SizeBytes { get; init; }

        public ModelAssetDescriptor ToDescriptor()
        {
            return new ModelAssetDescriptor(
                LocalFileName.Trim(),
                DownloadUrl.Trim(),
                Sha256.Trim(),
                SizeBytes);
        }
    }
}

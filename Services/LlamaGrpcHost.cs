using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Hotkey_Translator.Models;
using Hotkey_Translator.TranslationGrpc;

namespace Hotkey_Translator.Services;

public sealed class LlamaGrpcHost : IDisposable
{
    private const string FixedLlamaServerRelativePath = "LlamaCpp\\llama-server.exe";
    private const string FixedLlamaModelsRelativePath = "LlamaCpp\\Models";
    private const string DefaultLlamaModelFileName = "qwen3-1_7b-instruct-q4_k_m.gguf";
    private const string ManifestFileName = "model_manifest.json";
    private const string UvSyncStateFileName = ".uv-sync.state";
    private static readonly string[] RequiredNativeFiles =
    {
        "llama-server.exe",
        "llama.dll",
        "ggml.dll",
        "ggml-base.dll",
        "ggml-cpu.dll",
        "ggml-cuda.dll",
        "mtmd.dll",
    };
    private static readonly string[] RequiredCudaDllNames =
    {
        "cudart64_12.dll",
        "cublas64_12.dll",
        "cublasLt64_12.dll",
    };
    private static readonly HttpClient DownloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LlamaModelCatalog _modelCatalog = new();
    private readonly AppLogger? _logger;
    private readonly object _lock = new();
    private readonly List<DateTimeOffset> _restartHistory = new();
    private Process? _process;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _stopping;
    private int? _trackedLlamaServerPid;
    private string? _trackedLlamaServerPath;

    public LlamaGrpcHost(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.EnableLlamaCppTranslation)
        {
            return;
        }

        // WHY: gRPC over localhost uses HTTP/2 without TLS by default.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        lock (_lock)
        {
            if (_process is { HasExited: false })
            {
                return;
            }
        }

        await StartProcessAsync(settings, cancellationToken).ConfigureAwait(false);
        await WaitForReadyAsync(settings, cancellationToken).ConfigureAwait(false);
        EnsureMonitor(settings);
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stopping = true;
        }

        _monitorCts?.Cancel();
        try
        {
            _monitorTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown wait failures.
        }

        lock (_lock)
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(true);
                }
            }
            catch
            {
                // Ignore kill failures during shutdown.
            }

            _process?.Dispose();
            _process = null;
        }

        // WHY: If the parent process has already exited unexpectedly, llama-server can remain orphaned.
        // Track and terminate the child process explicitly as a shutdown fallback.
        TryKillTrackedLlamaServer();

        lock (_lock)
        {
            _trackedLlamaServerPid = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _monitorCts?.Dispose();
    }

    private async Task StartProcessAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var projectDir = ResolveDirectory(settings.LlamaGrpcProjectDir);
        var script = string.IsNullOrWhiteSpace(settings.LlamaGrpcServerScript) ? "server.py" : settings.LlamaGrpcServerScript.Trim();
        var scriptPath = Path.Combine(projectDir, script);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Llama gRPC server not found: {scriptPath}");
        }

        var uvPath = string.IsNullOrWhiteSpace(settings.LlamaGrpcUvPath) ? "uv" : settings.LlamaGrpcUvPath.Trim();
        var host = string.IsNullOrWhiteSpace(settings.LlamaGrpcHost) ? "127.0.0.1" : settings.LlamaGrpcHost.Trim();
        var port = settings.LlamaGrpcPort <= 0 ? 50071 : settings.LlamaGrpcPort;
        var selectedModelFileName = _modelCatalog.NormalizeModelFileName(
            settings.LlamaSelectedModelFileName,
            DefaultLlamaModelFileName);
        var paths = ResolveFixedLlamaPaths(projectDir, selectedModelFileName);

        await EnsurePythonRuntimeAsync(projectDir, uvPath, cancellationToken).ConfigureAwait(false);
        ValidateLlamaNativeFiles(paths.LlamaCppDirectory);
        await EnsureLlamaModelAsync(
            paths.ManifestPath,
            paths.ModelPath,
            selectedModelFileName,
            cancellationToken).ConfigureAwait(false);

        var pythonPath = ResolvePythonExecutable(projectDir);
        var nvidiaBinPaths = CollectNvidiaDllBinPaths(projectDir);
        ValidateCudaRuntime(nvidiaBinPaths);

        lock (_lock)
        {
            _trackedLlamaServerPath = Path.GetFullPath(paths.ServerPath);
            _trackedLlamaServerPid = null;
        }

        _logger?.Info("Starting Llama gRPC (llama-server over HTTP).");
        _logger?.Info($"Llama fixed server path: {paths.ServerPath}");
        _logger?.Info($"Llama selected model: {selectedModelFileName}");
        _logger?.Info($"Llama model path: {paths.ModelPath}");
        _logger?.Info($"Llama runtime python: {pythonPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["PATH"] = BuildProcessPath(nvidiaBinPaths);

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--llama-server");
        startInfo.ArgumentList.Add(paths.ServerPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(paths.ModelPath);
        startInfo.ArgumentList.Add("--llama-host");
        startInfo.ArgumentList.Add(settings.LlamaHost);
        startInfo.ArgumentList.Add("--llama-port");
        startInfo.ArgumentList.Add(settings.LlamaPort.ToString());
        startInfo.ArgumentList.Add("--ctx-size");
        startInfo.ArgumentList.Add(settings.LlamaContextSize.ToString());
        startInfo.ArgumentList.Add("--gpu-layers");
        startInfo.ArgumentList.Add(settings.LlamaGpuLayers.ToString());
        startInfo.ArgumentList.Add("--threads");
        startInfo.ArgumentList.Add(settings.LlamaThreads.ToString());
        startInfo.ArgumentList.Add("--parallel");
        startInfo.ArgumentList.Add(settings.LlamaParallel.ToString());
        startInfo.ArgumentList.Add("--batch-size");
        startInfo.ArgumentList.Add(settings.LlamaBatchSize.ToString());
        startInfo.ArgumentList.Add("--max-tokens");
        startInfo.ArgumentList.Add(settings.LlamaMaxTokens.ToString());
        startInfo.ArgumentList.Add("--temperature");
        startInfo.ArgumentList.Add(settings.LlamaTemperature.ToString("0.###"));
        startInfo.ArgumentList.Add("--top-p");
        startInfo.ArgumentList.Add(settings.LlamaTopP.ToString("0.###"));
        startInfo.ArgumentList.Add("--top-k");
        startInfo.ArgumentList.Add(settings.LlamaTopK.ToString());
        startInfo.ArgumentList.Add("--repeat-penalty");
        startInfo.ArgumentList.Add(settings.LlamaRepeatPenalty.ToString("0.###"));
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                TryTrackLlamaServerPid(args.Data);
                _logger?.Info($"[LlamaGrpc] {args.Data}");
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                TryTrackLlamaServerPid(args.Data);
                _logger?.Info($"[LlamaGrpc] {args.Data}");
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Llama gRPC server process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_lock)
        {
            _process?.Dispose();
            _process = process;
            _stopping = false;
        }
    }

    private async Task WaitForReadyAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(settings);
        var timeoutMs = Math.Max(1000, settings.LlamaGrpcReadyTimeoutMs);
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
                    _logger?.Info($"Llama gRPC ready: {reply.Message}");
                    return;
                }
            }
            catch
            {
                // NOTE: Keep retrying until timeout; server may still be warming up.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Llama gRPC server did not become ready in time.");
    }

    private void EnsureMonitor(AppSettings settings)
    {
        if (_monitorCts != null)
        {
            return;
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(settings, _monitorCts.Token));
    }

    private async Task MonitorLoopAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process;
            lock (_lock)
            {
                process = _process;
            }

            if (process == null)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            lock (_lock)
            {
                if (_stopping)
                {
                    return;
                }
            }

            _logger?.Info($"Llama gRPC exited with code {process.ExitCode}.");
            if (!CanRestart(settings))
            {
                _logger?.Info("Llama gRPC restart limit reached.");
                return;
            }

            try
            {
                await StartProcessAsync(settings, cancellationToken).ConfigureAwait(false);
                await WaitForReadyAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Info($"Llama gRPC restart failed: {ex.Message}");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool CanRestart(AppSettings settings)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromSeconds(Math.Max(1, settings.LlamaGrpcRestartWindowSeconds));
        _restartHistory.RemoveAll(time => now - time > window);
        if (_restartHistory.Count >= settings.LlamaGrpcRestartMax)
        {
            return false;
        }

        _restartHistory.Add(now);
        return true;
    }

    private static string ResolveEndpoint(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LlamaGrpcEndpoint))
        {
            return settings.LlamaGrpcEndpoint.Trim();
        }

        var host = string.IsNullOrWhiteSpace(settings.LlamaGrpcHost) ? "127.0.0.1" : settings.LlamaGrpcHost.Trim();
        var port = settings.LlamaGrpcPort <= 0 ? 50071 : settings.LlamaGrpcPort;
        return $"http://{host}:{port}";
    }

    private static string ResolveDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Llama gRPC project directory is not set.");
        }

        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Llama gRPC project directory not found: {resolved}");
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

    private static FixedLlamaPaths ResolveFixedLlamaPaths(string projectDir, string modelFileName)
    {
        var llamaCppDir = Path.Combine(projectDir, "LlamaCpp");
        var safeModelFileName = Path.GetFileName(modelFileName);
        if (string.IsNullOrWhiteSpace(safeModelFileName))
        {
            safeModelFileName = DefaultLlamaModelFileName;
        }

        return new FixedLlamaPaths(
            Path.Combine(projectDir, FixedLlamaServerRelativePath),
            Path.Combine(projectDir, FixedLlamaModelsRelativePath, safeModelFileName),
            llamaCppDir,
            Path.Combine(projectDir, ManifestFileName));
    }

    private void ValidateLlamaNativeFiles(string llamaCppDir)
    {
        var missing = new List<string>();
        foreach (var fileName in RequiredNativeFiles)
        {
            var path = Path.Combine(llamaCppDir, fileName);
            if (!File.Exists(path))
            {
                missing.Add(path);
            }
        }

        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                $"Required llama.cpp native files are missing:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
        }
    }

    private static IReadOnlyList<string> CollectNvidiaDllBinPaths(string projectDir)
    {
        var sitePackages = Path.Combine(projectDir, ".venv", "Lib", "site-packages", "nvidia");
        var bins = new List<string>();
        foreach (var package in new[] { "cuda_runtime", "cublas", "nvjitlink", "cudnn" })
        {
            var bin = Path.Combine(sitePackages, package, "bin");
            if (Directory.Exists(bin))
            {
                bins.Add(bin);
            }
        }

        return bins;
    }

    private void ValidateCudaRuntime(IReadOnlyList<string> nvidiaBinPaths)
    {
        if (nvidiaBinPaths.Count == 0)
        {
            throw new DirectoryNotFoundException(
                "CUDA runtime directories were not found under TranslationServiceLlama/.venv. Run `uv sync` and retry.");
        }

        var missingDlls = new List<string>();
        foreach (var dllName in RequiredCudaDllNames)
        {
            if (!nvidiaBinPaths.Any(bin => File.Exists(Path.Combine(bin, dllName))))
            {
                missingDlls.Add(dllName);
            }
        }

        if (missingDlls.Count > 0)
        {
            throw new FileNotFoundException(
                $"Required CUDA DLLs are missing from .venv:{Environment.NewLine}{string.Join(Environment.NewLine, missingDlls)}");
        }
    }

    private static string BuildProcessPath(IReadOnlyList<string> nvidiaBinPaths)
    {
        var uniqueBins = nvidiaBinPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (uniqueBins.Length == 0)
        {
            return currentPath;
        }

        return string.IsNullOrWhiteSpace(currentPath)
            ? string.Join(";", uniqueBins)
            : $"{string.Join(";", uniqueBins)};{currentPath}";
    }

    private static string ResolvePythonExecutable(string projectDir)
    {
        var relative = OperatingSystem.IsWindows() ? Path.Combine(".venv", "Scripts", "python.exe") : Path.Combine(".venv", "bin", "python");
        var path = Path.Combine(projectDir, relative);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Llama runtime python not found: {path}");
        }

        return path;
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
            throw new FileNotFoundException($"Model manifest not found: {manifestPath}");
        }

        var fingerprint = BuildRuntimeFingerprint(projectDir, pyprojectPath, manifestPath);
        var statePath = Path.Combine(projectDir, UvSyncStateFileName);
        var venvPath = Path.Combine(projectDir, ".venv");
        if (Directory.Exists(venvPath) && File.Exists(statePath))
        {
            var cachedFingerprint = (await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false)).Trim();
            if (string.Equals(cachedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _logger?.Info("Skip uv sync: runtime fingerprint unchanged.");
                return;
            }
        }

        _logger?.Info("Running uv sync for TranslationServiceLlama runtime.");
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
        var output = new StringBuilder();
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                output.AppendLine(args.Data);
                _logger?.Info($"[Llama uv] {args.Data}");
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                errors.AppendLine(args.Data);
                _logger?.Info($"[Llama uv] {args.Data}");
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start uv sync.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"uv sync failed with exit code {process.ExitCode}.{Environment.NewLine}{errors}{output}");
        }

        await File.WriteAllTextAsync(statePath, fingerprint, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildRuntimeFingerprint(string projectDir, string pyprojectPath, string manifestPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine(ComputeFileSha256(pyprojectPath));
        var lockPath = Path.Combine(projectDir, "uv.lock");
        builder.AppendLine(File.Exists(lockPath) ? ComputeFileSha256(lockPath) : "<missing-uv-lock>");
        builder.AppendLine(ComputeFileSha256(manifestPath));
        var fingerprintBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(fingerprintBytes);
    }

    private async Task EnsureLlamaModelAsync(
        string manifestPath,
        string modelPath,
        string selectedModelFileName,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(selectedModelFileName, DefaultLlamaModelFileName, StringComparison.OrdinalIgnoreCase))
        {
            var selectedInfo = new FileInfo(modelPath);
            if (!selectedInfo.Exists)
            {
                throw new FileNotFoundException($"Selected llama model was not found: {modelPath}");
            }

            if (selectedInfo.Length <= 0)
            {
                throw new InvalidDataException($"Selected llama model is empty: {modelPath}");
            }

            _logger?.Info($"Using user-selected llama model: {modelPath}");
            return;
        }

        var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<ModelManifest>(manifestText, ManifestJsonOptions)
            ?? throw new InvalidDataException($"Invalid model manifest: {manifestPath}");
        if (string.IsNullOrWhiteSpace(manifest.Filename) ||
            string.IsNullOrWhiteSpace(manifest.DownloadUrl) ||
            string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            throw new InvalidDataException($"Model manifest is missing required fields: {manifestPath}");
        }

        var expectedFileName = manifest.Filename.Trim();
        var actualFileName = Path.GetFileName(modelPath);
        if (!string.Equals(expectedFileName, actualFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Model manifest filename mismatch. Expected '{actualFileName}', got '{expectedFileName}'.");
        }

        if (TryValidateModel(modelPath, manifest, out _))
        {
            _logger?.Info($"Llama model is ready: {modelPath}");
            return;
        }

        var modelDir = Path.GetDirectoryName(modelPath) ?? throw new InvalidOperationException("Model directory is invalid.");
        Directory.CreateDirectory(modelDir);
        var lockPath = $"{modelPath}.lock";
        await using var lockHandle = await AcquireExclusiveLockAsync(lockPath, cancellationToken).ConfigureAwait(false);

        // WHY: Another process may complete the download while we waited on the lock.
        if (TryValidateModel(modelPath, manifest, out _))
        {
            _logger?.Info($"Llama model became ready while waiting for lock: {modelPath}");
            return;
        }

        var tempPath = $"{modelPath}.tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        _logger?.Info($"Downloading model: {manifest.DownloadUrl}");
        using var response = await DownloadClient.GetAsync(
            manifest.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        if (!TryValidateModel(tempPath, manifest, out var reason))
        {
            File.Delete(tempPath);
            throw new InvalidDataException($"Downloaded model validation failed: {reason}");
        }

        File.Move(tempPath, modelPath, true);
        _logger?.Info($"Model download complete: {modelPath}");
    }

    private static bool TryValidateModel(string modelPath, ModelManifest manifest, out string reason)
    {
        reason = string.Empty;
        if (!File.Exists(modelPath))
        {
            reason = "missing file";
            return false;
        }

        var info = new FileInfo(modelPath);
        if (info.Length <= 0)
        {
            reason = "empty file";
            return false;
        }

        if (manifest.SizeBytes is > 0 && info.Length != manifest.SizeBytes.Value)
        {
            reason = $"size mismatch (expected {manifest.SizeBytes.Value}, actual {info.Length})";
            return false;
        }

        var actualSha = ComputeFileSha256(modelPath);
        if (!string.Equals(actualSha, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"sha256 mismatch (expected {manifest.Sha256}, actual {actualSha})";
            return false;
        }

        return true;
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<FileStream> AcquireExclusiveLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Timed out waiting for lock: {lockPath}");
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryKill(Process process)
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
            // Ignore kill failures on cancellation.
        }
    }

    private void TryTrackLlamaServerPid(string line)
    {
        const string marker = "llama-server pid=";
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

        if (end <= start)
        {
            return;
        }

        if (!int.TryParse(line[start..end], out var pid))
        {
            return;
        }

        lock (_lock)
        {
            _trackedLlamaServerPid = pid;
        }

        _logger?.Info($"Tracked llama-server pid: {pid}");
    }

    private void TryKillTrackedLlamaServer()
    {
        int? trackedPid;
        string? trackedPath;
        lock (_lock)
        {
            trackedPid = _trackedLlamaServerPid;
            trackedPath = _trackedLlamaServerPath;
        }

        if (trackedPid.HasValue && TryKillLlamaProcessByPid(trackedPid.Value, trackedPath))
        {
            return;
        }

        TryKillLlamaProcessByPath(trackedPath);
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
                _logger?.Info($"Skip PID {pid} because it does not match tracked llama-server executable.");
                return false;
            }

            _logger?.Info($"Force-killing tracked llama-server PID {pid}.");
            process.Kill(true);
            process.WaitForExit(2000);
            return true;
        }
        catch (ArgumentException)
        {
            // Process already exited.
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Info($"Failed to kill tracked llama-server PID {pid}: {ex.Message}");
            return false;
        }
    }

    private void TryKillLlamaProcessByPath(string? trackedPath)
    {
        if (string.IsNullOrWhiteSpace(trackedPath))
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(trackedPath);
        foreach (var process in Process.GetProcessesByName(name))
        {
            var pid = process.Id;
            try
            {
                using (process)
                {
                    if (process.HasExited || !IsExpectedLlamaServerProcess(process, trackedPath))
                    {
                        continue;
                    }

                    _logger?.Info($"Force-killing residual llama-server PID {process.Id}.");
                    process.Kill(true);
                    process.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                _logger?.Info($"Failed to kill residual llama-server PID {pid}: {ex.Message}");
            }
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

    private readonly record struct FixedLlamaPaths(
        string ServerPath,
        string ModelPath,
        string LlamaCppDirectory,
        string ManifestPath);

    private sealed class ModelManifest
    {
        [JsonPropertyName("filename")]
        public string Filename { get; init; } = string.Empty;

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;

        [JsonPropertyName("size_bytes")]
        public long? SizeBytes { get; init; }
    }
}

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Grpc.Core;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.Application;
using Hotkey_Translator.Services.GrpcHost;

await Suite.RunAsync();

static class Suite
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }

    private static async Task Throws<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + name);
    }

    public static async Task RunAsync()
    {
        await ProbeAsync();
        await LoadLifecycleAsync();
        await ShutdownDuringLoadAsync();
        await OtherHostsAsync();
        await AssetsAsync();
        await PreparationAsync();
        if (Environment.GetCommandLineArgs().Contains("--native")) await NativeAsync();
        Check(!HostLoadDiagnostics.Sanitize("token=example-secret Authorization: Bearer other-secret https://host/?key=hidden")
            .Contains("secret"), "diagnostic credentials redacted");
        Console.WriteLine($"Completed {_checks} checks.");
    }

    private static async Task ProbeAsync()
    {
        var elapsed = Stopwatch.StartNew();
        await Throws<HostProcessExitedException>(() => GrpcReadyProbe.WaitAsync(60000, () => 23,
            (_, _) => Task.FromResult(true), default), "process already exited fails immediately");
        Check(elapsed.ElapsedMilliseconds < 1000, "exit does not wait for startup deadline");

        int? exit = null;
        using var lifetime = new CancellationTokenSource();
        var pending = GrpcReadyProbe.WaitAsync(60000, () => exit, async (_, token) =>
        {
            exit = 7;
            await Task.Delay(Timeout.Infinite, token);
            return false;
        }, lifetime.Token);
        await Throws<HostProcessExitedException>(() => pending, "process exit interrupts in-flight Health");

        elapsed.Restart();
        await Throws<TimeoutException>(() => GrpcReadyProbe.WaitAsync(1000, () => null,
            async (_, token) => { await Task.Delay(Timeout.Infinite, token); return false; }, default), "hung Health is bounded");
        Check(elapsed.ElapsedMilliseconds < 2000, "overall deadline enforced during RPC");

        var attempts = 0;
        await GrpcReadyProbe.WaitAsync(5000, () => null, (_, _) =>
        {
            if (++attempts < 3) throw new RpcException(new Status(StatusCode.Unavailable, "warming"));
            return Task.FromResult(true);
        }, default);
        Check(attempts == 3, "temporary unavailable followed by ready succeeds");
        attempts = 0;
        await GrpcReadyProbe.WaitAsync(5000, () => null, async (_, token) =>
        {
            if (++attempts == 1) await Task.Delay(Timeout.Infinite, token);
            return true;
        }, default);
        Check(attempts == 2, "one RPC timeout does not exhaust overall startup budget");
        await Throws<RpcException>(() => GrpcReadyProbe.WaitAsync(60000, () => null,
            (_, _) => throw new RpcException(new Status(StatusCode.PermissionDenied, "denied")), default), "permanent RPC error fails immediately");
        using var cancelled = new CancellationTokenSource(80);
        await Throws<OperationCanceledException>(() => GrpcReadyProbe.WaitAsync(60000, () => null,
            async (_, token) => { await Task.Delay(Timeout.Infinite, token); return false; }, cancelled.Token), "user cancellation preserved");

        // NOTE: This uses a real HTTP/2 gRPC client against a peer that accepts TCP and never responds.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
            var client = new Hotkey_Translator.TranslationGrpc.TranslationService.TranslationServiceClient(channel);
            await Throws<TimeoutException>(() => GrpcReadyProbe.WaitAsync(1000, () => null,
                async (deadline, token) => (await client.HealthAsync(new(), deadline: deadline, cancellationToken: token)).Ready,
                default), "real gRPC unresponsive peer times out");
        }
        finally { listener.Stop(); }
    }

    private static async Task LoadLifecycleAsync()
    {
        var busy = false;
        var messages = 0;
        var enabled = true;
        var stopped = 0;
        var starts = 0;
        var running = false;
        Func<CancellationToken, Task> start = _ => throw new InvalidOperationException("bad model");
        var descriptor = new GrpcHostDescriptor
        {
            HostId = "llama_grpc", ShouldLoad = settings => settings.EnableLlamaCppTranslation,
            IsRunning = () => running,
            StartAsync = async (_, token) => { starts++; await start(token); running = true; },
            Stop = () => { running = false; stopped++; }, BusyMessage = _ => "loading",
            DisableOnFailure = settings => { settings.EnableLlamaCppTranslation = false; enabled = false; },
            FailureLogMessage = "failed", FailureUserMessage = _ => "failed",
            DescribeFailure = (settings, ex) => new("llama_grpc", settings.LlamaSelectedModelFileName,
                null, HostLoadPhase.Model, ex.Message, "")
        };
        using var facade = new ResourceHostFacade(() => null, (visible, _) => busy = visible, (_, _) => { },
            message => { Check(!busy, "busy hidden before error dialog"); Check(message.Contains("bad model"), "failure reason displayed"); messages++; },
            _ => { }, _ => { }, _ => { }, new GrpcHostRegistry(new[] { descriptor }));
        var settings = new AppSettings { EnableLlamaCppTranslation = true, OcrEngine = OcrEngineKind.WinRt };
        var result = await facade.EnsureResourceHostsAsync(settings);
        Check(result.Status == ResourceHostLoadStatus.Failed && result.SettingsChanged && !enabled && messages == 1,
            "load failure reports result and preserves existing disable policy");

        settings.EnableLlamaCppTranslation = enabled = true;
        start = async token => { facade.CancelLoading(); await Task.Delay(Timeout.Infinite, token); };
        result = await facade.EnsureResourceHostsAsync(settings);
        Check(result.Status == ResourceHostLoadStatus.Cancelled && enabled && messages == 1 && !busy,
            "cancel closes busy without error or disabling model");
        Check(facade.GetUnavailableMessage(settings) != null && stopped >= 2, "cancelled selection marked not loaded and stopped");
        var cancelledStarts = starts;
        result = await facade.EnsureResourceHostsAsync(settings);
        Check(starts == cancelledStarts && result.Status == ResourceHostLoadStatus.Cancelled,
            "unrelated save does not restart cancelled model");
        facade.AllowExplicitReload(translationOnly: true);
        start = _ => Task.CompletedTask;
        result = await facade.EnsureResourceHostsAsync(settings);
        Check(result.Status == ResourceHostLoadStatus.Succeeded && running && facade.GetUnavailableMessage(settings) == null,
            "explicit reload resumes cancelled selection");
        await facade.ShutdownAsync();
        Check(!running, "shutdown stops registered host");
        Check((await facade.EnsureResourceHostsAsync(settings)).Status == ResourceHostLoadStatus.Cancelled,
            "shutdown rejects new loads");
    }

    private static async Task AssetsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "HotkeyTranslatorLlmTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = new byte[] { 1, 2, 3, 4 };
            var asset = new ModelAssetDescriptor("test.gguf", "http://127.0.0.1/model", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
            var path = Path.Combine(root, asset.LocalFileName);
            var limits = new ModelAssetTimeouts(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100),
                TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
            using var hungHeaders = new HttpClient(new Handler(async token =>
            {
                await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            await Throws<TimeoutException>(() => ModelAssetProvisioner.EnsureAssetAsync(asset, path, "test", null,
                default, client: hungHeaders, timeouts: limits), "download header wait bounded");
            Check(!File.Exists(path + ".tmp"), "header timeout removes only temporary file");

            using var hungBody = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new IdleStream()) })));
            await Throws<TimeoutException>(() => ModelAssetProvisioner.EnsureAssetAsync(asset, path, "test", null,
                default, client: hungBody, timeouts: limits), "download idle reception bounded");
            Check(!File.Exists(path + ".tmp"), "idle timeout cleans partial download");

            using var cancellation = new CancellationTokenSource(80);
            await Throws<OperationCanceledException>(() => ModelAssetProvisioner.EnsureAssetAsync(asset, path, "test", null,
                cancellation.Token, client: hungBody), "download user cancellation distinguished from timeout");
            Check(!File.Exists(path + ".tmp"), "cancel cleans partial download");

            await using (var locked = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                await File.WriteAllBytesAsync(path + ".tmp", bytes);
                await Throws<TimeoutException>(() => ModelAssetProvisioner.EnsureAssetAsync(asset, path, "test", null,
                    default, client: hungBody, timeouts: limits), "asset lock wait bounded");
                Check(File.Exists(path + ".tmp"), "lock waiter does not delete another operation's partial file");
            }
            var phases = new List<HostLoadPhase>();
            using var valid = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes) })));
            await ModelAssetProvisioner.EnsureAssetAsync(asset, path, "test", null, default,
                progress: progress => phases.Add(progress.Phase), client: valid);
            Check(File.ReadAllBytes(path).SequenceEqual(bytes) && !File.Exists(path + ".tmp"), "validated download committed atomically");
            Check(phases.Contains(HostLoadPhase.Download) && phases.Contains(HostLoadPhase.Validation), "download and verification reported separately");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Throws<OperationCanceledException>(() => ModelAssetProvisioner.ValidateAssetAsync(path, asset, cancelled.Token), "model verification accepts cancellation");
            await ModelAssetProvisioner.EnsureAssetAsync(asset, path, "test", null, default, client: hungHeaders);
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "verified final asset reused without network");

            await Throws<InvalidDataException>(() => ModelAssetProvisioner.EnsureAssetAsync(
                asset with { Sha256 = new string('f', 64) }, path, "test", null, default, client: valid),
                "invalid checksum rejects replacement");
            Check(File.ReadAllBytes(path).SequenceEqual(bytes) && !File.Exists(path + ".tmp"), "failed replacement preserves existing model");
            var large = Path.Combine(root, "large.gguf");
            await using (var file = new FileStream(large, FileMode.Create, FileAccess.Write)) file.SetLength(64 * 1024 * 1024);
            using var hashCancel = new CancellationTokenSource();
            var verifying = ModelAssetProvisioner.ValidateAssetAsync(large, asset with { SizeBytes = null }, hashCancel.Token);
            hashCancel.Cancel();
            await Throws<OperationCanceledException>(() => verifying, "in-flight large model verification cancelled");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task PreparationAsync()
    {
        using var process = Process.Start(new ProcessStartInfo("ping.exe")
        {
            ArgumentList = { "-n", "30", "127.0.0.1" }, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await Throws<TimeoutException>(() => TestHost.WaitPreparation(process, default, TimeSpan.FromMilliseconds(100)),
            "environment preparation deadline");
        Check(process.HasExited, "timed out preparation process terminated");
        using var cancellation = new CancellationTokenSource(100);
        using var host = new TestHost(token => Task.Delay(Timeout.Infinite, token));
        await Throws<OperationCanceledException>(() => host.StartAsync(new AppSettings(), cancellation.Token),
            "startup cancellation preserved through host cleanup");
        try
        {
            using var remaining = Process.GetProcessById(host.StartedPid);
            Check(remaining.HasExited, "cancelled startup leaves no parent process");
        }
        catch (ArgumentException) { Check(true, "cancelled startup leaves no parent process"); }
    }

    private static async Task ShutdownDuringLoadAsync()
    {
        var busy = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        var registry = new GrpcHostRegistry(new[] { new GrpcHostDescriptor
        {
            HostId = "llama_grpc", ShouldLoad = _ => true, IsRunning = () => false,
            StartAsync = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); },
            Stop = () => { }, DisableOnFailure = _ => throw new Exception("must not disable on shutdown"),
            BusyMessage = _ => "loading", FailureLogMessage = "failed", FailureUserMessage = _ => "failed"
        }});
        using var facade = new ResourceHostFacade(() => null, (visible, _) => busy = visible, (_, _) => { },
            _ => notifications++, _ => { }, _ => { }, _ => { }, registry);
        var settings = new AppSettings { EnableLlamaCppTranslation = true, OcrEngine = OcrEngineKind.WinRt };
        var active = facade.EnsureResourceHostsAsync(settings);
        await entered.Task;
        using var queuedCancel = new CancellationTokenSource(80);
        var queued = await facade.EnsureResourceHostsAsync(settings, queuedCancel.Token);
        Check(queued.Status == ResourceHostLoadStatus.Cancelled && busy && facade.IsLoading,
            "cancelled gate waiter does not hide active operation");
        await facade.ShutdownAsync();
        Check((await active).Status == ResourceHostLoadStatus.Cancelled && !busy && notifications == 0,
            "shutdown drains in-flight startup before disposing gate without dialog");
    }

    private static async Task OtherHostsAsync()
    {
        var started = new List<string>();
        var stopped = new List<string>();
        var disabled = new List<string>();
        var descriptors = new[] { "paddle_grpc", "paddle_vl_grpc", "ndl_grpc" }.Select(id => new GrpcHostDescriptor
        {
            HostId = id, ShouldLoad = _ => true, IsRunning = () => false,
            StartAsync = (_, _) => { started.Add(id); if (id == "paddle_vl_grpc") throw new InvalidOperationException("failed"); return Task.CompletedTask; },
            Stop = () => stopped.Add(id), DisableOnFailure = _ => disabled.Add(id),
            BusyMessage = _ => "loading", FailureLogMessage = "failed", FailureUserMessage = _ => "failed"
        }).ToArray();
        var orchestrator = new GrpcHostOrchestrator(() => null, _ => { });
        var result = await orchestrator.EnsureHostsAsync(new AppSettings(), new GrpcHostRegistry(descriptors), default, new HashSet<string>());
        Check(started.Count == 3 && result.Failures.Count == 1 && result.SettingsChanged,
            "other OCR hosts keep independent startup and failure aggregation");
        Check(stopped.SequenceEqual(new[] { "paddle_vl_grpc" }) && disabled.SequenceEqual(stopped),
            "only failed OCR host is stopped and disabled");
    }

    private static async Task NativeAsync()
    {
        var root = Directory.GetCurrentDirectory();
        var runtime = Path.Combine(root, "TranslationServiceLlama", "LlamaCpp");
        foreach (var vision in new[] { false, true })
        {
            var project = Path.Combine(root, vision ? "OcrServiceVisionLlm" : "TranslationServiceLlama");
            var model = Path.Combine(runtime, "Models", vision ? "Qwen3.5-4B-Q4_K_M.gguf" : "Hy-MT2-1.8B-Q4_K_M.gguf");
            await RunServer(model, expectedFailure: false);
            var corrupt = Path.Combine(Path.GetTempPath(), "llm-invalid-" + Guid.NewGuid().ToString("N") + ".gguf");
            try
            {
                await File.WriteAllTextAsync(corrupt, "invalid GGUF", new System.Text.UTF8Encoding(false));
                await RunServer(corrupt, expectedFailure: true);
            }
            finally { File.Delete(corrupt); }

            async Task RunServer(string modelPath, bool expectedFailure)
            {
                var port = FreePort();
                var httpPort = FreePort();
                var info = new ProcessStartInfo(Path.Combine(project, ".venv", "Scripts", "python.exe"))
                {
                    WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                var nativePaths = Directory.GetDirectories(Path.Combine(project, ".venv", "Lib", "site-packages", "nvidia"),
                    "bin", SearchOption.AllDirectories);
                info.Environment["PATH"] = string.Join(Path.PathSeparator, nativePaths.Prepend(runtime)) + Path.PathSeparator + info.Environment["PATH"];
                foreach (var arg in new[] { "server.py", "--port", port.ToString(), "--llama-port", httpPort.ToString(),
                    "--llama-server", Path.Combine(runtime, "llama-server.exe"), "--model", modelPath,
                    "--gpu-layers", "0", "--ctx-size", "256", "--threads", "2", "--batch-size", "256",
                    "--ready-timeout-ms", "60000" }) info.ArgumentList.Add(arg);
                if (vision)
                {
                    info.ArgumentList.Add("--mmproj");
                    info.ArgumentList.Add(Path.Combine(runtime, "Models", "mmproj-Qwen3.5-4B-BF16.gguf"));
                }
                var phaseSeen = false;
                using var process = new Process { StartInfo = info };
                process.OutputDataReceived += (_, args) => { if (args.Data == "HOTKEY_TRANSLATOR_PHASE:connection") phaseSeen = true; };
                process.ErrorDataReceived += (_, _) => { };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                try
                {
                    using var channel = Grpc.Net.Client.GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
                    var translation = new Hotkey_Translator.TranslationGrpc.TranslationService.TranslationServiceClient(channel);
                    var ocr = new Hotkey_Translator.OcrGrpc.OcrService.OcrServiceClient(channel);
                    Task Wait() => GrpcReadyProbe.WaitAsync(60000, () => process.HasExited ? process.ExitCode : null,
                        async (deadline, token) => vision
                            ? (await ocr.HealthAsync(new(), deadline: deadline, cancellationToken: token)).Ready
                            : (await translation.HealthAsync(new(), deadline: deadline, cancellationToken: token)).Ready,
                        default);
                    if (expectedFailure)
                    {
                        var clock = Stopwatch.StartNew();
                        await Throws<HostProcessExitedException>(Wait, $"native {(vision ? "VisionLLM" : "Llama")} invalid model exits");
                        Check(clock.Elapsed.TotalSeconds < 15, "native failure detected without waiting 60 seconds");
                    }
                    else
                    {
                        await Wait();
                        Check(phaseSeen, $"native {(vision ? "VisionLLM" : "Llama")} model ready and connection phase reported");
                    }
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }

        static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class TestHost(Func<CancellationToken, Task> ready) : GrpcHostBase
    {
        public int StartedPid { get; private set; }
        public static Task WaitPreparation(Process process, CancellationToken token, TimeSpan limit) => WaitForPreparationAsync(process, token, limit);
        protected override string HostId => "test";
        protected override bool IsEnabled(AppSettings settings) => true;
        protected override Task<Process> StartProcessCoreAsync(AppSettings settings, CancellationToken token)
        {
            var process = StartProcessWithLogging(new ProcessStartInfo("ping.exe")
            {
                ArgumentList = { "-n", "30", "127.0.0.1" }, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }, "test");
            StartedPid = process.Id;
            return Task.FromResult(process);
        }
        protected override Task WaitForReadyCoreAsync(AppSettings settings, CancellationToken token) => ready(token);
        protected override GrpcHostRestartPolicy GetRestartPolicy(AppSettings settings) => new(0, TimeSpan.FromSeconds(1));
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(token);
    }

    private sealed class IdleStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

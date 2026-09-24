using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.Application;

internal static class Program
{
    private static readonly string[] Names = ["oneocr.dll", "oneocr.onemodel", "onnxruntime.dll"];

    private static async Task Main(string[] args)
    {
        if (args.Contains("--pipe"))
        {
            await FakeHelperAsync(args);
            return;
        }

        using var workspace = new Workspace();
        await TestTransactionsAsync(workspace.Path);
        await TestRollbackFailureAsync(workspace.Path);
        await TestHostAsync(workspace.Path);
        await TestPromptGateAsync();
        if (args.Contains("--native")) await TestNativeAsync(workspace.Path);
        Console.WriteLine("PASS: all OneOCR recovery checks.");
    }

    private static async Task TestTransactionsAsync(string root)
    {
        foreach (var mode in new[] { "success", "source-missing", "copy-locked", "probe-failed", "install-locked", "installed-probe-failed", "cancel", "partial-original" })
        {
            var source = System.IO.Path.Combine(root, mode, "source");
            var target = System.IO.Path.Combine(root, mode, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            foreach (var name in Names)
            {
                File.WriteAllText(System.IO.Path.Combine(source, name), "new " + name, Encoding.UTF8);
                File.WriteAllText(System.IO.Path.Combine(target, name), "old " + name, Encoding.UTF8);
            }
            File.WriteAllText(System.IO.Path.Combine(target, "keep.txt"), "unrelated", Encoding.UTF8);
            if (mode == "source-missing") File.Delete(System.IO.Path.Combine(source, Names[1]));
            if (mode == "partial-original") File.Delete(System.IO.Path.Combine(target, Names[1]));
            using var sourceLock = mode == "copy-locked" ? File.Open(System.IO.Path.Combine(source, Names[1]), FileMode.Open, FileAccess.Read, FileShare.None) : null;
            using var targetLock = mode == "install-locked" ? File.Open(System.IO.Path.Combine(target, Names[1]), FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            using var cts = new CancellationTokenSource();
            var checks = 0;
            Exception? failure = null;
            try
            {
                await OneOcrRepairService.RepairFilesAsync(source, target, directory =>
                {
                    checks++;
                    foreach (var name in Names) Check(File.ReadAllText(System.IO.Path.Combine(directory, name)) == "new " + name, "candidate must be a complete set");
                    if (checks == 1) Check(File.ReadAllText(System.IO.Path.Combine(target, Names[0])) == "old " + Names[0], "probe must precede replacement");
                    if (mode == "probe-failed" || (checks == 2 && mode is "installed-probe-failed" or "partial-original")) throw new IOException("Injected probe failure");
                    if (mode == "cancel" && checks == 2) cts.Cancel();
                    return Task.CompletedTask;
                }, cts.Token);
            }
            catch (Exception ex) { failure = ex; }

            Check((mode == "success") == (failure is null), mode + " failure expectation");
            foreach (var name in Names)
            {
                if (mode == "partial-original" && name == Names[1])
                    Check(!File.Exists(System.IO.Path.Combine(target, name)), "rollback restores missing files as missing");
                else
                    Check(File.ReadAllText(System.IO.Path.Combine(target, name)) == (mode == "success" ? "new " : "old ") + name, mode + " preserved set");
            }
            Check(File.ReadAllText(System.IO.Path.Combine(target, "keep.txt")) == "unrelated", "unrelated files preserved");
            Check(!Directory.EnumerateDirectories(target, ".oneocr-repair-*").Any(), "temporary files cleaned");
            Console.WriteLine("PASS transaction: " + mode);
        }
    }

    private static async Task TestRollbackFailureAsync(string root)
    {
        var source = System.IO.Path.Combine(root, "rollback-failure", "source");
        var target = System.IO.Path.Combine(root, "rollback-failure", "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        foreach (var name in Names)
        {
            File.WriteAllText(System.IO.Path.Combine(source, name), "new", Encoding.UTF8);
            File.WriteAllText(System.IO.Path.Combine(target, name), "original", Encoding.UTF8);
        }
        FileStream? locked = null;
        try
        {
            await OneOcrRepairService.RepairFilesAsync(source, target, directory =>
            {
                if (directory == target)
                {
                    locked = File.Open(System.IO.Path.Combine(target, Names[0]), FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new IOException("Injected post-install lock");
                }
                return Task.CompletedTask;
            }, CancellationToken.None);
            throw new Exception("Rollback failure was not reported");
        }
        catch (IOException ex)
        {
            Check(ex.InnerException is AggregateException, "both repair and rollback failures are retained");
            var workspace = Directory.GetDirectories(target, ".oneocr-repair-*").Single();
            foreach (var name in Names)
                Check(File.ReadAllText(System.IO.Path.Combine(workspace, "original", name)) == "original", "original bytes survive rollback failure");
            Check(ex.Message.Contains(workspace), "failure identifies the retained backup");
        }
        finally { locked?.Dispose(); }
        Console.WriteLine("PASS transaction: originals retained when rollback is blocked");
    }

    private static async Task TestHostAsync(string root)
    {
        foreach (var mode in new[] { "empty", "exit", "hang-start", "hang-ready", "error", "disconnect", "cancel", "hang-response" })
        {
            var vendor = System.IO.Path.Combine(root, "host-" + mode);
            Directory.CreateDirectory(vendor);
            File.WriteAllText(System.IO.Path.Combine(vendor, "mode"), mode, Encoding.UTF8);
            var settings = new AppSettings
            {
                OneOcrHelperRelativePath = Environment.ProcessPath!,
                OneOcrVendorRelativePath = vendor,
                OneOcrReadyTimeoutMs = 1000
            };
            using var host = new OneOcrProcessHost();
            using var cts = new CancellationTokenSource();
            if (mode == "cancel") cts.CancelAfter(500);
            var timer = Stopwatch.StartNew();
            Exception? failure = null;
            try
            {
                var response = await host.RecognizeAsync([1], settings, cts.Token);
                Check(response.Ok && response.Lines?.Length == 0, "empty recognition is not damage");
            }
            catch (Exception ex) { failure = ex; }
            Check(mode switch
            {
                "empty" => failure is null,
                "cancel" => failure is OperationCanceledException,
                _ => failure is OneOcrUnavailableException
            }, $"{mode}: unexpected exception {failure}");
            Check(timer.Elapsed < TimeSpan.FromSeconds(mode == "hang-response" ? 36 : 6), "failure must be bounded");
            Check(File.ReadAllLines(System.IO.Path.Combine(vendor, "starts")).Length == 1, "failed OCR must not auto-retry");

            // A canceled or failed response must not poison the next connection.
            File.WriteAllText(System.IO.Path.Combine(vendor, "mode"), "empty", Encoding.UTF8);
            Check((await host.RecognizeAsync([1], settings, CancellationToken.None)).Ok, "fresh request after failure");
            Console.WriteLine("PASS host: " + mode);
        }

        using var maintenanceHost = new OneOcrProcessHost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = maintenanceHost.RunMaintenanceAsync(async () => { entered.SetResult(); await release.Task; }, CancellationToken.None);
        await entered.Task;
        using var waitingCts = new CancellationTokenSource(100);
        try
        {
            await maintenanceHost.RecognizeAsync([1], new AppSettings(), waitingCts.Token);
            throw new Exception("OCR entered during maintenance");
        }
        catch (OperationCanceledException) { }
        release.SetResult();
        await maintenance;
        Console.WriteLine("PASS host: maintenance excludes OCR");
    }

    private static async Task TestPromptGateAsync()
    {
        var settings = new SettingsService(); // No load/save: never touches the user's configuration.
        settings.Settings.OcrEngine = OcrEngineKind.OneOcr;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        var pipelineLookups = 0;
        var drains = 0;
        var cleared = 0;
        using var coordinator = new MainWindowRunCoordinator(settings, new View(),
            () => { pipelineLookups++; return null; }, null!,
            () => { drains++; return false; },
            (_, _) => { prompts++; return release.Task; }, () => cleared++);
        var first = coordinator.ReportOneOcrFailureAsync();
        Check(coordinator.IsRunning, "run gate held while repair prompt is open");
        await coordinator.ReportOneOcrFailureAsync();
        Check(prompts == 1, "simultaneous failures share one prompt");
        release.SetResult();
        await first;
        await coordinator.ReportOneOcrFailureAsync();
        await coordinator.RunOnceAsync(ForceRunOptions.None with { Trigger = RunTrigger.AutoSceneChange });
        Check(prompts == 1 && pipelineLookups == 0 && drains == 0 && cleared > 0, "no background retry or prompt loop");
        await coordinator.RunOnceAsync(ForceRunOptions.None);
        Check(pipelineLookups == 1, "manual retry remains available");
        Check(!coordinator.IsRunning, "run gate released after recovery");
        Console.WriteLine("PASS recovery: duplicate prompts, auto suppression, manual retry");
    }

    private static async Task TestNativeAsync(string root)
    {
        var helper = System.IO.Path.GetFullPath("Native/OneOcrHelper/bin/OneOcrHelper.exe");
        var source = OneOcrVendorProvisioner.TryFindInstalledSnippingToolVendorDirectory()
            ?? throw new Exception("Native test needs a registered Snipping Tool with OCR assets.");
        var target = System.IO.Path.Combine(root, "native-repair");
        Directory.CreateDirectory(target);
        foreach (var name in Names) File.WriteAllText(System.IO.Path.Combine(target, name), "broken", Encoding.UTF8);
        var settings = new AppSettings { OneOcrHelperRelativePath = helper, OneOcrVendorRelativePath = target };
        using var host = new OneOcrProcessHost();
        await new OneOcrRepairService(null).RepairAsync(settings, host, CancellationToken.None);
        var result = await host.RecognizeAsync(OneOcrRepairService.CreateProbeImage(), settings, CancellationToken.None);
        Check(result.Ok && result.Lines!.Any(line => line.Text!.Contains("12345")), "real OCR after repair");
        foreach (var name in Names)
        {
            Check(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(System.IO.Path.Combine(source, name)))
                .SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(System.IO.Path.Combine(target, name)))), "native files copied from same package");
        }
        Console.WriteLine("PASS native: corrupt set repaired, text recognized, all three hashes match");
    }

    private static async Task FakeHelperAsync(string[] args)
    {
        string Arg(string name) => args[Array.IndexOf(args, name) + 1];
        var vendor = Arg("--vendor-dir");
        var mode = File.ReadAllText(System.IO.Path.Combine(vendor, "mode"));
        File.AppendAllText(System.IO.Path.Combine(vendor, "starts"), "start\n", Encoding.UTF8);
        if (mode == "exit") return;
        if (mode == "hang-start") { await Task.Delay(Timeout.Infinite); return; }
        using var pipe = new NamedPipeServerStream(Arg("--pipe"), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync();
        if (mode == "hang-ready") { await Task.Delay(Timeout.Infinite); return; }
        await WriteAsync(pipe, new { type = "ready", ok = true });
        while (true)
        {
            var header = new byte[4];
            await pipe.ReadExactlyAsync(header);
            var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
            await pipe.ReadExactlyAsync(body);
            if (mode == "disconnect") return;
            if (mode is "cancel" or "hang-response") { await Task.Delay(Timeout.Infinite); return; }
            await WriteAsync(pipe, new { type = "recognize", ok = mode != "error", error = mode == "error" ? "RunOcrPipeline failed with code 6." : null, lines = Array.Empty<object>() });
        }
    }

    private static async Task WriteAsync(Stream pipe, object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(body);
        await pipe.FlushAsync();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oneocr-repair-tests-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class View : IMainWindowViewBridge
    {
        public void AppendLog(string message) { }
        public void EnableOverlay() { }
        public void SetBusyOverlay(bool visible, string? message) { }
        public void SetBusyOverlayCancelable(bool visible) { }
        public void ShowLoadingSpinnerForRun(AppSettings settings) { }
        public void HideLoadingSpinnerForRun() { }
        public void CancelTranslationOverlay() { }
    }
}

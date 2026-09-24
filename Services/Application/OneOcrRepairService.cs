using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Application;

internal sealed class OneOcrRepairService
{
    private readonly AppLogger? _logger;

    public OneOcrRepairService(AppLogger? logger) => _logger = logger;

    public async Task RepairAsync(AppSettings settings, OneOcrProcessHost host, CancellationToken cancellationToken)
    {
        var status = new OneOcrVendorProvisioner().GetStatus(settings);
        if (!status.HelperExists)
        {
            throw new FileNotFoundException("OneOCR helper is missing. Restore the application's helper executable first.", status.HelperPath);
        }

        var source = OneOcrVendorProvisioner.TryFindInstalledSnippingToolVendorDirectory()
            ?? throw new DirectoryNotFoundException("The registered Snipping Tool package does not contain the three required OneOCR files.");
        var probeSettings = new AppSettings
        {
            EnableOneOcrHelper = true,
            OneOcrHelperRelativePath = status.HelperPath,
            OneOcrReadyTimeoutMs = settings.OneOcrReadyTimeoutMs,
            OneOcrMaxLineCount = settings.OneOcrMaxLineCount
        };

        await host.RunMaintenanceAsync(async () =>
        {
            await RepairFilesAsync(source, status.VendorDirectory, async directory =>
            {
                probeSettings.OneOcrVendorRelativePath = directory;
                using var probe = new OneOcrProcessHost(_logger);
                var response = await probe.RecognizeAsync(CreateProbeImage(), probeSettings, cancellationToken).ConfigureAwait(false);
                var text = string.Join(" ", response.Lines?.Select(line => line.Text) ?? Array.Empty<string?>());
                // WHY: A ready handshake alone misses model/inference failures and unusable OCR output.
                if (!text.Contains("ONEOCR", StringComparison.OrdinalIgnoreCase) || !text.Contains("12345", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("OneOCR could not recognize the repair test image.");
                }
            }, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        _logger?.Info($"stage=oneocr event=repair_completed source=\"{source}\" target=\"{status.VendorDirectory}\".");
    }

    internal static async Task RepairFilesAsync(
        string source, string target, Func<string, Task> validate, CancellationToken cancellationToken)
    {
        foreach (var name in OneOcrVendorProvisioner.RequiredVendorFileNames)
        {
            if (!File.Exists(Path.Combine(source, name)))
            {
                throw new FileNotFoundException($"Snipping Tool is missing {name}.");
            }
        }

        Directory.CreateDirectory(target);
        var work = Path.Combine(Path.GetFullPath(target), ".oneocr-repair-" + Guid.NewGuid().ToString("N"));
        var candidate = Path.Combine(work, "candidate");
        var backup = Path.Combine(work, "original");
        Directory.CreateDirectory(candidate);
        Directory.CreateDirectory(backup);
        var originals = new List<string>();
        var installed = new List<string>();
        var preserveBackup = false;
        try
        {
            foreach (var name in OneOcrVendorProvisioner.RequiredVendorFileNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(Path.Combine(source, name), Path.Combine(candidate, name));
            }
            await validate(candidate).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // NOTE: Touch only the three runtime assets; user files in the vendor folder stay in place.
                foreach (var name in OneOcrVendorProvisioner.RequiredVendorFileNames)
                {
                    var destination = Path.Combine(target, name);
                    if (File.Exists(destination))
                    {
                        File.Move(destination, Path.Combine(backup, name));
                        originals.Add(name);
                    }
                    File.Move(Path.Combine(candidate, name), destination);
                    installed.Add(name);
                }
                // WHY: Validate again at the final path to catch deployment permissions and DLL search differences.
                await validate(target).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                try
                {
                    // NOTE: Rollback must finish even when cancellation caused the repair to stop.
                    foreach (var name in installed) File.Delete(Path.Combine(target, name));
                    foreach (var name in originals) File.Move(Path.Combine(backup, name), Path.Combine(target, name));
                }
                catch (Exception rollbackFailure)
                {
                    preserveBackup = true;
                    throw new IOException($"Repair failed and automatic restoration failed. Original files are retained at {backup}.",
                        new AggregateException(failure, rollbackFailure));
                }
                throw;
            }
        }
        finally
        {
            // SECURITY: Only delete this operation's generated workspace, never a configured vendor directory.
            if (!preserveBackup)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (IOException) { /* NOTE: Cleanup cannot invalidate a successful repair or mask its original failure. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static byte[] CreateProbeImage()
    {
        using var bitmap = new Bitmap(640, 120);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 36, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString("ONEOCR TEST 12345", font, Brushes.Black, 20, 30);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}

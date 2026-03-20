using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hotkey_Translator.Models;
using Windows.Management.Deployment;

namespace Hotkey_Translator.Services.Application;

internal sealed record OneOcrVendorStatus(
    string HelperPath,
    bool HelperExists,
    string VendorDirectory,
    IReadOnlyList<string> MissingVendorFiles);

internal sealed record OneOcrVendorCopyResult(
    bool Succeeded,
    bool CopiedFiles,
    string VendorDirectory,
    string? SourceDirectory,
    string? ErrorMessage);

internal sealed class OneOcrVendorProvisioner
{
    private const string SnippingToolPackageFamilyName = "Microsoft.ScreenSketch_8wekyb3d8bbwe";
    private const string SnippingToolVendorDirectoryName = "SnippingTool";
    private static readonly string[] RequiredVendorFileNames =
    [
        "oneocr.dll",
        "oneocr.onemodel",
        "onnxruntime.dll"
    ];

    public OneOcrVendorStatus GetStatus(AppSettings settings)
    {
        var helperPath = ResolveConfiguredPath(settings.OneOcrHelperRelativePath);
        var vendorDirectory = ResolveConfiguredPath(settings.OneOcrVendorRelativePath);
        var missingVendorFiles = RequiredVendorFileNames
            .Where(fileName => !File.Exists(Path.Combine(vendorDirectory, fileName)))
            .ToArray();
        return new OneOcrVendorStatus(
            helperPath,
            File.Exists(helperPath),
            vendorDirectory,
            missingVendorFiles);
    }

    public OneOcrVendorCopyResult TryProvisionFromInstalledSnippingTool(AppSettings settings)
    {
        OneOcrVendorStatus status;
        try
        {
            status = GetStatus(settings);
        }
        catch (Exception ex)
        {
            return new OneOcrVendorCopyResult(false, false, string.Empty, null, ex.Message);
        }

        if (!status.HelperExists)
        {
            return new OneOcrVendorCopyResult(
                false,
                false,
                status.VendorDirectory,
                null,
                $"OneOCR helper was not found: {status.HelperPath}");
        }

        if (status.MissingVendorFiles.Count == 0)
        {
            return new OneOcrVendorCopyResult(true, false, status.VendorDirectory, null, null);
        }

        string? sourceDirectory;
        try
        {
            sourceDirectory = TryFindInstalledSnippingToolVendorDirectory();
        }
        catch (Exception ex)
        {
            return new OneOcrVendorCopyResult(
                false,
                false,
                status.VendorDirectory,
                null,
                $"Failed to query the installed Snipping Tool package: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return new OneOcrVendorCopyResult(
                false,
                false,
                status.VendorDirectory,
                null,
                "Installed Snipping Tool package was not found.");
        }

        var missingSourceFiles = RequiredVendorFileNames
            .Where(fileName => !File.Exists(Path.Combine(sourceDirectory, fileName)))
            .ToArray();
        if (missingSourceFiles.Length > 0)
        {
            return new OneOcrVendorCopyResult(
                false,
                false,
                status.VendorDirectory,
                sourceDirectory,
                $"Installed Snipping Tool package is missing required OneOCR files: {string.Join(", ", missingSourceFiles)}");
        }

        try
        {
            Directory.CreateDirectory(status.VendorDirectory);
            foreach (var fileName in RequiredVendorFileNames)
            {
                var sourcePath = Path.Combine(sourceDirectory, fileName);
                var destinationPath = Path.Combine(status.VendorDirectory, fileName);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            return new OneOcrVendorCopyResult(
                false,
                false,
                status.VendorDirectory,
                sourceDirectory,
                $"Failed to copy OneOCR vendor files: {ex.Message}");
        }

        var refreshedStatus = GetStatus(settings);
        if (refreshedStatus.MissingVendorFiles.Count > 0)
        {
            return new OneOcrVendorCopyResult(
                false,
                false,
                refreshedStatus.VendorDirectory,
                sourceDirectory,
                $"Copied OneOCR vendor files, but validation still failed for: {string.Join(", ", refreshedStatus.MissingVendorFiles)}");
        }

        return new OneOcrVendorCopyResult(true, true, refreshedStatus.VendorDirectory, sourceDirectory, null);
    }

    private static string? TryFindInstalledSnippingToolVendorDirectory()
    {
        var packageManager = new PackageManager();
        foreach (var package in packageManager.FindPackagesForUser(string.Empty, SnippingToolPackageFamilyName))
        {
            var installLocation = package?.InstalledLocation?.Path;
            if (string.IsNullOrWhiteSpace(installLocation))
            {
                continue;
            }

            var candidate = Path.Combine(installLocation, SnippingToolVendorDirectoryName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ResolveConfiguredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("OneOCR path is not configured.");
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var baseCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (File.Exists(baseCandidate) || Directory.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        var currentCandidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        if (File.Exists(currentCandidate) || Directory.Exists(currentCandidate))
        {
            return currentCandidate;
        }

        var baseParent = Path.GetDirectoryName(baseCandidate);
        var currentParent = Path.GetDirectoryName(currentCandidate);

        // WHY: Repo-local runs usually resolve from the working directory, while packaged runs keep the expected
        // folder structure next to the executable. Prefer whichever parent directory already exists.
        if (!string.IsNullOrWhiteSpace(currentParent) &&
            Directory.Exists(currentParent) &&
            (string.IsNullOrWhiteSpace(baseParent) || !Directory.Exists(baseParent)))
        {
            return currentCandidate;
        }

        return baseCandidate;
    }
}

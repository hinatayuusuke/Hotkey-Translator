using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hotkey_Translator.Services.GrpcHost;

internal enum HostLoadPhase { Environment, Lock, Download, Validation, Model, Connection }
internal enum ResourceHostLoadStatus { Succeeded, Failed, Cancelled }

internal sealed record HostLoadProgress(string HostId, HostLoadPhase Phase, string? Asset = null,
    long Bytes = 0, long? TotalBytes = null, long OperationId = 0);

internal sealed record HostLoadFailure(string HostId, string Model, string? Mmproj,
    HostLoadPhase Phase, string Reason, string Details);

internal sealed record ResourceHostLoadResult(ResourceHostLoadStatus Status, bool SettingsChanged,
    IReadOnlyList<HostLoadFailure> Failures)
{
    public static ResourceHostLoadResult Success { get; } = new(ResourceHostLoadStatus.Succeeded, false, Array.Empty<HostLoadFailure>());
    public static ResourceHostLoadResult Cancelled { get; } = new(ResourceHostLoadStatus.Cancelled, false, Array.Empty<HostLoadFailure>());
}

internal sealed class HostProcessExitedException(int exitCode)
    : InvalidOperationException($"Process exited during startup (exit code: {exitCode}).");

internal static class HostLoadDiagnostics
{
    public static string Sanitize(string value)
    {
        // SECURITY: Startup output may contain download credentials; never expose URLs or tokens in a dialog.
        value = Regex.Replace(value, @"https?://[^\s""']+", "<url>", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"(?i)bearer\s+[^\s,;]+", "Bearer <redacted>");
        value = Regex.Replace(value, @"(?i)(authorization|api[_-]?key|token|password|secret)(\s*[=:]\s*|\s+)[^\s,;]+", "$1=<redacted>");
        return value.Length <= 4096 ? value : value[^4096..];
    }
}

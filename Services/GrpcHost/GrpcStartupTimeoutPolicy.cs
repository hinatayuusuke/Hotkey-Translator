using System;
using System.IO;

namespace Hotkey_Translator.Services.GrpcHost;

internal static class GrpcStartupTimeoutPolicy
{
    private const int BootstrapReadyTimeoutMs = 900000;

    public static int ResolveReadyTimeoutMs(int configuredTimeoutMs, string projectDir, out bool bootstrapMode)
    {
        var normalizedConfiguredTimeoutMs = Math.Max(1000, configuredTimeoutMs);
        var venvDir = Path.Combine(projectDir, ".venv");
        bootstrapMode = !Directory.Exists(venvDir);
        if (!bootstrapMode)
        {
            return normalizedConfiguredTimeoutMs;
        }

        // WHY: First run can spend minutes creating .venv and downloading dependencies.
        return Math.Max(normalizedConfiguredTimeoutMs, BootstrapReadyTimeoutMs);
    }
}

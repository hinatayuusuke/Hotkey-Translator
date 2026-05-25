using System;
using System.Collections.Generic;
using System.IO;

namespace Hotkey_Translator.Services;

internal static class LlamaCppRuntimeLayout
{
    // NOTE: llama-server.exe is a small launcher in current llama.cpp Windows builds.
    // The impl/common DLLs and CPU backend variants must stay beside it so both
    // translation and VisionLLM OCR can load the same runtime layout.
    public static IReadOnlyList<string> RequiredRuntimeFileNames { get; } = new[]
    {
        "llama-server.exe",
        "llama-server-impl.dll",
        "llama-common.dll",
        "llama.dll",
        "mtmd.dll",
        "ggml.dll",
        "ggml-base.dll",
        "ggml-cuda.dll",
        "libomp140.x86_64.dll",
        "ggml-cpu-alderlake.dll",
        "ggml-cpu-cannonlake.dll",
        "ggml-cpu-cascadelake.dll",
        "ggml-cpu-cooperlake.dll",
        "ggml-cpu-haswell.dll",
        "ggml-cpu-icelake.dll",
        "ggml-cpu-ivybridge.dll",
        "ggml-cpu-piledriver.dll",
        "ggml-cpu-sandybridge.dll",
        "ggml-cpu-sapphirerapids.dll",
        "ggml-cpu-skylakex.dll",
        "ggml-cpu-sse42.dll",
        "ggml-cpu-x64.dll",
        "ggml-cpu-zen4.dll",
    };

    public static void ValidateRequiredRuntimeFiles(string llamaCppDirectory, string owner)
    {
        var missing = new List<string>();
        foreach (var fileName in RequiredRuntimeFileNames)
        {
            var path = Path.Combine(llamaCppDirectory, fileName);
            if (!File.Exists(path))
            {
                missing.Add(path);
            }
        }

        if (missing.Count > 0)
        {
            throw new FileNotFoundException(
                $"Required llama.cpp runtime files are missing for {owner}:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
        }
    }
}

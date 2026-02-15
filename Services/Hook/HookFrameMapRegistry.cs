using System.Collections.Concurrent;

namespace Hotkey_Translator.Services.Hook;

internal static class HookFrameMapRegistry
{
    private static readonly ConcurrentDictionary<int, string> FrameMapsByPid = new();

    public static void Set(int pid, string frameMap)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(frameMap))
        {
            return;
        }

        FrameMapsByPid[pid] = frameMap.Trim();
    }

    public static void Clear(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        FrameMapsByPid.TryRemove(pid, out _);
    }

    public static bool TryGet(int pid, out string frameMap)
    {
        if (pid <= 0)
        {
            frameMap = string.Empty;
            return false;
        }

        return FrameMapsByPid.TryGetValue(pid, out frameMap!);
    }
}


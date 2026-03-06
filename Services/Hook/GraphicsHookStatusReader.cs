using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Hook;

internal static class GraphicsHookStatusReader
{
    private const uint StatusMagic = 0x48535453; // "HSTS"
    private const uint StatusVersion = 1;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct HookStatusHeader
    {
        public uint Magic;
        public uint Version;
        public uint Api;
        public uint TargetPid;
        public ulong PresentCount;
        public ulong LastPresentQpc;
        public uint LastPresentKind;
        public uint BackBufferDxgiFormat;
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public uint StagingDxgiFormat;
        public uint Reserved0;
        public ulong LastFrameIdWritten;
        public ulong LastFrameWriteQpc;
        public ulong LastCmdQpc;
        public uint LastCmdCount;
        public uint Reserved1;
        public ulong UpdatedQpc;
    }

    public static bool TryRead(int pid, GraphicsHookApiKind api, out HookStatusHeader status)
    {
        status = default;
        if (pid <= 0)
        {
            return false;
        }

        var name = BuildStatusMapName(pid, api);
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf<HookStatusHeader>(), MemoryMappedFileAccess.Read);
            accessor.Read(0, out status);
            return status.Magic == StatusMagic &&
                   status.Version == StatusVersion &&
                   status.Api == unchecked((uint)api);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadAny(int pid, out HookStatusHeader status, out GraphicsHookApiKind api)
    {
        status = default;
        api = GraphicsHookApiKind.Dx11;
        if (pid <= 0)
        {
            return false;
        }

        var apis = new[]
        {
            GraphicsHookApiKind.Dx11,
            GraphicsHookApiKind.Vulkan,
            GraphicsHookApiKind.Dx9,
            GraphicsHookApiKind.Dx12,
            GraphicsHookApiKind.OpenGl
        };

        foreach (var candidate in apis)
        {
            if (!TryRead(pid, candidate, out status))
            {
                continue;
            }

            api = candidate;
            return true;
        }

        return false;
    }

    public static string BuildStatusMapName(int pid, GraphicsHookApiKind api)
    {
        return $@"Local\HT_HOOK_STAT_{unchecked((uint)api)}_{pid}";
    }
}

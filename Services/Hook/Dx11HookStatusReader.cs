using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Hotkey_Translator.Services.Hook;

internal static class Dx11HookStatusReader
{
    private const uint StatusMagic = 0x48535453; // "HSTS"
    private const uint StatusVersion = 1;
    private const uint GraphicsApiDx11 = 1;

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

    public static bool TryRead(int pid, out HookStatusHeader status)
    {
        status = default;
        if (pid <= 0)
        {
            return false;
        }

        var name = $@"Local\HT_HOOK_STAT_{GraphicsApiDx11}_{pid}";
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf<HookStatusHeader>(), MemoryMappedFileAccess.Read);
            accessor.Read(0, out status);
            return status.Magic == StatusMagic && status.Version == StatusVersion && status.Api == GraphicsApiDx11;
        }
        catch
        {
            return false;
        }
    }
}


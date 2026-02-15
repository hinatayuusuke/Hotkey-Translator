using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hotkey_Translator.Services.Hook;

internal sealed class Dx11HookOverlayCommandWriter : IDisposable
{
    private const int MappingBytes = 64 * 1024;
    private const uint OverlayCmdMagic = 0x48434D44; // "HCMD"
    private const uint OverlayCmdVersion = 1;
    private const uint GraphicsApiDx11 = 1;

    private int _pid;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct OverlayCommandHeader
    {
        public uint Magic;
        public uint Version;
        public uint Api;
        public uint TargetPid;
        public uint CommandCount;
        public uint PayloadBytes;
        public uint Reserved0;
        public uint Reserved1;
        public ulong UpdatedQpc;
    }

    public bool TryWrite(int pid, int commandCount, byte[] payload)
    {
        if (pid <= 0)
        {
            return false;
        }

        if (commandCount < 0)
        {
            commandCount = 0;
        }

        var headerBytes = Marshal.SizeOf<OverlayCommandHeader>();
        var payloadBytes = payload.Length;
        if (payloadBytes > MappingBytes - headerBytes)
        {
            return false;
        }

        if (!Ensure(pid))
        {
            return false;
        }

        try
        {
            // NOTE: Must match Native/HookCommon/SharedOverlayCommands.cpp ordering:
            // write payload first, then write header last with an updatedQpc.
            _accessor!.WriteArray(headerBytes, payload, 0, payloadBytes);
            Thread.MemoryBarrier();

            var header = new OverlayCommandHeader
            {
                Magic = OverlayCmdMagic,
                Version = OverlayCmdVersion,
                Api = GraphicsApiDx11,
                TargetPid = unchecked((uint)pid),
                CommandCount = unchecked((uint)commandCount),
                PayloadBytes = unchecked((uint)payloadBytes),
                UpdatedQpc = NowQpc()
            };
            _accessor.Write(0, ref header);
            Thread.MemoryBarrier();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Reset()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
        _pid = 0;
    }

    private bool Ensure(int pid)
    {
        if (_pid == pid && _mmf != null && _accessor != null)
        {
            return true;
        }

        Reset();
        _pid = pid;

        var name = $@"Local\HT_HOOK_CMD_{GraphicsApiDx11}_{pid}";
        try
        {
            _mmf = MemoryMappedFile.CreateOrOpen(name, MappingBytes, MemoryMappedFileAccess.ReadWrite);
            _accessor = _mmf.CreateViewAccessor(0, MappingBytes, MemoryMappedFileAccess.ReadWrite);
            return true;
        }
        catch
        {
            Reset();
            return false;
        }
    }

    public void Dispose()
    {
        Reset();
    }

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    private static ulong NowQpc()
    {
        if (!QueryPerformanceCounter(out var qpc))
        {
            return 0;
        }

        return unchecked((ulong)qpc);
    }
}


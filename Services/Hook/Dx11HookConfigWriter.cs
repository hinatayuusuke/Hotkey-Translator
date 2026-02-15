using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hotkey_Translator.Services.Hook;

internal sealed class Dx11HookConfigWriter : IDisposable
{
    private const int MappingBytes = 4096;
    private const uint ConfigMagic = 0x48435446; // "HCTF"
    private const uint ConfigVersion = 1;
    private const uint GraphicsApiDx11 = 1;

    private int _pid;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HookConfigHeader
    {
        public uint Magic;
        public uint Version;
        public uint Api;
        public uint TargetPid;
        public uint CaptureFpsLimit;
        public uint OverlayEnabled;
        public uint Reserved0;
        public uint Reserved1;
        public ulong UpdatedQpc;
    }

    public bool TryWrite(int pid, int captureFpsLimit, bool overlayEnabled)
    {
        if (pid <= 0)
        {
            return false;
        }

        if (!Ensure(pid))
        {
            return false;
        }

        try
        {
            var header = new HookConfigHeader
            {
                Magic = ConfigMagic,
                Version = ConfigVersion,
                Api = GraphicsApiDx11,
                TargetPid = unchecked((uint)pid),
                CaptureFpsLimit = unchecked((uint)Math.Max(1, captureFpsLimit)),
                OverlayEnabled = overlayEnabled ? 1u : 0u,
                UpdatedQpc = NowQpc()
            };
            _accessor!.Write(0, ref header);
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

        var name = $@"Local\HT_HOOK_CFG_{GraphicsApiDx11}_{pid}";
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


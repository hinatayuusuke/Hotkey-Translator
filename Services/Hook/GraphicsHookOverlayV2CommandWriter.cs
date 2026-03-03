using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Hook;

internal sealed class GraphicsHookOverlayV2CommandWriter : IDisposable
{
    private const int MappingBytes = 256 * 1024;

    // "HOV2" little-endian.
    private const uint OverlayV2Magic = 0x32564F48;
    private const uint OverlayV2Version = 2;
    private const int MaxTextBlocks = 64;

    private static readonly int HeaderBytes = Marshal.SizeOf<OverlayV2Header>();
    private static readonly int BlockBytes = Marshal.SizeOf<TextBlockV2>();

    private int _pid;
    private GraphicsHookApiKind _api = GraphicsHookApiKind.Dx11;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private ulong _nextSeq;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct OverlayV2Header
    {
        public uint Magic;
        public uint Version;
        public uint Api;
        public uint TargetPid;
        public ulong UpdatedSeq;
        public uint CanvasW;
        public uint CanvasH;
        public uint TextBlockCount;
        public uint TextBytes;
        public uint Flags;
        public uint Reserved0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TextBlockV2
    {
        public float X;
        public float Y;
        public float W;
        public float H;
        public float PaddingPx;
        public float RoundingPx;
        public float FontPx;
        public uint FgArgb;
        public uint BgArgb;
        public uint Wrap;
        public uint TextOffset;
        public uint TextLen;
        public int ZOrder;
    }

    public bool TryWrite(
        int pid,
        GraphicsHookApiKind api,
        uint canvasW,
        uint canvasH,
        ReadOnlySpan<TextBlockV2> blocks,
        byte[] textBlob,
        int textBytes,
        uint flags = 0)
    {
        if (pid <= 0)
        {
            return false;
        }

        if (!Ensure(pid, api))
        {
            return false;
        }

        try
        {
            var count = Math.Min(blocks.Length, MaxTextBlocks);
            var blocksBytes = checked(count * BlockBytes);
            if (textBytes < 0)
            {
                return false;
            }

            var blobBytes = Math.Min(textBytes, textBlob.Length);
            if (blocksBytes + blobBytes > MappingBytes - HeaderBytes)
            {
                // WHY: The caller must ensure text offsets/lengths stay valid; don't truncate silently here.
                return false;
            }

            var blockOffset = HeaderBytes;
            for (var i = 0; i < count; i++)
            {
                var block = blocks[i];
                _accessor!.Write(blockOffset + (i * BlockBytes), ref block);
            }

            if (blobBytes > 0)
            {
                var blobOffset = HeaderBytes + blocksBytes;
                // NOTE: Payload first, then header last with UpdatedSeq.
                _accessor!.WriteArray(blobOffset, textBlob, 0, blobBytes);
            }

            Thread.MemoryBarrier();

            var header = new OverlayV2Header
            {
                Magic = OverlayV2Magic,
                Version = OverlayV2Version,
                Api = unchecked((uint)api),
                TargetPid = unchecked((uint)pid),
                UpdatedSeq = ++_nextSeq,
                CanvasW = canvasW,
                CanvasH = canvasH,
                TextBlockCount = unchecked((uint)count),
                TextBytes = unchecked((uint)blobBytes),
                Flags = flags
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
        _api = GraphicsHookApiKind.Dx11;
        _nextSeq = 0;
    }

    private bool Ensure(int pid, GraphicsHookApiKind api)
    {
        if (_pid == pid && _api == api && _mmf != null && _accessor != null)
        {
            return true;
        }

        Reset();
        _pid = pid;
        _api = api;

        var name = $@"Local\HT_HOOK_OVL_{unchecked((uint)api)}_{pid}";
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
}

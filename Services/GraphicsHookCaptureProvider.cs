using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services.Hook;

namespace Hotkey_Translator.Services;

public sealed class GraphicsHookCaptureProvider : ICaptureProvider
{
    private const uint FrameHeaderMagic = 0x48465452; // "HFTR"
    private const uint FramePipeVersion = 2;
    private const uint FramePipeSlotCount = 2;
    private const uint PixelFormatBgra8 = 1;
    private const int MaxReadAttempts = 5;
    private const int MaxPayloadBytes = 7680 * 4320 * 4;

    internal GraphicsHookCaptureProvider(AppLogger logger, LauncherSessionTargetState launcherSessionTargetState)
    {
        _logger = logger;
        _launcherSessionTargetState = launcherSessionTargetState;
    }

    public CaptureProviderKind Kind => CaptureProviderKind.GraphicsHook;

    public bool IsEnabled(AppSettings settings)
    {
        // WHY: Launcher provisional attach can publish the real target window before settings persistence catches up.
        return settings.EnableGraphicsHookPipeline &&
               settings.CaptureMode == CaptureMode.ActiveWindow &&
               _launcherSessionTargetState.TryResolveEffectiveWindowHandle(settings, out _);
    }

    public bool TryGetBounds(CaptureRequest request, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (request.Mode != CaptureMode.ActiveWindow)
        {
            return false;
        }

        if (!TryGetBoundTargetHwnd(request, out var hwnd))
        {
            return false;
        }

        bounds = GetActiveWindowBounds(hwnd) ?? Rect.Empty;
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool TryCapture(CaptureRequest request, out CaptureFrame frame, out string? error)
    {
        frame = null!;
        error = null;

        if (request.Mode != CaptureMode.ActiveWindow)
        {
            error = "Hook capture only supports active window mode.";
            return false;
        }

        if (!TryGetBoundTargetHwnd(request, out var hwnd))
        {
            error = "Hook capture requires a bound fixed target window.";
            return false;
        }

        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            error = "Invalid capture target window handle.";
            return false;
        }

        if (!TryGetBounds(request, out var bounds))
        {
            error = "Failed to resolve capture bounds.";
            return false;
        }

        if (!TryGetWindowPid(hwnd, out var pid) || pid <= 0)
        {
            error = "Failed to resolve capture target process id.";
            return false;
        }

        if (!TryResolveFrameMappingName(pid, out var mappingName, out var header, out var bitmap, out var readError))
        {
            error = readError ?? "Hook shared frame read failed.";
            return false;
        }

        try
        {
            UpdateCache(pid, mappingName, header.FrameId, bitmap);
            frame = new CaptureFrame(bitmap, bounds, Kind, DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.Error(ex, "Hook capture bitmap decode failed.");
            return false;
        }
    }

    private readonly AppLogger _logger;
    private readonly LauncherSessionTargetState _launcherSessionTargetState;
    private readonly object _cacheLock = new();
    private readonly object _mappingLock = new();
    private int _openedPid;
    private string _openedMapName = string.Empty;
    private MemoryMappedFile? _openedMemoryMappedFile;
    private MemoryMappedViewAccessor? _openedAccessor;
    private int _cachedPid;
    private string _cachedMapName = string.Empty;
    private ulong _cachedFrameId;
    private Bitmap? _cachedBitmap;
    private DateTimeOffset _cachedBitmapUpdatedAtUtc;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SharedFramePipeHeader
    {
        public uint Magic;
        public uint Version;
        public uint Api;
        public uint ProducerPid;
        public uint SlotCount;
        public uint PayloadCapacity;
        public uint PublishedIndex;
        public uint Reserved0;
        public ulong PublishedSeq;
        public ulong Reserved1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SharedFrameHeader
    {
        public ulong FrameId;
        public uint Width;
        public uint Height;
        public uint Stride;
        public uint PayloadBytes;
        public uint PixelFormat;
        public ulong TimestampQpc;
        public ulong SlotSeq;
    }

    private static string BuildFrameMappingName(int pid, GraphicsHookApiKind api)
    {
        // NOTE: Must match Native/HookCommon/HookIpcProtocol.h naming.
        return $@"Local\HT_HOOK_FRAME_{unchecked((uint)api)}_{pid}_V2";
    }

    private bool TryResolveFrameMappingName(
        int pid,
        out string mappingName,
        out SharedFrameHeader header,
        out Bitmap bitmap,
        out string? error)
    {
        header = default;
        bitmap = null!;
        error = null;

        if (HookFrameMapRegistry.TryGet(pid, out var dynamicMap) &&
            !string.IsNullOrWhiteSpace(dynamicMap) &&
            TryReadBitmap(pid, dynamicMap, out header, out bitmap, out error))
        {
            mappingName = dynamicMap;
            return true;
        }

        var fallbacks = new[]
        {
            GraphicsHookApiKind.Dx11,
            GraphicsHookApiKind.Vulkan,
            GraphicsHookApiKind.Dx9
        };
        foreach (var api in fallbacks)
        {
            var candidate = BuildFrameMappingName(pid, api);
            if (!TryReadBitmap(pid, candidate, out header, out bitmap, out error))
            {
                continue;
            }

            mappingName = candidate;
            HookFrameMapRegistry.Set(pid, candidate);
            return true;
        }

        mappingName = string.Empty;
        return false;
    }

    private bool TryReadBitmap(
        int pid,
        string mappingName,
        out SharedFrameHeader header,
        out Bitmap bitmap,
        out string? error)
    {
        var pipeHeaderBytes = Marshal.SizeOf<SharedFramePipeHeader>();
        var frameHeaderBytes = Marshal.SizeOf<SharedFrameHeader>();
        header = default;
        bitmap = null!;
        error = null;

        try
        {
            lock (_mappingLock)
            {
                if (!TryGetAccessorLocked(pid, mappingName, out var accessor))
                {
                    error = $"Hook shared frame mapping not found. map=\"{mappingName}\" pid={pid}.";
                    return false;
                }

                for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
                {
                    accessor.Read(0, out SharedFramePipeHeader pipeHeader);
                    if (!ValidatePipeHeader(pipeHeader, out var pipeError))
                    {
                        error = pipeError;
                        return false;
                    }

                    var slotHeaderOffset = checked((long)(pipeHeaderBytes + ((frameHeaderBytes + pipeHeader.PayloadCapacity) * pipeHeader.PublishedIndex)));
                    accessor.Read(slotHeaderOffset, out header);
                    if (!ValidateFrameHeader(pipeHeader, header, out var frameError))
                    {
                        error = frameError;
                        return false;
                    }

                    // WHY: Same frameId means the published slot did not advance, so cloning is cheaper than decoding again.
                    if (TryCloneCached(pid, mappingName, header.FrameId, out bitmap))
                    {
                        return true;
                    }

                    var payloadBytes = checked((int)header.PayloadBytes);
                    var payloadOffset = checked(slotHeaderOffset + frameHeaderBytes);
                    var payload = new byte[payloadBytes];
                    accessor.ReadArray(payloadOffset, payload, 0, payloadBytes);

                    accessor.Read(0, out SharedFramePipeHeader confirmPipeHeader);
                    if (confirmPipeHeader.PublishedSeq == pipeHeader.PublishedSeq &&
                        confirmPipeHeader.PublishedIndex == pipeHeader.PublishedIndex &&
                        header.SlotSeq == pipeHeader.PublishedSeq)
                    {
                        bitmap = CreateBitmapFromBgraPayload(header, payload);
                        return true;
                    }

                    if (attempt == MaxReadAttempts - 1)
                    {
                        break;
                    }
                }
            }

            if (TryCloneLatestCached(pid, mappingName, out bitmap, out var cacheAgeMs))
            {
                _logger.Info(
                    $"stage=capture event=hook_cached_reuse reason=writer_race map=\"{mappingName}\" pid={pid} ageMs={cacheAgeMs:0}.");
                return true;
            }

            error = "Frame was unstable (writer race).";
            return false;
        }
        catch (FileNotFoundException)
        {
            lock (_mappingLock)
            {
                if (_openedPid == pid && string.Equals(_openedMapName, mappingName, StringComparison.Ordinal))
                {
                    ResetOpenedMappingLocked();
                }
            }

            if (GraphicsHookStatusReader.TryReadAny(pid, out var status, out var statusApi))
            {
                error =
                    $"Hook shared frame mapping not found. map=\"{mappingName}\" pid={pid}. " +
                    $"status.api={statusApi} " +
                    $"status.presentCount={status.PresentCount} kind={status.LastPresentKind} " +
                    $"bb={status.BackBufferWidth}x{status.BackBufferHeight} dxgi={status.BackBufferDxgiFormat} " +
                    $"cmdCount={status.LastCmdCount}.";
            }
            else
            {
                error = $"Hook shared frame mapping not found. map=\"{mappingName}\" pid={pid}.";
            }
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryGetAccessorLocked(int pid, string mappingName, out MemoryMappedViewAccessor accessor)
    {
        if (_openedAccessor != null &&
            _openedMemoryMappedFile != null &&
            _openedPid == pid &&
            string.Equals(_openedMapName, mappingName, StringComparison.Ordinal))
        {
            accessor = _openedAccessor;
            return true;
        }

        ResetOpenedMappingLocked();
        _openedMemoryMappedFile = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.Read);
        _openedAccessor = _openedMemoryMappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _openedPid = pid;
        _openedMapName = mappingName;
        accessor = _openedAccessor;
        return true;
    }

    private void ResetOpenedMappingLocked()
    {
        _openedAccessor?.Dispose();
        _openedAccessor = null;
        _openedMemoryMappedFile?.Dispose();
        _openedMemoryMappedFile = null;
        _openedPid = 0;
        _openedMapName = string.Empty;
    }

    private static bool ValidatePipeHeader(SharedFramePipeHeader header, out string? error)
    {
        error = null;

        if (header.Magic != FrameHeaderMagic)
        {
            error = "Hook frame pipe not initialized.";
            return false;
        }

        if (header.Version != FramePipeVersion)
        {
            error = $"Unexpected hook frame pipe version: {header.Version}.";
            return false;
        }

        if (!Enum.IsDefined(typeof(GraphicsHookApiKind), header.Api) || header.Api == 0)
        {
            error = $"Unexpected hook api: {header.Api}.";
            return false;
        }

        if (header.SlotCount != FramePipeSlotCount)
        {
            error = $"Unexpected hook frame slot count: {header.SlotCount}.";
            return false;
        }

        if (header.PublishedIndex >= header.SlotCount)
        {
            error = $"Unexpected hook frame slot index: {header.PublishedIndex}.";
            return false;
        }

        if (header.PayloadCapacity == 0 || header.PayloadCapacity > MaxPayloadBytes)
        {
            error = $"Unexpected hook payload capacity: {header.PayloadCapacity}.";
            return false;
        }

        if (header.PublishedSeq == 0)
        {
            error = "Hook frame pipe not initialized.";
            return false;
        }

        return true;
    }

    private static bool ValidateFrameHeader(SharedFramePipeHeader pipeHeader, SharedFrameHeader header, out string? error)
    {
        error = null;

        if (header.FrameId == 0 || header.SlotSeq == 0)
        {
            error = "Hook frame slot not initialized.";
            return false;
        }

        if (header.SlotSeq != pipeHeader.PublishedSeq)
        {
            error = "Hook frame slot sequence mismatch.";
            return false;
        }

        if (header.PixelFormat != PixelFormatBgra8)
        {
            error = $"Unexpected hook pixel format: {header.PixelFormat}.";
            return false;
        }

        if (header.Width == 0 || header.Height == 0 || header.Stride == 0)
        {
            error = "Invalid hook frame header dimensions.";
            return false;
        }

        if (header.PayloadBytes == 0 || header.PayloadBytes > pipeHeader.PayloadCapacity)
        {
            error = $"Invalid payload size: {header.PayloadBytes}.";
            return false;
        }

        return true;
    }

    private bool TryCloneCached(int pid, string mapName, ulong frameId, out Bitmap bitmap)
    {
        bitmap = null!;
        lock (_cacheLock)
        {
            if (_cachedBitmap == null)
            {
                return false;
            }

            if (_cachedPid != pid || !string.Equals(_cachedMapName, mapName, StringComparison.Ordinal) || _cachedFrameId != frameId)
            {
                return false;
            }

            // WHY: Caller owns disposal of the returned bitmap, so provide a deep copy.
            bitmap = (Bitmap)_cachedBitmap.Clone();
            return true;
        }
    }

    private void UpdateCache(int pid, string mapName, ulong frameId, Bitmap bitmap)
    {
        lock (_cacheLock)
        {
            _cachedPid = pid;
            _cachedMapName = mapName;
            _cachedFrameId = frameId;
            _cachedBitmap?.Dispose();
            _cachedBitmap = (Bitmap)bitmap.Clone();
            _cachedBitmapUpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private bool TryCloneLatestCached(int pid, string mapName, out Bitmap bitmap, out double ageMs)
    {
        bitmap = null!;
        ageMs = 0;
        lock (_cacheLock)
        {
            if (_cachedBitmap == null)
            {
                return false;
            }

            if (_cachedPid != pid || !string.Equals(_cachedMapName, mapName, StringComparison.Ordinal))
            {
                return false;
            }

            ageMs = (DateTimeOffset.UtcNow - _cachedBitmapUpdatedAtUtc).TotalMilliseconds;
            bitmap = (Bitmap)_cachedBitmap.Clone();
            return true;
        }
    }

    private static Bitmap CreateBitmapFromBgraPayload(SharedFrameHeader header, byte[] payload)
    {
        var width = checked((int)header.Width);
        var height = checked((int)header.Height);
        var stride = checked((int)header.Stride);

        if (stride < width * 4)
        {
            throw new InvalidOperationException($"Invalid stride: {stride}.");
        }

        var expectedBytes = checked(stride * height);
        if (expectedBytes != payload.Length)
        {
            // NOTE: We currently require a full contiguous image payload.
            throw new InvalidOperationException($"Unexpected payload size: {payload.Length} != {expectedBytes}.");
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                var dstRow = IntPtr.Add(data.Scan0, y * data.Stride);

                // Copy only the active pixels; row pitch may include padding.
                Marshal.Copy(payload, y * stride, dstRow, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static bool TryGetBoundTargetHwnd(CaptureRequest request, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (!request.TargetWindowHandle.HasValue)
        {
            return false;
        }

        hwnd = request.TargetWindowHandle.Value;
        return hwnd != IntPtr.Zero;
    }

    private static bool TryGetWindowPid(IntPtr hwnd, out int pid)
    {
        pid = 0;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var nativePid);
        pid = unchecked((int)nativePid);
        return pid > 0;
    }

    private static Rect? GetActiveWindowBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        // WHY: Hooked backbuffer dimensions typically correspond to the client area, not the extended frame bounds.
        // Use the client rect for consistent screen<->hook pixel mapping (overlay/ROI alignment).
        if (TryGetClientScreenRect(hwnd, out var clientRect))
        {
            return clientRect;
        }

        if (TryGetExtendedFrameBounds(hwnd, out var rect))
        {
            return rect;
        }

        if (GetWindowRect(hwnd, out var fallback))
        {
            return fallback.ToRect();
        }

        return null;
    }

    private static bool TryGetClientScreenRect(IntPtr hwnd, out Rect rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetClientRect(hwnd, out var client))
        {
            return false;
        }

        var w = client.Right - client.Left;
        var h = client.Bottom - client.Top;
        if (w <= 0 || h <= 0)
        {
            return false;
        }

        var tl = new NativePoint(0, 0);
        var br = new NativePoint(w, h);
        if (!ClientToScreen(hwnd, ref tl) || !ClientToScreen(hwnd, ref br))
        {
            return false;
        }

        var outW = br.X - tl.X;
        var outH = br.Y - tl.Y;
        if (outW <= 0 || outH <= 0)
        {
            return false;
        }

        rect = new Rect(tl.X, tl.Y, outW, outH);
        return true;
    }

    private static bool TryGetExtendedFrameBounds(IntPtr hwnd, out Rect rect)
    {
        rect = default;
        var size = Marshal.SizeOf<NativeRect>();
        if (DwmGetWindowAttribute(hwnd, DwmWindowAttribute.ExtendedFrameBounds, out var nativeRect, size) != 0)
        {
            return false;
        }

        rect = nativeRect.ToRect();
        return rect.Width > 0 && rect.Height > 0;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, DwmWindowAttribute dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private enum DwmWindowAttribute
    {
        ExtendedFrameBounds = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rect ToRect()
        {
            return new Rect(Left, Top, Right - Left, Bottom - Top);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}


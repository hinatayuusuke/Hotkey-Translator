#pragma once

#include <cstdint>
#include <string>

#include <windows.h>

namespace ht::hook::ipc
{
    constexpr std::uint32_t kFrameHeaderMagic = 0x48465452; // "HFTR"
    constexpr std::uint32_t kFrameHeaderVersion = 1;
    constexpr std::uint32_t kFramePixelFormatBgra8 = 1;

    constexpr std::uint32_t kConfigHeaderMagic = 0x48435446; // "HCTF"
    constexpr std::uint32_t kConfigHeaderVersion = 1;
    constexpr std::uint32_t kConfigFlagEnablePerfDiagLog = 1u << 0;
    constexpr std::uint32_t kConfigFlagEnableDiagFileSink = 1u << 1;

    // Overlay v2 (ImGui translation overlay): text blocks + UTF-8 blob.
    constexpr std::uint32_t kOverlayV2Magic = 0x32564F48; // "HOV2"
    constexpr std::uint32_t kOverlayV2Version = 2;

    constexpr std::uint32_t kStatusMagic = 0x48535453; // "HSTS"
    constexpr std::uint32_t kStatusVersion = 1;

    enum class GraphicsApi : std::uint32_t
    {
        Unknown = 0,
        Dx11 = 1,
        Dx12 = 2,
        OpenGl = 3,
        Vulkan = 4
    };

    enum class CommandType : std::uint32_t
    {
        Unknown = 0,
        Attach = 1,
        Detach = 2,
        OverlayUpdate = 3,
        FrameReady = 4
    };

#pragma pack(push, 1)
    struct FrameHeader
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint64_t frameId;
        std::uint32_t width;
        std::uint32_t height;
        std::uint32_t stride;
        std::uint32_t payloadBytes;
        std::uint32_t pixelFormat;
        std::uint32_t api;
        std::uint32_t producerPid;
        std::uint32_t reserved0;
        std::uint64_t timestampQpc;
    };

    struct HookConfigHeader
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint32_t api;
        std::uint32_t targetPid;
        std::uint32_t captureFpsLimit;
        std::uint32_t overlayEnabled;
        std::uint32_t reserved0;
        std::uint32_t reserved1;
        std::uint64_t updatedQpc;
    };

    struct OverlayV2Header
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint32_t api;
        std::uint32_t targetPid;
        // WHY: Prefer a monotonic sequence number over cross-process QPC comparisons.
        // Writer updates payload first, then updates this field last to publish the new frame.
        std::uint64_t updatedSeq;
        std::uint32_t canvasW;
        std::uint32_t canvasH;
        std::uint32_t textBlockCount;
        std::uint32_t textBytes;
        std::uint32_t flags;
        std::uint32_t reserved0;
    };

    struct OverlayTextBlockV2
    {
        float x;
        float y;
        float w;
        float h;
        float paddingPx;
        float roundingPx;
        float fontPx;
        std::uint32_t fgArgb;
        std::uint32_t bgArgb;
        std::uint32_t wrap;
        std::uint32_t textOffset;
        std::uint32_t textLen;
        std::int32_t zOrder;
    };

    struct HookStatusHeader
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint32_t api;
        std::uint32_t targetPid;
        std::uint64_t presentCount;
        std::uint64_t lastPresentQpc;
        std::uint32_t lastPresentKind;
        std::uint32_t backBufferDxgiFormat;
        std::uint32_t backBufferWidth;
        std::uint32_t backBufferHeight;
        std::uint32_t stagingDxgiFormat;
        std::uint32_t reserved0;
        std::uint64_t lastFrameIdWritten;
        std::uint64_t lastFrameWriteQpc;
        std::uint64_t lastCmdQpc;
        std::uint32_t lastCmdCount;
        std::uint32_t reserved1;
        std::uint64_t updatedQpc;
    };
#pragma pack(pop)

    inline std::wstring BuildFrameMappingName(DWORD pid, GraphicsApi api)
    {
        // WHY: Backends use one naming convention so future OpenGL/Vulkan agents can reuse the same reader path.
        std::wstring name = L"Local\\HT_HOOK_FRAME_";
        name += std::to_wstring(static_cast<std::uint32_t>(api));
        name += L"_";
        name += std::to_wstring(pid);
        return name;
    }

    inline std::wstring BuildConfigMappingName(DWORD pid, GraphicsApi api)
    {
        std::wstring name = L"Local\\HT_HOOK_CFG_";
        name += std::to_wstring(static_cast<std::uint32_t>(api));
        name += L"_";
        name += std::to_wstring(pid);
        return name;
    }

    inline std::wstring BuildOverlayV2MappingName(DWORD pid, GraphicsApi api)
    {
        // WHY: Keep naming consistent across graphics APIs so backends can share the same reader/writer logic.
        std::wstring name = L"Local\\HT_HOOK_OVL_";
        name += std::to_wstring(static_cast<std::uint32_t>(api));
        name += L"_";
        name += std::to_wstring(pid);
        return name;
    }

    inline std::wstring BuildStatusMappingName(DWORD pid, GraphicsApi api)
    {
        std::wstring name = L"Local\\HT_HOOK_STAT_";
        name += std::to_wstring(static_cast<std::uint32_t>(api));
        name += L"_";
        name += std::to_wstring(pid);
        return name;
    }
}

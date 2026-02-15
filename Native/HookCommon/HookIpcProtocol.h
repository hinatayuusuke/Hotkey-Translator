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

    constexpr std::uint32_t kOverlayCmdMagic = 0x48434D44; // "HCMD"
    constexpr std::uint32_t kOverlayCmdVersion = 1;

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

    struct OverlayCommandHeader
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint32_t api;
        std::uint32_t targetPid;
        std::uint32_t commandCount;
        std::uint32_t payloadBytes;
        std::uint32_t reserved0;
        std::uint32_t reserved1;
        std::uint64_t updatedQpc;
    };

    struct OverlayRectCommand
    {
        float x;
        float y;
        float w;
        float h;
        std::uint32_t argb;
        std::uint32_t thickness;
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

    inline std::wstring BuildOverlayCommandMappingName(DWORD pid, GraphicsApi api)
    {
        std::wstring name = L"Local\\HT_HOOK_CMD_";
        name += std::to_wstring(static_cast<std::uint32_t>(api));
        name += L"_";
        name += std::to_wstring(pid);
        return name;
    }
}

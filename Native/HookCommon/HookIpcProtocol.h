#pragma once

#include <cstdint>

namespace ht::hook::ipc
{
    constexpr std::uint32_t kFrameHeaderMagic = 0x48465452; // "HFTR"
    constexpr std::uint32_t kFrameHeaderVersion = 1;

    enum class CommandType : std::uint32_t
    {
        Unknown = 0,
        Attach = 1,
        Detach = 2,
        OverlayUpdate = 3
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
        std::uint64_t timestampQpc;
    };
#pragma pack(pop)
}

#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

#include <windows.h>

#include "HookIpcProtocol.h"

namespace ht::hook::ipc
{
    class SharedFrameWriter
    {
    public:
        SharedFrameWriter() = default;
        ~SharedFrameWriter();

        SharedFrameWriter(const SharedFrameWriter&) = delete;
        SharedFrameWriter& operator=(const SharedFrameWriter&) = delete;

        bool EnsureCapacity(DWORD pid, GraphicsApi api, std::size_t payloadBytes);
        bool WriteFrame(
            DWORD pid,
            GraphicsApi api,
            std::uint64_t frameId,
            std::uint32_t width,
            std::uint32_t height,
            std::uint32_t stride,
            std::uint64_t timestampQpc,
            const std::uint8_t* payload,
            std::size_t payloadBytes);

        void Reset();
        std::wstring MappingName() const { return mappingName_; }

    private:
        bool RecreateMapping(DWORD pid, GraphicsApi api, std::size_t payloadBytes);

        HANDLE mappingHandle_ = nullptr;
        std::uint8_t* mappedView_ = nullptr;
        std::wstring mappingName_;
        std::size_t mappedCapacityBytes_ = 0;
    };
}

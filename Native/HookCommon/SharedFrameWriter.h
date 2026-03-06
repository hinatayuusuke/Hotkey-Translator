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
        enum class LastErrorKind : std::uint32_t
        {
            None = 0,
            InvalidArguments = 1,
            EnsureCapacityFailed = 2,
            MappingSizeInvalid = 3,
            CreateFileMappingFailed = 4,
            MapViewFailed = 5,
        };

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
        LastErrorKind LastError() const { return lastErrorKind_; }
        DWORD LastWin32Error() const { return lastWin32Error_; }
        std::size_t LastRequestedPayloadBytes() const { return lastRequestedPayloadBytes_; }
        std::size_t LastTotalBytes() const { return lastTotalBytes_; }

    private:
        bool RecreateMapping(DWORD pid, GraphicsApi api, std::size_t payloadBytes);
        void SetLastError(LastErrorKind kind, DWORD win32Error, std::size_t requestedPayloadBytes, std::size_t totalBytes);

        HANDLE mappingHandle_ = nullptr;
        std::uint8_t* mappedView_ = nullptr;
        std::wstring mappingName_;
        std::size_t mappedCapacityBytes_ = 0;
        LastErrorKind lastErrorKind_ = LastErrorKind::None;
        DWORD lastWin32Error_ = 0;
        std::size_t lastRequestedPayloadBytes_ = 0;
        std::size_t lastTotalBytes_ = 0;
    };
}

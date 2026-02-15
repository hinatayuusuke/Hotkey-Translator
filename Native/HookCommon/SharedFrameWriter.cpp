#include "SharedFrameWriter.h"

#include <algorithm>
#include <cstring>

namespace ht::hook::ipc
{
    namespace
    {
        constexpr std::size_t kMaxPayloadBytes = 7680ull * 4320ull * 4ull;
    }

    SharedFrameWriter::~SharedFrameWriter()
    {
        Reset();
    }

    bool SharedFrameWriter::EnsureCapacity(DWORD pid, GraphicsApi api, std::size_t payloadBytes)
    {
        if (mappedView_ != nullptr && mappedCapacityBytes_ >= payloadBytes)
        {
            return true;
        }

        return RecreateMapping(pid, api, payloadBytes);
    }

    bool SharedFrameWriter::WriteFrame(
        DWORD pid,
        GraphicsApi api,
        std::uint64_t frameId,
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t stride,
        std::uint64_t timestampQpc,
        const std::uint8_t* payload,
        std::size_t payloadBytes)
    {
        if (payload == nullptr || payloadBytes == 0)
        {
            return false;
        }

        if (!EnsureCapacity(pid, api, payloadBytes))
        {
            return false;
        }

        auto* header = reinterpret_cast<FrameHeader*>(mappedView_);
        auto* payloadDst = mappedView_ + sizeof(FrameHeader);

        std::memcpy(payloadDst, payload, payloadBytes);
        MemoryBarrier();

        header->magic = kFrameHeaderMagic;
        header->version = kFrameHeaderVersion;
        header->frameId = frameId;
        header->width = width;
        header->height = height;
        header->stride = stride;
        header->payloadBytes = static_cast<std::uint32_t>(payloadBytes);
        header->pixelFormat = kFramePixelFormatBgra8;
        header->api = static_cast<std::uint32_t>(api);
        header->producerPid = pid;
        header->reserved0 = 0;
        header->timestampQpc = timestampQpc;
        MemoryBarrier();
        return true;
    }

    void SharedFrameWriter::Reset()
    {
        if (mappedView_ != nullptr)
        {
            UnmapViewOfFile(mappedView_);
            mappedView_ = nullptr;
        }

        if (mappingHandle_ != nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
        }

        mappedCapacityBytes_ = 0;
        mappingName_.clear();
    }

    bool SharedFrameWriter::RecreateMapping(DWORD pid, GraphicsApi api, std::size_t payloadBytes)
    {
        Reset();

        if (payloadBytes == 0 || payloadBytes > kMaxPayloadBytes)
        {
            return false;
        }

        const auto totalBytes = sizeof(FrameHeader) + payloadBytes;
        mappingName_ = BuildFrameMappingName(pid, api);

        mappingHandle_ = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            nullptr,
            PAGE_READWRITE,
            static_cast<DWORD>((totalBytes >> 32) & 0xFFFFFFFF),
            static_cast<DWORD>(totalBytes & 0xFFFFFFFF),
            mappingName_.c_str());

        if (mappingHandle_ == nullptr)
        {
            return false;
        }

        mappedView_ = static_cast<std::uint8_t*>(MapViewOfFile(mappingHandle_, FILE_MAP_ALL_ACCESS, 0, 0, totalBytes));
        if (mappedView_ == nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            return false;
        }

        mappedCapacityBytes_ = payloadBytes;
        std::memset(mappedView_, 0, totalBytes);
        return true;
    }
}

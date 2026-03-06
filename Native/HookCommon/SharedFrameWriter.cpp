#include "SharedFrameWriter.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <limits>

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

    void SharedFrameWriter::SetLastError(
        LastErrorKind kind,
        DWORD win32Error,
        std::size_t requestedPayloadBytes,
        std::size_t totalBytes)
    {
        lastErrorKind_ = kind;
        lastWin32Error_ = win32Error;
        lastRequestedPayloadBytes_ = requestedPayloadBytes;
        lastTotalBytes_ = totalBytes;
    }

    bool SharedFrameWriter::EnsureCapacity(DWORD pid, GraphicsApi api, std::size_t payloadBytes)
    {
        if (mappedView_ != nullptr && mappedCapacityBytes_ >= payloadBytes)
        {
            SetLastError(LastErrorKind::None, 0, payloadBytes, sizeof(FrameHeader) + payloadBytes);
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
            SetLastError(LastErrorKind::InvalidArguments, ERROR_INVALID_PARAMETER, payloadBytes, 0);
            return false;
        }

        if (!EnsureCapacity(pid, api, payloadBytes))
        {
            if (lastErrorKind_ == LastErrorKind::None)
            {
                SetLastError(LastErrorKind::EnsureCapacityFailed, GetLastError(), payloadBytes, sizeof(FrameHeader) + payloadBytes);
            }
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
        SetLastError(LastErrorKind::None, 0, payloadBytes, sizeof(FrameHeader) + payloadBytes);
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
        SetLastError(LastErrorKind::None, 0, 0, 0);
    }

    bool SharedFrameWriter::RecreateMapping(DWORD pid, GraphicsApi api, std::size_t payloadBytes)
    {
        Reset();

        if (payloadBytes == 0 || payloadBytes > kMaxPayloadBytes)
        {
            SetLastError(LastErrorKind::MappingSizeInvalid, ERROR_INVALID_PARAMETER, payloadBytes, sizeof(FrameHeader) + payloadBytes);
            return false;
        }

        const std::uint64_t totalBytes64 = static_cast<std::uint64_t>(sizeof(FrameHeader)) + static_cast<std::uint64_t>(payloadBytes);
        if (totalBytes64 > static_cast<std::uint64_t>(std::numeric_limits<SIZE_T>::max()))
        {
            SetLastError(LastErrorKind::MappingSizeInvalid, ERROR_INVALID_PARAMETER, payloadBytes, 0);
            return false;
        }

        const SIZE_T totalBytes = static_cast<SIZE_T>(totalBytes64);
        const DWORD mappingSizeHigh = static_cast<DWORD>((totalBytes64 >> 32u) & 0xFFFFFFFFull);
        const DWORD mappingSizeLow = static_cast<DWORD>(totalBytes64 & 0xFFFFFFFFull);
        mappingName_ = BuildFrameMappingName(pid, api);

        mappingHandle_ = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            nullptr,
            PAGE_READWRITE,
            mappingSizeHigh,
            mappingSizeLow,
            mappingName_.c_str());

        if (mappingHandle_ == nullptr)
        {
            SetLastError(LastErrorKind::CreateFileMappingFailed, GetLastError(), payloadBytes, static_cast<std::size_t>(totalBytes));
            return false;
        }

        mappedView_ = static_cast<std::uint8_t*>(MapViewOfFile(mappingHandle_, FILE_MAP_ALL_ACCESS, 0, 0, totalBytes));
        if (mappedView_ == nullptr)
        {
            const DWORD gle = GetLastError();
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            SetLastError(LastErrorKind::MapViewFailed, gle, payloadBytes, static_cast<std::size_t>(totalBytes));
            return false;
        }

        mappedCapacityBytes_ = payloadBytes;
        std::memset(mappedView_, 0, totalBytes);
        SetLastError(LastErrorKind::None, 0, payloadBytes, static_cast<std::size_t>(totalBytes));
        return true;
    }
}

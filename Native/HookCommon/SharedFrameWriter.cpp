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
        constexpr std::size_t kFramePipePayloadCapacityBytes = kMaxPayloadBytes;
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
            SetLastError(LastErrorKind::None, 0, payloadBytes, mappedTotalBytes_);
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
                SetLastError(LastErrorKind::EnsureCapacityFailed, GetLastError(), payloadBytes, mappedTotalBytes_);
            }
            return false;
        }

        auto* pipeHeader = PipeHeader();
        const std::uint64_t publishSeq = nextPublishedSeq_++;
        if (nextPublishedSeq_ == 0)
        {
            nextPublishedSeq_ = 1;
        }

        std::uint32_t targetSlotIndex = 0;
        if (pipeHeader->publishedSeq != 0)
        {
            targetSlotIndex = (pipeHeader->publishedIndex + 1u) % kFramePipeSlotCount;
        }

        auto* slotBase = SlotBase(targetSlotIndex);
        auto* slotHeader = reinterpret_cast<FrameSlotHeaderV2*>(slotBase);
        auto* payloadDst = slotBase + sizeof(FrameSlotHeaderV2);

        std::memcpy(payloadDst, payload, payloadBytes);
        MemoryBarrier();

        slotHeader->frameId = frameId;
        slotHeader->width = width;
        slotHeader->height = height;
        slotHeader->stride = stride;
        slotHeader->payloadBytes = static_cast<std::uint32_t>(payloadBytes);
        slotHeader->pixelFormat = kFramePixelFormatBgra8;
        slotHeader->timestampQpc = timestampQpc;
        slotHeader->slotSeq = publishSeq;
        MemoryBarrier();

        pipeHeader->publishedIndex = targetSlotIndex;
        MemoryBarrier();
        pipeHeader->publishedSeq = publishSeq;
        MemoryBarrier();

        SetLastError(LastErrorKind::None, 0, payloadBytes, mappedTotalBytes_);
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
        mappedTotalBytes_ = 0;
        mappingName_.clear();
        nextPublishedSeq_ = 1;
        SetLastError(LastErrorKind::None, 0, 0, 0);
    }

    bool SharedFrameWriter::RecreateMapping(DWORD pid, GraphicsApi api, std::size_t payloadBytes)
    {
        Reset();

        if (payloadBytes == 0 || payloadBytes > kMaxPayloadBytes)
        {
            SetLastError(LastErrorKind::MappingSizeInvalid, ERROR_INVALID_PARAMETER, payloadBytes, 0);
            return false;
        }

        // WHY: C# reader caches the mapping handle, so the named mapping cannot resize after the first successful open.
        // Keep a fixed payload capacity and publish into alternating slots instead of recreating the object on resize.
        const std::uint64_t totalBytes64 = static_cast<std::uint64_t>(FramePipeTotalBytes(kFramePipePayloadCapacityBytes));
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

        mappedCapacityBytes_ = kFramePipePayloadCapacityBytes;
        mappedTotalBytes_ = static_cast<std::size_t>(totalBytes);
        std::memset(mappedView_, 0, totalBytes);

        auto* pipeHeader = PipeHeader();
        pipeHeader->magic = kFrameHeaderMagic;
        pipeHeader->version = kFramePipeVersion;
        pipeHeader->api = static_cast<std::uint32_t>(api);
        pipeHeader->producerPid = pid;
        pipeHeader->slotCount = kFramePipeSlotCount;
        pipeHeader->payloadCapacity = static_cast<std::uint32_t>(mappedCapacityBytes_);
        pipeHeader->publishedIndex = 0;
        pipeHeader->reserved0 = 0;
        pipeHeader->publishedSeq = 0;
        pipeHeader->reserved1 = 0;

        SetLastError(LastErrorKind::None, 0, payloadBytes, mappedTotalBytes_);
        return true;
    }

    std::uint8_t* SharedFrameWriter::SlotBase(std::uint32_t slotIndex) const
    {
        if (mappedView_ == nullptr || slotIndex >= kFramePipeSlotCount)
        {
            return nullptr;
        }

        const std::size_t slotOffset = sizeof(FramePipeHeaderV2) + (FrameSlotBytes(mappedCapacityBytes_) * slotIndex);
        return mappedView_ + slotOffset;
    }

    FramePipeHeaderV2* SharedFrameWriter::PipeHeader() const
    {
        return reinterpret_cast<FramePipeHeaderV2*>(mappedView_);
    }
}

#include "SharedOverlayV2.h"

#include <algorithm>
#include <cstring>

// NOTE: windows.h defines min/max macros unless NOMINMAX is set. Undef here to keep STL calls usable.
#ifdef min
#undef min
#endif
#ifdef max
#undef max
#endif

namespace ht::hook::ipc
{
    namespace
    {
        constexpr std::size_t kOverlayV2MappingBytes = 256 * 1024;
        constexpr std::size_t kMaxTextBlocks = 64;

        std::size_t PayloadCapacity()
        {
            const std::size_t headerBytes = sizeof(OverlayV2Header);
            return (kOverlayV2MappingBytes > headerBytes) ? (kOverlayV2MappingBytes - headerBytes) : 0;
        }

        bool ValidateHeader(const OverlayV2Header& h)
        {
            if (h.magic != kOverlayV2Magic || h.version != kOverlayV2Version)
            {
                return false;
            }

            const std::size_t blocks = std::min<std::size_t>(h.textBlockCount, kMaxTextBlocks);
            const std::size_t blockBytes = blocks * sizeof(OverlayTextBlockV2);
            const std::size_t blobBytes = static_cast<std::size_t>(h.textBytes);
            const std::size_t totalPayload = blockBytes + blobBytes;
            return totalPayload <= PayloadCapacity();
        }
    }

    SharedOverlayV2Writer::~SharedOverlayV2Writer()
    {
        Reset();
    }

    SharedOverlayV2Writer::SharedOverlayV2Writer(SharedOverlayV2Writer&& other) noexcept
    {
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);
        nextSeq_ = other.nextSeq_;

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
        other.nextSeq_ = 0;
    }

    SharedOverlayV2Writer& SharedOverlayV2Writer::operator=(SharedOverlayV2Writer&& other) noexcept
    {
        if (this == &other)
        {
            return *this;
        }

        Reset();
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);
        nextSeq_ = other.nextSeq_;

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
        other.nextSeq_ = 0;
        return *this;
    }

    bool SharedOverlayV2Writer::Ensure(DWORD pid, GraphicsApi api)
    {
        if (mapped_ != nullptr)
        {
            return true;
        }

        mappingName_ = BuildOverlayV2MappingName(pid, api);
        mappingHandle_ = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            nullptr,
            PAGE_READWRITE,
            0,
            static_cast<DWORD>(kOverlayV2MappingBytes),
            mappingName_.c_str());
        if (mappingHandle_ == nullptr)
        {
            return false;
        }

        mapped_ = static_cast<std::uint8_t*>(
            MapViewOfFile(mappingHandle_, FILE_MAP_ALL_ACCESS, 0, 0, kOverlayV2MappingBytes));
        if (mapped_ == nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            return false;
        }

        std::memset(mapped_, 0, kOverlayV2MappingBytes);
        nextSeq_ = 0;
        return true;
    }

    bool SharedOverlayV2Writer::Write(
        DWORD pid,
        GraphicsApi api,
        std::uint32_t canvasW,
        std::uint32_t canvasH,
        const OverlayTextBlockV2* blocks,
        std::size_t blockCount,
        const std::uint8_t* textBlob,
        std::size_t textBytes,
        std::uint32_t flags)
    {
        if (!Ensure(pid, api) || mapped_ == nullptr)
        {
            return false;
        }

        const std::size_t clampedBlocks = std::min<std::size_t>(blockCount, kMaxTextBlocks);
        const std::size_t blocksBytes = clampedBlocks * sizeof(OverlayTextBlockV2);
        const std::size_t blobBytes = textBytes;
        if (blocksBytes + blobBytes > PayloadCapacity())
        {
            // WHY: Writer must ensure offsets/lengths stay valid. Don't silently truncate here.
            return false;
        }

        const std::size_t headerBytes = sizeof(OverlayV2Header);
        auto* payload = mapped_ + headerBytes;

        if (clampedBlocks > 0 && blocks != nullptr)
        {
            std::memcpy(payload, blocks, blocksBytes);
        }
        if (blobBytes > 0 && textBlob != nullptr)
        {
            std::memcpy(payload + blocksBytes, textBlob, blobBytes);
        }

        MemoryBarrier();

        OverlayV2Header hdr{};
        hdr.magic = kOverlayV2Magic;
        hdr.version = kOverlayV2Version;
        hdr.api = static_cast<std::uint32_t>(api);
        hdr.targetPid = pid;
        hdr.updatedSeq = ++nextSeq_;
        hdr.canvasW = canvasW;
        hdr.canvasH = canvasH;
        hdr.textBlockCount = static_cast<std::uint32_t>(clampedBlocks);
        hdr.textBytes = static_cast<std::uint32_t>(blobBytes);
        hdr.flags = flags;
        *reinterpret_cast<OverlayV2Header*>(mapped_) = hdr;

        MemoryBarrier();
        return true;
    }

    void SharedOverlayV2Writer::Reset()
    {
        if (mapped_ != nullptr)
        {
            UnmapViewOfFile(mapped_);
            mapped_ = nullptr;
        }
        if (mappingHandle_ != nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
        }

        mappingName_.clear();
        nextSeq_ = 0;
    }

    SharedOverlayV2Reader::~SharedOverlayV2Reader()
    {
        Reset();
    }

    SharedOverlayV2Reader::SharedOverlayV2Reader(SharedOverlayV2Reader&& other) noexcept
    {
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
    }

    SharedOverlayV2Reader& SharedOverlayV2Reader::operator=(SharedOverlayV2Reader&& other) noexcept
    {
        if (this == &other)
        {
            return *this;
        }

        Reset();
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
        return *this;
    }

    bool SharedOverlayV2Reader::Ensure(DWORD pid, GraphicsApi api)
    {
        if (mapped_ != nullptr)
        {
            return true;
        }

        mappingName_ = BuildOverlayV2MappingName(pid, api);
        mappingHandle_ = OpenFileMappingW(FILE_MAP_READ, FALSE, mappingName_.c_str());
        if (mappingHandle_ == nullptr)
        {
            return false;
        }

        mapped_ = static_cast<const std::uint8_t*>(
            MapViewOfFile(mappingHandle_, FILE_MAP_READ, 0, 0, kOverlayV2MappingBytes));
        if (mapped_ == nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            return false;
        }

        return true;
    }

    bool SharedOverlayV2Reader::TryRead(
        OverlayV2Header& outHeader,
        std::vector<OverlayTextBlockV2>& outBlocks,
        std::vector<std::uint8_t>& outTextBlob) const
    {
        if (mapped_ == nullptr)
        {
            return false;
        }

        MemoryBarrier();
        const auto h0 = *reinterpret_cast<const OverlayV2Header*>(mapped_);
        MemoryBarrier();

        if (!ValidateHeader(h0))
        {
            return false;
        }

        const std::size_t headerBytes = sizeof(OverlayV2Header);
        const std::size_t blocks = std::min<std::size_t>(h0.textBlockCount, kMaxTextBlocks);
        const std::size_t blocksBytes = blocks * sizeof(OverlayTextBlockV2);
        const std::size_t blobBytes = static_cast<std::size_t>(h0.textBytes);

        const auto* payload = mapped_ + headerBytes;

        outHeader = h0;
        outBlocks.resize(blocks);
        outTextBlob.resize(blobBytes);

        if (blocksBytes > 0)
        {
            std::memcpy(outBlocks.data(), payload, blocksBytes);
        }
        if (blobBytes > 0)
        {
            std::memcpy(outTextBlob.data(), payload + blocksBytes, blobBytes);
        }

        MemoryBarrier();
        const auto h1 = *reinterpret_cast<const OverlayV2Header*>(mapped_);
        MemoryBarrier();

        // WHY: Payload is written before header; if header changed during copy, drop this read to avoid tearing.
        if (h1.updatedSeq != h0.updatedSeq)
        {
            return false;
        }

        return true;
    }

    void SharedOverlayV2Reader::Reset()
    {
        if (mapped_ != nullptr)
        {
            UnmapViewOfFile(mapped_);
            mapped_ = nullptr;
        }
        if (mappingHandle_ != nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
        }

        mappingName_.clear();
    }
}


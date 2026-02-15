#include "SharedOverlayCommands.h"

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
        constexpr std::size_t kOverlayMappingBytes = 64 * 1024;

        std::uint64_t NowQpc()
        {
            LARGE_INTEGER qpc{};
            QueryPerformanceCounter(&qpc);
            return static_cast<std::uint64_t>(qpc.QuadPart);
        }

        std::size_t MaxCommands()
        {
            const std::size_t headerBytes = sizeof(OverlayCommandHeader);
            const std::size_t cmdBytes = sizeof(OverlayRectCommand);
            if (kOverlayMappingBytes <= headerBytes)
            {
                return 0;
            }

            return (kOverlayMappingBytes - headerBytes) / cmdBytes;
        }
    }

    SharedOverlayCommandsWriter::~SharedOverlayCommandsWriter()
    {
        Reset();
    }

    SharedOverlayCommandsWriter::SharedOverlayCommandsWriter(SharedOverlayCommandsWriter&& other) noexcept
    {
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
    }

    SharedOverlayCommandsWriter& SharedOverlayCommandsWriter::operator=(SharedOverlayCommandsWriter&& other) noexcept
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

    bool SharedOverlayCommandsWriter::Ensure(DWORD pid, GraphicsApi api)
    {
        if (mapped_ != nullptr)
        {
            return true;
        }

        mappingName_ = BuildOverlayCommandMappingName(pid, api);
        mappingHandle_ = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            nullptr,
            PAGE_READWRITE,
            0,
            static_cast<DWORD>(kOverlayMappingBytes),
            mappingName_.c_str());

        if (mappingHandle_ == nullptr)
        {
            return false;
        }

        mapped_ = static_cast<std::uint8_t*>(
            MapViewOfFile(mappingHandle_, FILE_MAP_ALL_ACCESS, 0, 0, kOverlayMappingBytes));
        if (mapped_ == nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            return false;
        }

        std::memset(mapped_, 0, kOverlayMappingBytes);
        return true;
    }

    bool SharedOverlayCommandsWriter::Write(DWORD pid, GraphicsApi api, const OverlayRectCommand* commands, std::size_t commandCount)
    {
        if (!Ensure(pid, api) || mapped_ == nullptr)
        {
            return false;
        }

        const auto max = MaxCommands();
        const auto count = std::min(commandCount, max);
        const std::uint32_t payloadBytes = static_cast<std::uint32_t>(count * sizeof(OverlayRectCommand));

        auto* header = reinterpret_cast<OverlayCommandHeader*>(mapped_);
        auto* payload = mapped_ + sizeof(OverlayCommandHeader);

        if (count > 0 && commands != nullptr)
        {
            std::memcpy(payload, commands, payloadBytes);
        }

        MemoryBarrier();

        OverlayCommandHeader hdr{};
        hdr.magic = kOverlayCmdMagic;
        hdr.version = kOverlayCmdVersion;
        hdr.api = static_cast<std::uint32_t>(api);
        hdr.targetPid = pid;
        hdr.commandCount = static_cast<std::uint32_t>(count);
        hdr.payloadBytes = payloadBytes;
        hdr.updatedQpc = NowQpc();
        *header = hdr;
        MemoryBarrier();
        return true;
    }

    void SharedOverlayCommandsWriter::Reset()
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

    SharedOverlayCommandsReader::~SharedOverlayCommandsReader()
    {
        Reset();
    }

    SharedOverlayCommandsReader::SharedOverlayCommandsReader(SharedOverlayCommandsReader&& other) noexcept
    {
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
    }

    SharedOverlayCommandsReader& SharedOverlayCommandsReader::operator=(SharedOverlayCommandsReader&& other) noexcept
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

    bool SharedOverlayCommandsReader::Ensure(DWORD pid, GraphicsApi api)
    {
        if (mapped_ != nullptr)
        {
            return true;
        }

        mappingName_ = BuildOverlayCommandMappingName(pid, api);
        mappingHandle_ = OpenFileMappingW(FILE_MAP_READ, FALSE, mappingName_.c_str());
        if (mappingHandle_ == nullptr)
        {
            return false;
        }

        mapped_ = static_cast<const std::uint8_t*>(
            MapViewOfFile(mappingHandle_, FILE_MAP_READ, 0, 0, kOverlayMappingBytes));
        if (mapped_ == nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            return false;
        }

        return true;
    }

    bool SharedOverlayCommandsReader::TryRead(OverlayCommandHeader& outHeader, std::vector<OverlayRectCommand>& outCommands) const
    {
        if (mapped_ == nullptr)
        {
            return false;
        }

        MemoryBarrier();
        auto header = *reinterpret_cast<const OverlayCommandHeader*>(mapped_);
        MemoryBarrier();

        if (header.magic != kOverlayCmdMagic || header.version != kOverlayCmdVersion)
        {
            return false;
        }

        const auto max = MaxCommands();
        const auto count = std::min<std::size_t>(header.commandCount, max);
        const auto expectedBytes = count * sizeof(OverlayRectCommand);
        if (header.payloadBytes < expectedBytes)
        {
            return false;
        }

        outHeader = header;
        outCommands.resize(count);
        if (count > 0)
        {
            const auto* payload = mapped_ + sizeof(OverlayCommandHeader);
            std::memcpy(outCommands.data(), payload, expectedBytes);
        }

        return true;
    }

    void SharedOverlayCommandsReader::Reset()
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

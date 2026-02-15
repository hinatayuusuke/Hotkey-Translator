#include "SharedHookStatus.h"

#include <cstring>

namespace ht::hook::ipc
{
    namespace
    {
        constexpr std::size_t kStatusMappingBytes = 4096;

        std::uint64_t NowQpc()
        {
            LARGE_INTEGER qpc{};
            QueryPerformanceCounter(&qpc);
            return static_cast<std::uint64_t>(qpc.QuadPart);
        }
    }

    SharedHookStatusWriter::~SharedHookStatusWriter()
    {
        Reset();
    }

    SharedHookStatusWriter::SharedHookStatusWriter(SharedHookStatusWriter&& other) noexcept
    {
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
    }

    SharedHookStatusWriter& SharedHookStatusWriter::operator=(SharedHookStatusWriter&& other) noexcept
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

    bool SharedHookStatusWriter::Ensure(DWORD pid, GraphicsApi api)
    {
        if (mapped_ != nullptr)
        {
            return true;
        }

        mappingName_ = BuildStatusMappingName(pid, api);
        mappingHandle_ = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            nullptr,
            PAGE_READWRITE,
            0,
            static_cast<DWORD>(kStatusMappingBytes),
            mappingName_.c_str());

        if (mappingHandle_ == nullptr)
        {
            return false;
        }

        mapped_ = static_cast<std::uint8_t*>(
            MapViewOfFile(mappingHandle_, FILE_MAP_ALL_ACCESS, 0, 0, kStatusMappingBytes));
        if (mapped_ == nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            return false;
        }

        std::memset(mapped_, 0, kStatusMappingBytes);
        return true;
    }

    bool SharedHookStatusWriter::Write(const HookStatusHeader& status)
    {
        if (mapped_ == nullptr)
        {
            return false;
        }

        auto hdr = status;
        hdr.magic = kStatusMagic;
        hdr.version = kStatusVersion;
        hdr.updatedQpc = NowQpc();

        MemoryBarrier();
        std::memcpy(mapped_, &hdr, sizeof(HookStatusHeader));
        MemoryBarrier();
        return true;
    }

    void SharedHookStatusWriter::Reset()
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

    SharedHookStatusReader::~SharedHookStatusReader()
    {
        Reset();
    }

    SharedHookStatusReader::SharedHookStatusReader(SharedHookStatusReader&& other) noexcept
    {
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
    }

    SharedHookStatusReader& SharedHookStatusReader::operator=(SharedHookStatusReader&& other) noexcept
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

    bool SharedHookStatusReader::Ensure(DWORD pid, GraphicsApi api)
    {
        if (mapped_ != nullptr)
        {
            return true;
        }

        mappingName_ = BuildStatusMappingName(pid, api);
        mappingHandle_ = OpenFileMappingW(FILE_MAP_READ, FALSE, mappingName_.c_str());
        if (mappingHandle_ == nullptr)
        {
            return false;
        }

        mapped_ = static_cast<const std::uint8_t*>(
            MapViewOfFile(mappingHandle_, FILE_MAP_READ, 0, 0, kStatusMappingBytes));
        if (mapped_ == nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            return false;
        }

        return true;
    }

    bool SharedHookStatusReader::TryRead(HookStatusHeader& outStatus) const
    {
        if (mapped_ == nullptr)
        {
            return false;
        }

        MemoryBarrier();
        auto status = *reinterpret_cast<const HookStatusHeader*>(mapped_);
        MemoryBarrier();

        if (status.magic != kStatusMagic || status.version != kStatusVersion)
        {
            return false;
        }

        outStatus = status;
        return true;
    }

    void SharedHookStatusReader::Reset()
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


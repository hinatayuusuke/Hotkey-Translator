#include "SharedHookConfig.h"

#include <algorithm>
#include <cstring>

namespace ht::hook::ipc
{
    namespace
    {
        std::uint64_t NowQpc()
        {
            LARGE_INTEGER qpc{};
            QueryPerformanceCounter(&qpc);
            return static_cast<std::uint64_t>(qpc.QuadPart);
        }

        std::uint32_t ClampFps(std::uint32_t fps)
        {
            // PERF: Prevent divide-by-zero and unreasonable intervals.
            return std::clamp(fps, 1u, 240u);
        }
    }

    SharedHookConfigWriter::~SharedHookConfigWriter()
    {
        Reset();
    }

    SharedHookConfigWriter::SharedHookConfigWriter(SharedHookConfigWriter&& other) noexcept
    {
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
    }

    SharedHookConfigWriter& SharedHookConfigWriter::operator=(SharedHookConfigWriter&& other) noexcept
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

    bool SharedHookConfigWriter::Ensure(DWORD pid, GraphicsApi api)
    {
        if (mapped_ != nullptr)
        {
            return true;
        }

        mappingName_ = BuildConfigMappingName(pid, api);
        mappingHandle_ = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            nullptr,
            PAGE_READWRITE,
            0,
            static_cast<DWORD>(sizeof(HookConfigHeader)),
            mappingName_.c_str());

        if (mappingHandle_ == nullptr)
        {
            return false;
        }

        mapped_ = static_cast<HookConfigHeader*>(
            MapViewOfFile(mappingHandle_, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(HookConfigHeader)));

        if (mapped_ == nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            return false;
        }

        std::memset(mapped_, 0, sizeof(HookConfigHeader));
        return true;
    }

    bool SharedHookConfigWriter::Write(
        DWORD pid,
        GraphicsApi api,
        std::uint32_t captureFpsLimit,
        bool overlayEnabled,
        std::uint32_t configFlags)
    {
        if (!Ensure(pid, api) || mapped_ == nullptr)
        {
            return false;
        }

        HookConfigHeader hdr{};
        hdr.magic = kConfigHeaderMagic;
        hdr.version = kConfigHeaderVersion;
        hdr.api = static_cast<std::uint32_t>(api);
        hdr.targetPid = pid;
        hdr.captureFpsLimit = ClampFps(captureFpsLimit);
        hdr.overlayEnabled = overlayEnabled ? 1u : 0u;
        hdr.reserved0 = configFlags;
        hdr.updatedQpc = NowQpc();

        *mapped_ = hdr;
        MemoryBarrier();
        return true;
    }

    void SharedHookConfigWriter::Reset()
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

    SharedHookConfigReader::~SharedHookConfigReader()
    {
        Reset();
    }

    SharedHookConfigReader::SharedHookConfigReader(SharedHookConfigReader&& other) noexcept
    {
        mappingHandle_ = other.mappingHandle_;
        mapped_ = other.mapped_;
        mappingName_ = std::move(other.mappingName_);

        other.mappingHandle_ = nullptr;
        other.mapped_ = nullptr;
        other.mappingName_.clear();
    }

    SharedHookConfigReader& SharedHookConfigReader::operator=(SharedHookConfigReader&& other) noexcept
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

    bool SharedHookConfigReader::Ensure(DWORD pid, GraphicsApi api)
    {
        if (mapped_ != nullptr)
        {
            return true;
        }

        mappingName_ = BuildConfigMappingName(pid, api);
        mappingHandle_ = OpenFileMappingW(FILE_MAP_READ, FALSE, mappingName_.c_str());
        if (mappingHandle_ == nullptr)
        {
            return false;
        }

        mapped_ = static_cast<const HookConfigHeader*>(
            MapViewOfFile(mappingHandle_, FILE_MAP_READ, 0, 0, sizeof(HookConfigHeader)));
        if (mapped_ == nullptr)
        {
            CloseHandle(mappingHandle_);
            mappingHandle_ = nullptr;
            return false;
        }

        return true;
    }

    bool SharedHookConfigReader::TryRead(HookConfigHeader& outHeader) const
    {
        if (mapped_ == nullptr)
        {
            return false;
        }

        MemoryBarrier();
        outHeader = *mapped_;
        MemoryBarrier();
        return true;
    }

    void SharedHookConfigReader::Reset()
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

#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

#include <windows.h>

#include "HookIpcProtocol.h"

namespace ht::hook::ipc
{
    class SharedHookStatusWriter
    {
    public:
        SharedHookStatusWriter() = default;
        ~SharedHookStatusWriter();

        SharedHookStatusWriter(const SharedHookStatusWriter&) = delete;
        SharedHookStatusWriter& operator=(const SharedHookStatusWriter&) = delete;
        SharedHookStatusWriter(SharedHookStatusWriter&& other) noexcept;
        SharedHookStatusWriter& operator=(SharedHookStatusWriter&& other) noexcept;

        bool Ensure(DWORD pid, GraphicsApi api);
        bool Write(const HookStatusHeader& status);
        void Reset();

    private:
        HANDLE mappingHandle_ = nullptr;
        std::uint8_t* mapped_ = nullptr;
        std::wstring mappingName_;
    };

    class SharedHookStatusReader
    {
    public:
        SharedHookStatusReader() = default;
        ~SharedHookStatusReader();

        SharedHookStatusReader(const SharedHookStatusReader&) = delete;
        SharedHookStatusReader& operator=(const SharedHookStatusReader&) = delete;
        SharedHookStatusReader(SharedHookStatusReader&& other) noexcept;
        SharedHookStatusReader& operator=(SharedHookStatusReader&& other) noexcept;

        bool Ensure(DWORD pid, GraphicsApi api);
        bool TryRead(HookStatusHeader& outStatus) const;
        void Reset();

    private:
        HANDLE mappingHandle_ = nullptr;
        const std::uint8_t* mapped_ = nullptr;
        std::wstring mappingName_;
    };
}


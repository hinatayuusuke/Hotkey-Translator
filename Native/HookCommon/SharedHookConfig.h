#pragma once

#include <cstdint>
#include <string>

#include <windows.h>

#include "HookIpcProtocol.h"

namespace ht::hook::ipc
{
    class SharedHookConfigWriter
    {
    public:
        SharedHookConfigWriter() = default;
        ~SharedHookConfigWriter();

        SharedHookConfigWriter(const SharedHookConfigWriter&) = delete;
        SharedHookConfigWriter& operator=(const SharedHookConfigWriter&) = delete;
        SharedHookConfigWriter(SharedHookConfigWriter&& other) noexcept;
        SharedHookConfigWriter& operator=(SharedHookConfigWriter&& other) noexcept;

        bool Ensure(DWORD pid, GraphicsApi api);
        bool Write(
            DWORD pid,
            GraphicsApi api,
            std::uint32_t captureFpsLimit,
            bool overlayEnabled,
            std::uint32_t configFlags = 0u);
        void Reset();

    private:
        HANDLE mappingHandle_ = nullptr;
        HookConfigHeader* mapped_ = nullptr;
        std::wstring mappingName_;
    };

    class SharedHookConfigReader
    {
    public:
        SharedHookConfigReader() = default;
        ~SharedHookConfigReader();

        SharedHookConfigReader(const SharedHookConfigReader&) = delete;
        SharedHookConfigReader& operator=(const SharedHookConfigReader&) = delete;
        SharedHookConfigReader(SharedHookConfigReader&& other) noexcept;
        SharedHookConfigReader& operator=(SharedHookConfigReader&& other) noexcept;

        bool Ensure(DWORD pid, GraphicsApi api);
        bool TryRead(HookConfigHeader& outHeader) const;
        void Reset();

    private:
        HANDLE mappingHandle_ = nullptr;
        const HookConfigHeader* mapped_ = nullptr;
        std::wstring mappingName_;
    };
}

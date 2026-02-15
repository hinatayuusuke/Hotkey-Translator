#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include <windows.h>

#include "HookIpcProtocol.h"

namespace ht::hook::ipc
{
    class SharedOverlayCommandsWriter
    {
    public:
        SharedOverlayCommandsWriter() = default;
        ~SharedOverlayCommandsWriter();

        SharedOverlayCommandsWriter(const SharedOverlayCommandsWriter&) = delete;
        SharedOverlayCommandsWriter& operator=(const SharedOverlayCommandsWriter&) = delete;
        SharedOverlayCommandsWriter(SharedOverlayCommandsWriter&& other) noexcept;
        SharedOverlayCommandsWriter& operator=(SharedOverlayCommandsWriter&& other) noexcept;

        bool Ensure(DWORD pid, GraphicsApi api);
        bool Write(DWORD pid, GraphicsApi api, const OverlayRectCommand* commands, std::size_t commandCount);
        void Reset();

    private:
        HANDLE mappingHandle_ = nullptr;
        std::uint8_t* mapped_ = nullptr;
        std::wstring mappingName_;
    };

    class SharedOverlayCommandsReader
    {
    public:
        SharedOverlayCommandsReader() = default;
        ~SharedOverlayCommandsReader();

        SharedOverlayCommandsReader(const SharedOverlayCommandsReader&) = delete;
        SharedOverlayCommandsReader& operator=(const SharedOverlayCommandsReader&) = delete;
        SharedOverlayCommandsReader(SharedOverlayCommandsReader&& other) noexcept;
        SharedOverlayCommandsReader& operator=(SharedOverlayCommandsReader&& other) noexcept;

        bool Ensure(DWORD pid, GraphicsApi api);
        bool TryRead(OverlayCommandHeader& outHeader, std::vector<OverlayRectCommand>& outCommands) const;
        void Reset();

    private:
        HANDLE mappingHandle_ = nullptr;
        const std::uint8_t* mapped_ = nullptr;
        std::wstring mappingName_;
    };
}


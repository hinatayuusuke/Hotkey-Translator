#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include <windows.h>

#include "HookIpcProtocol.h"

namespace ht::hook::ipc
{
    class SharedOverlayV2Writer
    {
    public:
        SharedOverlayV2Writer() = default;
        ~SharedOverlayV2Writer();

        SharedOverlayV2Writer(const SharedOverlayV2Writer&) = delete;
        SharedOverlayV2Writer& operator=(const SharedOverlayV2Writer&) = delete;
        SharedOverlayV2Writer(SharedOverlayV2Writer&& other) noexcept;
        SharedOverlayV2Writer& operator=(SharedOverlayV2Writer&& other) noexcept;

        bool Ensure(DWORD pid, GraphicsApi api);
        bool Write(
            DWORD pid,
            GraphicsApi api,
            std::uint32_t canvasW,
            std::uint32_t canvasH,
            const OverlayTextBlockV2* blocks,
            std::size_t blockCount,
            const std::uint8_t* textBlob,
            std::size_t textBytes,
            std::uint32_t flags = 0);
        void Reset();

    private:
        HANDLE mappingHandle_ = nullptr;
        std::uint8_t* mapped_ = nullptr;
        std::wstring mappingName_;
        std::uint64_t nextSeq_ = 0;
    };

    class SharedOverlayV2Reader
    {
    public:
        SharedOverlayV2Reader() = default;
        ~SharedOverlayV2Reader();

        SharedOverlayV2Reader(const SharedOverlayV2Reader&) = delete;
        SharedOverlayV2Reader& operator=(const SharedOverlayV2Reader&) = delete;
        SharedOverlayV2Reader(SharedOverlayV2Reader&& other) noexcept;
        SharedOverlayV2Reader& operator=(SharedOverlayV2Reader&& other) noexcept;

        bool Ensure(DWORD pid, GraphicsApi api);
        bool TryRead(
            OverlayV2Header& outHeader,
            std::vector<OverlayTextBlockV2>& outBlocks,
            std::vector<std::uint8_t>& outTextBlob) const;
        void Reset();

    private:
        HANDLE mappingHandle_ = nullptr;
        const std::uint8_t* mapped_ = nullptr;
        std::wstring mappingName_;
    };
}


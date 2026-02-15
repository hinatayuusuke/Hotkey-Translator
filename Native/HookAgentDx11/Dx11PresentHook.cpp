#include "Dx11PresentHook.h"

#include <atomic>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <mutex>
#include <vector>

#include <d3d11.h>
#include <dxgi.h>

#include <MinHook.h>

#include "../HookCommon/SharedFrameWriter.h"

namespace ht::hook::dx11
{
    namespace
    {
        using PresentFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT);
        using ResizeBuffersFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);

        constexpr int kSwapChainPresentIndex = 8;
        constexpr int kSwapChainResizeBuffersIndex = 13;

        struct Dx11Runtime
        {
            std::mutex mutex;
            std::atomic_bool installed{false};

            void* presentTarget = nullptr;
            void* resizeBuffersTarget = nullptr;
            PresentFn originalPresent = nullptr;
            ResizeBuffersFn originalResizeBuffers = nullptr;

            ht::hook::ipc::SharedFrameWriter frameWriter;

            ID3D11Device* device = nullptr;
            ID3D11DeviceContext* context = nullptr;
            ID3D11Texture2D* staging = nullptr;
            UINT stagingWidth = 0;
            UINT stagingHeight = 0;
            DXGI_FORMAT stagingFormat = DXGI_FORMAT_UNKNOWN;

            std::vector<std::uint8_t> scratch;
            std::uint64_t frameId = 0;
            std::uint64_t lastCaptureQpc = 0;
            std::uint64_t captureIntervalQpc = 0;
            std::uint64_t qpcFreq = 0;
        };

        Dx11Runtime g_rt;

        std::uint64_t NowQpc()
        {
            LARGE_INTEGER qpc{};
            QueryPerformanceCounter(&qpc);
            return static_cast<std::uint64_t>(qpc.QuadPart);
        }

        std::uint64_t QpcFreq()
        {
            LARGE_INTEGER freq{};
            QueryPerformanceFrequency(&freq);
            return static_cast<std::uint64_t>(freq.QuadPart);
        }

        std::uint32_t ReadEnvU32(const wchar_t* name, std::uint32_t defaultValue)
        {
            wchar_t buf[32]{};
            const DWORD got = GetEnvironmentVariableW(name, buf, static_cast<DWORD>(std::size(buf)));
            if (got == 0 || got >= std::size(buf))
            {
                return defaultValue;
            }

            wchar_t* end = nullptr;
            const unsigned long val = wcstoul(buf, &end, 10);
            if (end == buf || val == 0)
            {
                return defaultValue;
            }

            return static_cast<std::uint32_t>(val);
        }

        void SafeRelease(IUnknown*& ptr)
        {
            if (ptr != nullptr)
            {
                ptr->Release();
                ptr = nullptr;
            }
        }

        void ResetDeviceStateLocked(Dx11Runtime& rt)
        {
            // WHY: ResizeBuffers/Alt+Tab can invalidate backbuffer resources; reset so next Present can re-init.
            IUnknown* staging = rt.staging;
            rt.staging = nullptr;
            SafeRelease(staging);

            IUnknown* ctx = rt.context;
            rt.context = nullptr;
            SafeRelease(ctx);

            IUnknown* dev = rt.device;
            rt.device = nullptr;
            SafeRelease(dev);

            rt.stagingWidth = 0;
            rt.stagingHeight = 0;
            rt.stagingFormat = DXGI_FORMAT_UNKNOWN;
        }

        bool EnsureStagingLocked(Dx11Runtime& rt, ID3D11Texture2D* backBuffer)
        {
            if (backBuffer == nullptr)
            {
                return false;
            }

            D3D11_TEXTURE2D_DESC desc{};
            backBuffer->GetDesc(&desc);

            if (rt.staging != nullptr &&
                rt.stagingWidth == desc.Width &&
                rt.stagingHeight == desc.Height &&
                rt.stagingFormat == desc.Format)
            {
                return true;
            }

            if (rt.context != nullptr)
            {
                rt.context->Flush();
            }

            IUnknown* oldStaging = rt.staging;
            rt.staging = nullptr;
            SafeRelease(oldStaging);

            D3D11_TEXTURE2D_DESC stagingDesc = desc;
            stagingDesc.BindFlags = 0;
            stagingDesc.MiscFlags = 0;
            stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            stagingDesc.Usage = D3D11_USAGE_STAGING;

            ID3D11Texture2D* staging = nullptr;
            if (rt.device->CreateTexture2D(&stagingDesc, nullptr, &staging) != S_OK || staging == nullptr)
            {
                return false;
            }

            rt.staging = staging;
            rt.stagingWidth = desc.Width;
            rt.stagingHeight = desc.Height;
            rt.stagingFormat = desc.Format;
            return true;
        }

        bool EnsureDeviceLocked(Dx11Runtime& rt, IDXGISwapChain* swap)
        {
            if (rt.device != nullptr && rt.context != nullptr)
            {
                return true;
            }

            ID3D11Device* dev = nullptr;
            if (swap->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&dev)) != S_OK || dev == nullptr)
            {
                return false;
            }

            ID3D11DeviceContext* ctx = nullptr;
            dev->GetImmediateContext(&ctx);
            if (ctx == nullptr)
            {
                dev->Release();
                return false;
            }

            rt.device = dev;
            rt.context = ctx;
            return true;
        }

        bool CaptureAndShareFrameLocked(Dx11Runtime& rt, IDXGISwapChain* swap)
        {
            const auto now = NowQpc();
            if (rt.captureIntervalQpc != 0 && rt.lastCaptureQpc != 0)
            {
                if (now - rt.lastCaptureQpc < rt.captureIntervalQpc)
                {
                    return true;
                }
            }

            ID3D11Texture2D* backBuffer = nullptr;
            const HRESULT hr = swap->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer));
            if (hr != S_OK || backBuffer == nullptr)
            {
                return false;
            }

            const bool okDevice = EnsureDeviceLocked(rt, swap);
            const bool okStaging = okDevice && EnsureStagingLocked(rt, backBuffer);
            if (!okStaging)
            {
                backBuffer->Release();
                return false;
            }

            rt.context->CopyResource(rt.staging, backBuffer);
            backBuffer->Release();

            D3D11_MAPPED_SUBRESOURCE mapped{};
            const HRESULT mapHr = rt.context->Map(rt.staging, 0, D3D11_MAP_READ, 0, &mapped);
            if (mapHr != S_OK || mapped.pData == nullptr)
            {
                return false;
            }

            // NOTE: We standardize on BGRA8 in v1. Many swapchains are already this format.
            // If the title uses a different format, this capture path may be invalid; we fail fast and rely on fallback.
            if (rt.stagingFormat != DXGI_FORMAT_B8G8R8A8_UNORM && rt.stagingFormat != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
            {
                rt.context->Unmap(rt.staging, 0);
                return false;
            }

            const std::uint32_t width = rt.stagingWidth;
            const std::uint32_t height = rt.stagingHeight;
            const std::uint32_t stride = static_cast<std::uint32_t>(mapped.RowPitch);
            const std::size_t payloadBytes = static_cast<std::size_t>(stride) * static_cast<std::size_t>(height);

            rt.scratch.resize(payloadBytes);
            std::memcpy(rt.scratch.data(), mapped.pData, payloadBytes);
            rt.context->Unmap(rt.staging, 0);

            const DWORD pid = GetCurrentProcessId();
            const auto frameId = ++rt.frameId;
            const auto qpc = now;

            const bool ok = rt.frameWriter.WriteFrame(
                pid,
                ht::hook::ipc::GraphicsApi::Dx11,
                frameId,
                width,
                height,
                stride,
                qpc,
                rt.scratch.data(),
                rt.scratch.size());
            if (ok)
            {
                rt.lastCaptureQpc = now;
            }
            return ok;
        }

        HRESULT HookedPresentImpl(IDXGISwapChain* swap, UINT syncInterval, UINT flags)
        {
            std::lock_guard<std::mutex> lock(g_rt.mutex);
            if (g_rt.installed.load(std::memory_order_acquire) && swap != nullptr)
            {
                (void)CaptureAndShareFrameLocked(g_rt, swap);
            }

            return g_rt.originalPresent ? g_rt.originalPresent(swap, syncInterval, flags) : S_OK;
        }

        HRESULT __stdcall HookedPresent(IDXGISwapChain* swap, UINT syncInterval, UINT flags)
        {
            // WHY: Hook code must never crash the host process. SEH must live in a function without C++ unwinding.
            __try
            {
                return HookedPresentImpl(swap, syncInterval, flags);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return g_rt.originalPresent ? g_rt.originalPresent(swap, syncInterval, flags) : S_OK;
            }
        }

        HRESULT HookedResizeBuffersImpl(
            IDXGISwapChain* swap,
            UINT bufferCount,
            UINT width,
            UINT height,
            DXGI_FORMAT newFormat,
            UINT swapChainFlags)
        {
            std::lock_guard<std::mutex> lock(g_rt.mutex);
            ResetDeviceStateLocked(g_rt);
            return g_rt.originalResizeBuffers
                ? g_rt.originalResizeBuffers(swap, bufferCount, width, height, newFormat, swapChainFlags)
                : S_OK;
        }

        HRESULT __stdcall HookedResizeBuffers(
            IDXGISwapChain* swap,
            UINT bufferCount,
            UINT width,
            UINT height,
            DXGI_FORMAT newFormat,
            UINT swapChainFlags)
        {
            __try
            {
                return HookedResizeBuffersImpl(swap, bufferCount, width, height, newFormat, swapChainFlags);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return g_rt.originalResizeBuffers
                    ? g_rt.originalResizeBuffers(swap, bufferCount, width, height, newFormat, swapChainFlags)
                    : S_OK;
            }
        }

        bool CreateDummySwapChainAndGetVtable(void*** outVtable)
        {
            if (outVtable == nullptr)
            {
                return false;
            }

            *outVtable = nullptr;

            WNDCLASSW wc{};
            wc.lpfnWndProc = DefWindowProcW;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.lpszClassName = L"HT_HookDummyWindow";
            RegisterClassW(&wc);

            HWND hwnd = CreateWindowExW(
                0,
                wc.lpszClassName,
                L"",
                WS_OVERLAPPEDWINDOW,
                0,
                0,
                64,
                64,
                nullptr,
                nullptr,
                wc.hInstance,
                nullptr);

            if (hwnd == nullptr)
            {
                return false;
            }

            DXGI_SWAP_CHAIN_DESC scDesc{};
            scDesc.BufferDesc.Width = 64;
            scDesc.BufferDesc.Height = 64;
            scDesc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            scDesc.BufferDesc.RefreshRate.Numerator = 60;
            scDesc.BufferDesc.RefreshRate.Denominator = 1;
            scDesc.SampleDesc.Count = 1;
            scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            scDesc.BufferCount = 1;
            scDesc.OutputWindow = hwnd;
            scDesc.Windowed = TRUE;
            scDesc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

            ID3D11Device* dev = nullptr;
            ID3D11DeviceContext* ctx = nullptr;
            IDXGISwapChain* sc = nullptr;
            const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0};
            D3D_FEATURE_LEVEL levelOut{};

            UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
            HRESULT hr = D3D11CreateDeviceAndSwapChain(
                nullptr,
                D3D_DRIVER_TYPE_HARDWARE,
                nullptr,
                flags,
                levels,
                static_cast<UINT>(std::size(levels)),
                D3D11_SDK_VERSION,
                &scDesc,
                &sc,
                &dev,
                &levelOut,
                &ctx);

            if (sc == nullptr || hr != S_OK)
            {
                // WHY: Some environments can't create a hardware device at injection time. Use WARP as a last resort.
                hr = D3D11CreateDeviceAndSwapChain(
                    nullptr,
                    D3D_DRIVER_TYPE_WARP,
                    nullptr,
                    flags,
                    levels,
                    static_cast<UINT>(std::size(levels)),
                    D3D11_SDK_VERSION,
                    &scDesc,
                    &sc,
                    &dev,
                    &levelOut,
                    &ctx);
            }

            if (sc == nullptr || hr != S_OK)
            {
                if (ctx) ctx->Release();
                if (dev) dev->Release();
                DestroyWindow(hwnd);
                return false;
            }

            void** vtable = *reinterpret_cast<void***>(sc);
            *outVtable = vtable;

            sc->Release();
            if (ctx) ctx->Release();
            if (dev) dev->Release();
            DestroyWindow(hwnd);
            return true;
        }

    }

    bool InstallPresentHook()
    {
        std::lock_guard<std::mutex> lock(g_rt.mutex);
        if (g_rt.installed.load(std::memory_order_acquire))
        {
            return true;
        }

        g_rt.qpcFreq = QpcFreq();
        const std::uint32_t fpsLimit = ReadEnvU32(L"HT_HOOK_CAPTURE_FPS_LIMIT", 15);
        if (fpsLimit > 0 && g_rt.qpcFreq != 0)
        {
            g_rt.captureIntervalQpc = g_rt.qpcFreq / fpsLimit;
        }

        void** vtable = nullptr;
        if (!CreateDummySwapChainAndGetVtable(&vtable) || vtable == nullptr)
        {
            return false;
        }

        g_rt.presentTarget = vtable[kSwapChainPresentIndex];
        g_rt.resizeBuffersTarget = vtable[kSwapChainResizeBuffersIndex];

        const MH_STATUS init = MH_Initialize();
        if (init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED)
        {
            return false;
        }

        // WHY: Keep hooks explicitly paired with stored targets to support safe disable/remove at detach time.
        if (MH_CreateHook(g_rt.presentTarget, reinterpret_cast<LPVOID>(&HookedPresent), reinterpret_cast<LPVOID*>(&g_rt.originalPresent)) != MH_OK)
        {
            return false;
        }

        if (MH_CreateHook(g_rt.resizeBuffersTarget, reinterpret_cast<LPVOID>(&HookedResizeBuffers), reinterpret_cast<LPVOID*>(&g_rt.originalResizeBuffers)) != MH_OK)
        {
            (void)MH_RemoveHook(g_rt.presentTarget);
            return false;
        }

        if (MH_EnableHook(g_rt.presentTarget) != MH_OK)
        {
            (void)MH_RemoveHook(g_rt.resizeBuffersTarget);
            (void)MH_RemoveHook(g_rt.presentTarget);
            return false;
        }

        if (MH_EnableHook(g_rt.resizeBuffersTarget) != MH_OK)
        {
            (void)MH_DisableHook(g_rt.presentTarget);
            (void)MH_RemoveHook(g_rt.resizeBuffersTarget);
            (void)MH_RemoveHook(g_rt.presentTarget);
            return false;
        }

        g_rt.installed.store(true, std::memory_order_release);
        return true;
    }

    void UninstallPresentHook()
    {
        std::lock_guard<std::mutex> lock(g_rt.mutex);
        if (!g_rt.installed.load(std::memory_order_acquire))
        {
            return;
        }

        g_rt.installed.store(false, std::memory_order_release);

        // NOTE: We keep the DLL resident by policy, but detaching must disable hooks to avoid impacting the title.
        if (g_rt.presentTarget != nullptr)
        {
            (void)MH_DisableHook(g_rt.presentTarget);
            (void)MH_RemoveHook(g_rt.presentTarget);
        }

        if (g_rt.resizeBuffersTarget != nullptr)
        {
            (void)MH_DisableHook(g_rt.resizeBuffersTarget);
            (void)MH_RemoveHook(g_rt.resizeBuffersTarget);
        }

        ResetDeviceStateLocked(g_rt);
        g_rt.frameWriter.Reset();
        g_rt.scratch.clear();

        g_rt.presentTarget = nullptr;
        g_rt.resizeBuffersTarget = nullptr;
        g_rt.originalPresent = nullptr;
        g_rt.originalResizeBuffers = nullptr;
    }
}

#include "Dx9PresentHook.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <vector>

#include <d3d9.h>

#include <MinHook.h>

#include "../HookCommon/SharedFrameWriter.h"
#include "../HookCommon/SharedHookConfig.h"
#include "../HookCommon/SharedHookStatus.h"

namespace ht::hook::dx9
{
    namespace
    {
        using PresentFn = HRESULT(STDMETHODCALLTYPE*)(
            IDirect3DDevice9*,
            const RECT*,
            const RECT*,
            HWND,
            const RGNDATA*);
        using ResetFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9*, D3DPRESENT_PARAMETERS*);

        constexpr int kDeviceResetIndex = 16;
        constexpr int kDevicePresentIndex = 17;
        constexpr std::uint32_t kDefaultCaptureFps = 15u;

        // WHY: Some titles can re-enter Present on the same thread. Capture only on outer-most call.
        static thread_local int g_presentDepth = 0;

        struct Dx9Runtime
        {
            std::mutex mutex;
            std::atomic_bool installed{false};

            void* presentTarget = nullptr;
            void* resetTarget = nullptr;
            PresentFn originalPresent = nullptr;
            ResetFn originalReset = nullptr;

            ipc::SharedFrameWriter frameWriter;
            ipc::SharedHookConfigReader configReader;
            ipc::SharedHookStatusWriter statusWriter;

            std::vector<std::uint8_t> scratch;

            std::uint64_t qpcFreq = 0;
            std::uint64_t frameId = 0;
            std::uint64_t captureIntervalQpc = 0;
            std::uint64_t lastCaptureQpc = 0;
            std::uint64_t lastConfigQpc = 0;
            std::uint64_t lastFrameWriteQpc = 0;
            std::uint32_t configuredFpsLimit = kDefaultCaptureFps;
            bool overlayEnabled = false;

            std::uint64_t presentCount = 0;
            std::uint64_t lastPresentQpc = 0;
            std::uint32_t lastPresentKind = 1;
            std::uint32_t backBufferFormat = 0;
            std::uint32_t backBufferWidth = 0;
            std::uint32_t backBufferHeight = 0;
        };

        Dx9Runtime g_rt;

        std::uint64_t NowQpc()
        {
            LARGE_INTEGER qpc{};
            QueryPerformanceCounter(&qpc);
            return static_cast<std::uint64_t>(qpc.QuadPart);
        }

        std::uint64_t QueryQpcFreq()
        {
            LARGE_INTEGER freq{};
            QueryPerformanceFrequency(&freq);
            return static_cast<std::uint64_t>(freq.QuadPart);
        }

        std::uint32_t ClampFps(std::uint32_t fps)
        {
            return std::clamp(fps, 1u, 240u);
        }

        void ResetRuntimeStateLocked(Dx9Runtime& rt)
        {
            rt.frameWriter.Reset();
            rt.configReader.Reset();
            rt.statusWriter.Reset();
            rt.scratch.clear();

            rt.frameId = 0;
            rt.captureIntervalQpc = (rt.qpcFreq > 0) ? (rt.qpcFreq / kDefaultCaptureFps) : 0;
            rt.lastCaptureQpc = 0;
            rt.lastConfigQpc = 0;
            rt.lastFrameWriteQpc = 0;
            rt.configuredFpsLimit = kDefaultCaptureFps;
            rt.overlayEnabled = false;

            rt.presentCount = 0;
            rt.lastPresentQpc = 0;
            rt.lastPresentKind = 1;
            rt.backBufferFormat = 0;
            rt.backBufferWidth = 0;
            rt.backBufferHeight = 0;
        }

        bool RefreshConfigLocked(Dx9Runtime& rt)
        {
            const DWORD pid = GetCurrentProcessId();
            if (!rt.configReader.Ensure(pid, ipc::GraphicsApi::Dx9))
            {
                return false;
            }

            ipc::HookConfigHeader cfg{};
            if (!rt.configReader.TryRead(cfg))
            {
                return false;
            }

            if (cfg.magic != ipc::kConfigHeaderMagic || cfg.version != ipc::kConfigHeaderVersion)
            {
                return false;
            }

            if (cfg.api != static_cast<std::uint32_t>(ipc::GraphicsApi::Dx9))
            {
                return false;
            }

            rt.lastConfigQpc = cfg.updatedQpc;
            rt.configuredFpsLimit = ClampFps(cfg.captureFpsLimit);
            rt.captureIntervalQpc = (rt.qpcFreq > 0) ? (rt.qpcFreq / rt.configuredFpsLimit) : 0;
            rt.overlayEnabled = (cfg.overlayEnabled != 0);
            return true;
        }

        bool ShouldCaptureNowLocked(const Dx9Runtime& rt, std::uint64_t nowQpc)
        {
            if (rt.captureIntervalQpc == 0)
            {
                return true;
            }

            if (rt.lastCaptureQpc == 0)
            {
                return true;
            }

            return (nowQpc - rt.lastCaptureQpc) >= rt.captureIntervalQpc;
        }

        bool IsSupportedCaptureFormat(D3DFORMAT format)
        {
            return format == D3DFMT_A8R8G8B8 || format == D3DFMT_X8R8G8B8;
        }

        bool CaptureAndShareFrameLocked(Dx9Runtime& rt, IDirect3DDevice9* device)
        {
            if (device == nullptr)
            {
                return false;
            }

            IDirect3DSurface9* backBuffer = nullptr;
            if (FAILED(device->GetRenderTarget(0, &backBuffer)) || backBuffer == nullptr)
            {
                return false;
            }

            D3DSURFACE_DESC desc{};
            const HRESULT descHr = backBuffer->GetDesc(&desc);
            if (FAILED(descHr) || desc.Width == 0 || desc.Height == 0 || !IsSupportedCaptureFormat(desc.Format))
            {
                backBuffer->Release();
                return false;
            }

            IDirect3DSurface9* copySource = backBuffer;
            IDirect3DSurface9* resolved = nullptr;
            if (desc.MultiSampleType != D3DMULTISAMPLE_NONE)
            {
                const HRESULT resolveCreateHr = device->CreateRenderTarget(
                    desc.Width,
                    desc.Height,
                    desc.Format,
                    D3DMULTISAMPLE_NONE,
                    0,
                    FALSE,
                    &resolved,
                    nullptr);
                if (FAILED(resolveCreateHr) || resolved == nullptr)
                {
                    backBuffer->Release();
                    return false;
                }

                const HRESULT stretchHr = device->StretchRect(backBuffer, nullptr, resolved, nullptr, D3DTEXF_NONE);
                if (FAILED(stretchHr))
                {
                    resolved->Release();
                    backBuffer->Release();
                    return false;
                }

                copySource = resolved;
            }

            IDirect3DSurface9* staging = nullptr;
            const HRESULT createHr = device->CreateOffscreenPlainSurface(
                desc.Width,
                desc.Height,
                desc.Format,
                D3DPOOL_SYSTEMMEM,
                &staging,
                nullptr);
            if (FAILED(createHr) || staging == nullptr)
            {
                backBuffer->Release();
                return false;
            }

            const HRESULT copyHr = device->GetRenderTargetData(copySource, staging);
            if (resolved != nullptr)
            {
                resolved->Release();
            }
            backBuffer->Release();
            if (FAILED(copyHr))
            {
                staging->Release();
                return false;
            }

            D3DLOCKED_RECT locked{};
            const HRESULT lockHr = staging->LockRect(&locked, nullptr, D3DLOCK_READONLY);
            if (FAILED(lockHr) || locked.pBits == nullptr || locked.Pitch <= 0)
            {
                staging->Release();
                return false;
            }

            const std::uint32_t width = desc.Width;
            const std::uint32_t height = desc.Height;
            const std::uint32_t stride = width * 4u;
            const std::size_t payloadBytes = static_cast<std::size_t>(stride) * static_cast<std::size_t>(height);
            rt.scratch.resize(payloadBytes);

            for (std::uint32_t y = 0; y < height; y++)
            {
                const auto* src = static_cast<const std::uint8_t*>(locked.pBits) + (static_cast<std::size_t>(locked.Pitch) * y);
                auto* dst = rt.scratch.data() + (static_cast<std::size_t>(stride) * y);
                std::memcpy(dst, src, stride);
            }

            staging->UnlockRect();
            staging->Release();

            const DWORD pid = GetCurrentProcessId();
            const std::uint64_t frameId = ++rt.frameId;
            if (!rt.frameWriter.WriteFrame(
                    pid,
                    ipc::GraphicsApi::Dx9,
                    frameId,
                    width,
                    height,
                    stride,
                    rt.lastPresentQpc,
                    rt.scratch.data(),
                    payloadBytes))
            {
                return false;
            }

            rt.lastFrameWriteQpc = NowQpc();
            rt.lastCaptureQpc = rt.lastPresentQpc;
            rt.backBufferFormat = static_cast<std::uint32_t>(desc.Format);
            rt.backBufferWidth = width;
            rt.backBufferHeight = height;
            return true;
        }

        void PublishStatusLocked(Dx9Runtime& rt)
        {
            const DWORD pid = GetCurrentProcessId();
            if (!rt.statusWriter.Ensure(pid, ipc::GraphicsApi::Dx9))
            {
                return;
            }

            ipc::HookStatusHeader status{};
            status.api = static_cast<std::uint32_t>(ipc::GraphicsApi::Dx9);
            status.targetPid = pid;
            status.presentCount = rt.presentCount;
            status.lastPresentQpc = rt.lastPresentQpc;
            status.lastPresentKind = rt.lastPresentKind;
            status.backBufferDxgiFormat = rt.backBufferFormat;
            status.backBufferWidth = rt.backBufferWidth;
            status.backBufferHeight = rt.backBufferHeight;
            status.stagingDxgiFormat = rt.backBufferFormat;
            status.lastFrameIdWritten = rt.frameId;
            status.lastFrameWriteQpc = rt.lastFrameWriteQpc;
            status.lastCmdQpc = rt.lastConfigQpc;
            status.lastCmdCount = 0;
            (void)rt.statusWriter.Write(status);
        }

        HRESULT STDMETHODCALLTYPE HookedPresent(
            IDirect3DDevice9* device,
            const RECT* sourceRect,
            const RECT* destRect,
            HWND destWindowOverride,
            const RGNDATA* dirtyRegion)
        {
            if (g_presentDepth > 0)
            {
                return g_rt.originalPresent
                    ? g_rt.originalPresent(device, sourceRect, destRect, destWindowOverride, dirtyRegion)
                    : D3D_OK;
            }

            g_presentDepth++;
            PresentFn original = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                g_rt.presentCount++;
                g_rt.lastPresentQpc = NowQpc();
                g_rt.lastPresentKind = 1;
                (void)RefreshConfigLocked(g_rt);

                if (ShouldCaptureNowLocked(g_rt, g_rt.lastPresentQpc))
                {
                    (void)CaptureAndShareFrameLocked(g_rt, device);
                }

                PublishStatusLocked(g_rt);
                original = g_rt.originalPresent;
            }

            const HRESULT result = original
                ? original(device, sourceRect, destRect, destWindowOverride, dirtyRegion)
                : D3D_OK;
            g_presentDepth--;
            return result;
        }

        HRESULT STDMETHODCALLTYPE HookedReset(IDirect3DDevice9* device, D3DPRESENT_PARAMETERS* params)
        {
            ResetFn original = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                g_rt.backBufferWidth = 0;
                g_rt.backBufferHeight = 0;
                g_rt.backBufferFormat = 0;
                g_rt.lastCaptureQpc = 0;
                original = g_rt.originalReset;
            }

            return original ? original(device, params) : D3D_OK;
        }

        bool CreateDummyDeviceAndGetVtable(void*** outVtable)
        {
            if (outVtable == nullptr)
            {
                return false;
            }
            *outVtable = nullptr;

            WNDCLASSW wc{};
            wc.lpfnWndProc = DefWindowProcW;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.lpszClassName = L"HT_DX9_HookDummyWindow";
            (void)RegisterClassW(&wc);

            HWND hwnd = CreateWindowExW(
                0,
                wc.lpszClassName,
                L"HT_DX9_HookDummyWindow",
                WS_OVERLAPPEDWINDOW,
                0,
                0,
                2,
                2,
                nullptr,
                nullptr,
                wc.hInstance,
                nullptr);
            if (hwnd == nullptr)
            {
                return false;
            }

            IDirect3D9* d3d9 = Direct3DCreate9(D3D_SDK_VERSION);
            if (d3d9 == nullptr)
            {
                DestroyWindow(hwnd);
                return false;
            }

            D3DPRESENT_PARAMETERS pp{};
            pp.Windowed = TRUE;
            pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
            pp.hDeviceWindow = hwnd;
            pp.BackBufferFormat = D3DFMT_A8R8G8B8;
            pp.BackBufferWidth = 2;
            pp.BackBufferHeight = 2;
            pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;

            IDirect3DDevice9* device = nullptr;
            HRESULT hr = d3d9->CreateDevice(
                D3DADAPTER_DEFAULT,
                D3DDEVTYPE_HAL,
                hwnd,
                D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE,
                &pp,
                &device);
            if (FAILED(hr))
            {
                hr = d3d9->CreateDevice(
                    D3DADAPTER_DEFAULT,
                    D3DDEVTYPE_REF,
                    hwnd,
                    D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE,
                    &pp,
                    &device);
            }

            if (FAILED(hr) || device == nullptr)
            {
                d3d9->Release();
                DestroyWindow(hwnd);
                return false;
            }

            void** vtable = *reinterpret_cast<void***>(device);
            if (vtable != nullptr)
            {
                *outVtable = vtable;
            }

            device->Release();
            d3d9->Release();
            DestroyWindow(hwnd);
            return *outVtable != nullptr;
        }
    }

    bool InstallPresentHook()
    {
        auto& rt = g_rt;
        std::lock_guard<std::mutex> lock(rt.mutex);
        if (rt.installed.load(std::memory_order_acquire))
        {
            return true;
        }

        rt.qpcFreq = QueryQpcFreq();
        rt.captureIntervalQpc = (rt.qpcFreq > 0) ? (rt.qpcFreq / kDefaultCaptureFps) : 0;
        rt.configuredFpsLimit = kDefaultCaptureFps;
        rt.overlayEnabled = false;

        void** vtable = nullptr;
        if (!CreateDummyDeviceAndGetVtable(&vtable) || vtable == nullptr)
        {
            return false;
        }

        rt.resetTarget = vtable[kDeviceResetIndex];
        rt.presentTarget = vtable[kDevicePresentIndex];
        if (rt.resetTarget == nullptr || rt.presentTarget == nullptr)
        {
            return false;
        }

        const MH_STATUS initStatus = MH_Initialize();
        if (initStatus != MH_OK && initStatus != MH_ERROR_ALREADY_INITIALIZED)
        {
            return false;
        }

        if (MH_CreateHook(
                rt.presentTarget,
                reinterpret_cast<LPVOID>(&HookedPresent),
                reinterpret_cast<LPVOID*>(&rt.originalPresent)) != MH_OK)
        {
            return false;
        }

        if (MH_CreateHook(
                rt.resetTarget,
                reinterpret_cast<LPVOID>(&HookedReset),
                reinterpret_cast<LPVOID*>(&rt.originalReset)) != MH_OK)
        {
            (void)MH_RemoveHook(rt.presentTarget);
            return false;
        }

        if (MH_EnableHook(rt.presentTarget) != MH_OK)
        {
            (void)MH_RemoveHook(rt.resetTarget);
            (void)MH_RemoveHook(rt.presentTarget);
            return false;
        }

        if (MH_EnableHook(rt.resetTarget) != MH_OK)
        {
            (void)MH_DisableHook(rt.presentTarget);
            (void)MH_RemoveHook(rt.resetTarget);
            (void)MH_RemoveHook(rt.presentTarget);
            return false;
        }

        rt.installed.store(true, std::memory_order_release);
        return true;
    }

    void UninstallPresentHook()
    {
        auto& rt = g_rt;
        std::lock_guard<std::mutex> lock(rt.mutex);
        if (!rt.installed.load(std::memory_order_acquire))
        {
            return;
        }

        rt.installed.store(false, std::memory_order_release);

        if (rt.presentTarget != nullptr)
        {
            (void)MH_DisableHook(rt.presentTarget);
            (void)MH_RemoveHook(rt.presentTarget);
        }

        if (rt.resetTarget != nullptr)
        {
            (void)MH_DisableHook(rt.resetTarget);
            (void)MH_RemoveHook(rt.resetTarget);
        }

        (void)MH_Uninitialize();

        rt.presentTarget = nullptr;
        rt.resetTarget = nullptr;
        rt.originalPresent = nullptr;
        rt.originalReset = nullptr;
        ResetRuntimeStateLocked(rt);
    }
}

#include "Dx11PresentHook.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <mutex>
#include <vector>

#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi.h>
#include <dxgi1_2.h>

#include <MinHook.h>

#include "../HookCommon/SharedFrameWriter.h"
#include "../HookCommon/SharedHookConfig.h"
#include "../HookCommon/SharedOverlayCommands.h"
#include "../HookCommon/SharedOverlayV2.h"
#include "../HookCommon/SharedHookStatus.h"

namespace ht::hook::dx11
{
    namespace
    {
        using PresentFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT);
        using Present1Fn = HRESULT(__stdcall*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
        using ResizeBuffersFn = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);

        constexpr int kSwapChainPresentIndex = 8;
        constexpr int kSwapChainResizeBuffersIndex = 13;
        constexpr int kSwapChain1Present1Index = 22;

        struct Dx11Runtime
        {
            std::mutex mutex;
            std::atomic_bool installed{false};

            void* presentTarget = nullptr;
            void* present1Target = nullptr;
            void* resizeBuffersTarget = nullptr;
            PresentFn originalPresent = nullptr;
            Present1Fn originalPresent1 = nullptr;
            ResizeBuffersFn originalResizeBuffers = nullptr;

            ht::hook::ipc::SharedFrameWriter frameWriter;
            ht::hook::ipc::SharedHookConfigReader configReader;
            ht::hook::ipc::SharedOverlayCommandsReader overlayReader;
            ht::hook::ipc::SharedOverlayV2Reader overlayV2Reader;
            ht::hook::ipc::SharedHookStatusWriter statusWriter;

            ID3D11Device* device = nullptr;
            ID3D11DeviceContext* context = nullptr;
            ID3D11DeviceContext1* context1 = nullptr;
            ID3D11Texture2D* staging = nullptr;
            ID3D11RenderTargetView* backBufferRtv = nullptr;
            UINT backBufferWidth = 0;
            UINT backBufferHeight = 0;
            UINT stagingWidth = 0;
            UINT stagingHeight = 0;
            DXGI_FORMAT stagingFormat = DXGI_FORMAT_UNKNOWN;

            std::vector<std::uint8_t> scratch;
            std::uint64_t frameId = 0;
            std::uint64_t lastCaptureQpc = 0;
            std::uint64_t captureIntervalQpc = 0;
            std::uint64_t qpcFreq = 0;
            std::uint64_t lastConfigQpc = 0;
            std::uint32_t configuredFpsLimit = 15;
            bool overlayEnabled = true;
            std::uint64_t lastOverlayQpc = 0;
            std::vector<ht::hook::ipc::OverlayRectCommand> overlayCommands;

            std::uint64_t lastOverlayV2Seq = 0;
            ht::hook::ipc::OverlayV2Header overlayV2Header{};
            std::vector<ht::hook::ipc::OverlayTextBlockV2> overlayV2Blocks;
            std::vector<std::uint8_t> overlayV2TextBlob;

            std::uint64_t presentCount = 0;
            std::uint64_t lastPresentQpc = 0;
            std::uint32_t lastPresentKind = 0; // 1=Present, 2=Present1
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

            IUnknown* rtv = rt.backBufferRtv;
            rt.backBufferRtv = nullptr;
            SafeRelease(rtv);

            IUnknown* ctx1 = rt.context1;
            rt.context1 = nullptr;
            SafeRelease(ctx1);

            IUnknown* ctx = rt.context;
            rt.context = nullptr;
            SafeRelease(ctx);

            IUnknown* dev = rt.device;
            rt.device = nullptr;
            SafeRelease(dev);

            rt.backBufferWidth = 0;
            rt.backBufferHeight = 0;
            rt.stagingWidth = 0;
            rt.stagingHeight = 0;
            rt.stagingFormat = DXGI_FORMAT_UNKNOWN;
        }

        void PublishStatusLocked(Dx11Runtime& rt)
        {
            const DWORD pid = GetCurrentProcessId();
            if (!rt.statusWriter.Ensure(pid, ht::hook::ipc::GraphicsApi::Dx11))
            {
                return;
            }

            ht::hook::ipc::HookStatusHeader st{};
            st.api = static_cast<std::uint32_t>(ht::hook::ipc::GraphicsApi::Dx11);
            st.targetPid = pid;
            st.presentCount = rt.presentCount;
            st.lastPresentQpc = rt.lastPresentQpc;
            st.lastPresentKind = rt.lastPresentKind;
            st.backBufferWidth = rt.backBufferWidth;
            st.backBufferHeight = rt.backBufferHeight;
            // NOTE: We only have swapchain format when a capture path touches GetBuffer/EnsureStagingLocked.
            // This is still useful for diagnosing "mapping exists but capture fails due to unexpected format".
            st.backBufferDxgiFormat = static_cast<std::uint32_t>(rt.stagingFormat); // best-effort in v1
            st.stagingDxgiFormat = static_cast<std::uint32_t>(rt.stagingFormat);
            st.lastFrameIdWritten = rt.frameId;
            st.lastFrameWriteQpc = rt.lastCaptureQpc;
            st.lastCmdQpc = rt.lastOverlayQpc;
            st.lastCmdCount = static_cast<std::uint32_t>(rt.overlayCommands.size());
            // NOTE: Reuse reserved fields to expose v2 overlay diagnostics without changing the status struct size.
            st.reserved0 = static_cast<std::uint32_t>(rt.overlayV2TextBlob.size());
            st.reserved1 = static_cast<std::uint32_t>(rt.overlayV2Blocks.size());
            (void)rt.statusWriter.Write(st);
        }

        void RefreshConfigLocked(Dx11Runtime& rt)
        {
            if (rt.qpcFreq == 0)
            {
                rt.qpcFreq = QpcFreq();
            }

            const DWORD pid = GetCurrentProcessId();
            if (!rt.configReader.Ensure(pid, ht::hook::ipc::GraphicsApi::Dx11))
            {
                // NOTE: Fallback to env var if host didn't publish config mapping yet.
                const std::uint32_t fps = ReadEnvU32(L"HT_HOOK_CAPTURE_FPS_LIMIT", rt.configuredFpsLimit);
                rt.configuredFpsLimit = std::max(1u, fps);
                rt.captureIntervalQpc = (rt.qpcFreq != 0) ? (rt.qpcFreq / rt.configuredFpsLimit) : 0;
                return;
            }

            ht::hook::ipc::HookConfigHeader cfg{};
            if (!rt.configReader.TryRead(cfg))
            {
                return;
            }

            if (cfg.magic != ht::hook::ipc::kConfigHeaderMagic || cfg.version != ht::hook::ipc::kConfigHeaderVersion)
            {
                return;
            }

            if (cfg.updatedQpc == 0 || cfg.updatedQpc == rt.lastConfigQpc)
            {
                return;
            }

            rt.lastConfigQpc = cfg.updatedQpc;
            rt.configuredFpsLimit = std::max(1u, cfg.captureFpsLimit);
            rt.overlayEnabled = cfg.overlayEnabled != 0;
            rt.captureIntervalQpc = (rt.qpcFreq != 0) ? (rt.qpcFreq / rt.configuredFpsLimit) : 0;
        }

        bool EnsureDeviceLocked(Dx11Runtime& rt, IDXGISwapChain* swap);

        bool EnsureContext1Locked(Dx11Runtime& rt)
        {
            if (rt.context1 != nullptr)
            {
                return true;
            }
            if (rt.context == nullptr)
            {
                return false;
            }

            ID3D11DeviceContext1* ctx1 = nullptr;
            if (rt.context->QueryInterface(__uuidof(ID3D11DeviceContext1), reinterpret_cast<void**>(&ctx1)) != S_OK || ctx1 == nullptr)
            {
                return false;
            }

            rt.context1 = ctx1;
            return true;
        }

        bool EnsureBackBufferRtvLocked(Dx11Runtime& rt, IDXGISwapChain* swap)
        {
            if (rt.backBufferRtv != nullptr)
            {
                return true;
            }
            if (rt.device == nullptr || swap == nullptr)
            {
                return false;
            }

            ID3D11Texture2D* backBuffer = nullptr;
            const HRESULT hr = swap->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backBuffer));
            if (hr != S_OK || backBuffer == nullptr)
            {
                return false;
            }

            D3D11_TEXTURE2D_DESC bbDesc{};
            backBuffer->GetDesc(&bbDesc);

            ID3D11RenderTargetView* rtv = nullptr;
            const HRESULT rtvHr = rt.device->CreateRenderTargetView(backBuffer, nullptr, &rtv);
            backBuffer->Release();
            if (rtvHr != S_OK || rtv == nullptr)
            {
                return false;
            }

            rt.backBufferRtv = rtv;
            rt.backBufferWidth = bbDesc.Width;
            rt.backBufferHeight = bbDesc.Height;
            return true;
        }

        bool RefreshOverlayCommandsLocked(Dx11Runtime& rt)
        {
            const DWORD pid = GetCurrentProcessId();
            if (!rt.overlayReader.Ensure(pid, ht::hook::ipc::GraphicsApi::Dx11))
            {
                return false;
            }

            ht::hook::ipc::OverlayCommandHeader header{};
            if (!rt.overlayReader.TryRead(header, rt.overlayCommands))
            {
                return false;
            }

            if (header.updatedQpc == 0 || header.updatedQpc == rt.lastOverlayQpc)
            {
                return false;
            }

            rt.lastOverlayQpc = header.updatedQpc;
            return true;
        }

        bool RefreshOverlayV2Locked(Dx11Runtime& rt)
        {
            const DWORD pid = GetCurrentProcessId();
            if (!rt.overlayV2Reader.Ensure(pid, ht::hook::ipc::GraphicsApi::Dx11))
            {
                return false;
            }

            ht::hook::ipc::OverlayV2Header header{};
            if (!rt.overlayV2Reader.TryRead(header, rt.overlayV2Blocks, rt.overlayV2TextBlob))
            {
                return false;
            }

            if (header.updatedSeq == 0 || header.updatedSeq == rt.lastOverlayV2Seq)
            {
                return false;
            }

            rt.lastOverlayV2Seq = header.updatedSeq;
            rt.overlayV2Header = header;
            return true;
        }

        D3D11_RECT ClampRect(int left, int top, int right, int bottom, int maxW, int maxH)
        {
            D3D11_RECT r{};
            r.left = std::max(0, std::min(left, maxW));
            r.top = std::max(0, std::min(top, maxH));
            r.right = std::max(0, std::min(right, maxW));
            r.bottom = std::max(0, std::min(bottom, maxH));
            return r;
        }

        void DrawOverlayLocked(Dx11Runtime& rt, IDXGISwapChain* swap)
        {
            if (!rt.overlayEnabled)
            {
                return;
            }

            if (!EnsureDeviceLocked(rt, swap) || !EnsureContext1Locked(rt))
            {
                return;
            }

            // NOTE: Overlay commands are "latest only"; we cache by updatedQpc to avoid redundant memcpy.
            (void)RefreshOverlayCommandsLocked(rt);
            if (rt.overlayCommands.empty())
            {
                return;
            }

            if (!EnsureBackBufferRtvLocked(rt, swap))
            {
                return;
            }

            const int maxW = static_cast<int>(rt.backBufferWidth != 0 ? rt.backBufferWidth : rt.stagingWidth);
            const int maxH = static_cast<int>(rt.backBufferHeight != 0 ? rt.backBufferHeight : rt.stagingHeight);
            if (maxW <= 0 || maxH <= 0)
            {
                return;
            }

            for (const auto& cmd : rt.overlayCommands)
            {
                const int x = static_cast<int>(cmd.x);
                const int y = static_cast<int>(cmd.y);
                const int w = static_cast<int>(cmd.w);
                const int h = static_cast<int>(cmd.h);
                if (w <= 1 || h <= 1)
                {
                    continue;
                }

                const int t = std::max(1, std::min(static_cast<int>(cmd.thickness), 24));
                const int left = x;
                const int top = y;
                const int right = x + w;
                const int bottom = y + h;
                if (right <= 0 || bottom <= 0 || left >= maxW || top >= maxH)
                {
                    continue;
                }

                const std::uint32_t argb = cmd.argb;
                const float r = static_cast<float>((argb >> 16) & 0xFF) / 255.0f;
                const float g = static_cast<float>((argb >> 8) & 0xFF) / 255.0f;
                const float b = static_cast<float>((argb >> 0) & 0xFF) / 255.0f;
                // NOTE: ClearView does not alpha-blend; treat as opaque debug overlay in v1.
                const float color[4] = {r, g, b, 1.0f};

                D3D11_RECT rects[4] = {
                    ClampRect(left, top, right, top + t, maxW, maxH),
                    ClampRect(left, bottom - t, right, bottom, maxW, maxH),
                    ClampRect(left, top, left + t, bottom, maxW, maxH),
                    ClampRect(right - t, top, right, bottom, maxW, maxH),
                };

                rt.context1->ClearView(rt.backBufferRtv, color, rects, static_cast<UINT>(std::size(rects)));
            }
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
            RefreshConfigLocked(rt);

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

            // NOTE: We standardize on BGRA8 in v1 (Windows bitmap compatibility).
            // Some titles use RGBA8; in that case we swizzle to BGRA on CPU after readback.
            const bool isBgra8 = (rt.stagingFormat == DXGI_FORMAT_B8G8R8A8_UNORM || rt.stagingFormat == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB);
            const bool isRgba8 = (rt.stagingFormat == DXGI_FORMAT_R8G8B8A8_UNORM || rt.stagingFormat == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB);
            if (!isBgra8 && !isRgba8)
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

            if (isRgba8)
            {
                const std::uint32_t rowBytes = width * 4;
                for (std::uint32_t y = 0; y < height; y++)
                {
                    auto* row = rt.scratch.data() + (static_cast<std::size_t>(y) * stride);
                    for (std::uint32_t x = 0; x < rowBytes; x += 4)
                    {
                        std::swap(row[x + 0], row[x + 2]); // RGBA -> BGRA
                    }
                }
            }

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
                g_rt.presentCount++;
                g_rt.lastPresentQpc = NowQpc();
                g_rt.lastPresentKind = 1;
                (void)CaptureAndShareFrameLocked(g_rt, swap);
                DrawOverlayLocked(g_rt, swap);
                // Step 1: Read v2 overlay mapping for diagnostics only (rendering is introduced in later steps).
                (void)RefreshOverlayV2Locked(g_rt);
                PublishStatusLocked(g_rt);
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

        HRESULT HookedPresent1Impl(IDXGISwapChain1* swap, UINT syncInterval, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
        {
            std::lock_guard<std::mutex> lock(g_rt.mutex);
            if (g_rt.installed.load(std::memory_order_acquire) && swap != nullptr)
            {
                g_rt.presentCount++;
                g_rt.lastPresentQpc = NowQpc();
                g_rt.lastPresentKind = 2;
                (void)CaptureAndShareFrameLocked(g_rt, swap);
                DrawOverlayLocked(g_rt, swap);
                (void)RefreshOverlayV2Locked(g_rt);
                PublishStatusLocked(g_rt);
            }

            return g_rt.originalPresent1 ? g_rt.originalPresent1(swap, syncInterval, flags, params) : S_OK;
        }

        HRESULT __stdcall HookedPresent1(IDXGISwapChain1* swap, UINT syncInterval, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
        {
            __try
            {
                return HookedPresent1Impl(swap, syncInterval, flags, params);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return g_rt.originalPresent1 ? g_rt.originalPresent1(swap, syncInterval, flags, params) : S_OK;
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

        bool CreateDummySwapChainAndGetVtables(void*** outVtableSwapChain, void*** outVtableSwapChain1)
        {
            if (outVtableSwapChain == nullptr || outVtableSwapChain1 == nullptr)
            {
                return false;
            }

            *outVtableSwapChain = nullptr;
            *outVtableSwapChain1 = nullptr;

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
            *outVtableSwapChain = vtable;

            IDXGISwapChain1* sc1 = nullptr;
            if (sc->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&sc1)) == S_OK && sc1 != nullptr)
            {
                void** vtable1 = *reinterpret_cast<void***>(sc1);
                *outVtableSwapChain1 = vtable1;
                sc1->Release();
            }

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
        // NOTE: Config mapping may not be available at install time; RefreshConfigLocked handles fallback.
        g_rt.configuredFpsLimit = 15;
        g_rt.captureIntervalQpc = (g_rt.qpcFreq != 0) ? (g_rt.qpcFreq / g_rt.configuredFpsLimit) : 0;
        g_rt.lastConfigQpc = 0;
        g_rt.overlayEnabled = true;

        void** vtable = nullptr;
        void** vtable1 = nullptr;
        if (!CreateDummySwapChainAndGetVtables(&vtable, &vtable1) || vtable == nullptr)
        {
            return false;
        }

        g_rt.presentTarget = vtable[kSwapChainPresentIndex];
        g_rt.resizeBuffersTarget = vtable[kSwapChainResizeBuffersIndex];
        g_rt.present1Target = (vtable1 != nullptr) ? vtable1[kSwapChain1Present1Index] : nullptr;

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

        if (g_rt.present1Target != nullptr)
        {
            if (MH_CreateHook(g_rt.present1Target, reinterpret_cast<LPVOID>(&HookedPresent1), reinterpret_cast<LPVOID*>(&g_rt.originalPresent1)) != MH_OK)
            {
                (void)MH_RemoveHook(g_rt.presentTarget);
                return false;
            }
        }

        if (MH_CreateHook(g_rt.resizeBuffersTarget, reinterpret_cast<LPVOID>(&HookedResizeBuffers), reinterpret_cast<LPVOID*>(&g_rt.originalResizeBuffers)) != MH_OK)
        {
            if (g_rt.present1Target != nullptr)
            {
                (void)MH_RemoveHook(g_rt.present1Target);
            }
            (void)MH_RemoveHook(g_rt.presentTarget);
            return false;
        }

        if (MH_EnableHook(g_rt.presentTarget) != MH_OK)
        {
            (void)MH_RemoveHook(g_rt.resizeBuffersTarget);
            if (g_rt.present1Target != nullptr)
            {
                (void)MH_RemoveHook(g_rt.present1Target);
            }
            (void)MH_RemoveHook(g_rt.presentTarget);
            return false;
        }

        if (g_rt.present1Target != nullptr)
        {
            if (MH_EnableHook(g_rt.present1Target) != MH_OK)
            {
                (void)MH_DisableHook(g_rt.presentTarget);
                (void)MH_RemoveHook(g_rt.resizeBuffersTarget);
                (void)MH_RemoveHook(g_rt.present1Target);
                (void)MH_RemoveHook(g_rt.presentTarget);
                return false;
            }
        }

        if (MH_EnableHook(g_rt.resizeBuffersTarget) != MH_OK)
        {
            (void)MH_DisableHook(g_rt.presentTarget);
            (void)MH_RemoveHook(g_rt.resizeBuffersTarget);
            if (g_rt.present1Target != nullptr)
            {
                (void)MH_RemoveHook(g_rt.present1Target);
            }
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

        if (g_rt.present1Target != nullptr)
        {
            (void)MH_DisableHook(g_rt.present1Target);
            (void)MH_RemoveHook(g_rt.present1Target);
        }

        if (g_rt.resizeBuffersTarget != nullptr)
        {
            (void)MH_DisableHook(g_rt.resizeBuffersTarget);
            (void)MH_RemoveHook(g_rt.resizeBuffersTarget);
        }

        ResetDeviceStateLocked(g_rt);
        g_rt.frameWriter.Reset();
        g_rt.configReader.Reset();
        g_rt.statusWriter.Reset();
        g_rt.scratch.clear();

        g_rt.presentTarget = nullptr;
        g_rt.present1Target = nullptr;
        g_rt.resizeBuffersTarget = nullptr;
        g_rt.originalPresent = nullptr;
        g_rt.originalPresent1 = nullptr;
        g_rt.originalResizeBuffers = nullptr;
        g_rt.lastConfigQpc = 0;
        g_rt.presentCount = 0;
        g_rt.lastPresentQpc = 0;
        g_rt.lastPresentKind = 0;
    }
}

#include "Dx9PresentHook.h"

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
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

            IDirect3DSurface9* stagingSurface = nullptr;
            IDirect3DSurface9* resolvedSurface = nullptr;
            std::uint32_t surfaceWidth = 0;
            std::uint32_t surfaceHeight = 0;
            std::uint32_t surfaceFormat = 0;
            std::uint32_t surfaceMsaaType = 0;
            std::uint32_t surfaceMsaaQuality = 0;
            std::uint64_t surfaceRecreateCount = 0;

            std::uint64_t resetCount = 0;
            std::uint64_t lastResetQpc = 0;
            bool pendingPostResetRebind = false;
            std::uint32_t lastCaptureFailureHr = 0;
            std::uint64_t lastCaptureFailureQpc = 0;
        };

        Dx9Runtime g_rt;
        std::mutex g_diagFileMutex;
        HANDLE g_diagFileHandle = INVALID_HANDLE_VALUE;
        DWORD g_diagFilePid = 0;
        std::wstring g_diagFilePath;

        std::uint64_t NowQpc();

        std::string WideToUtf8(const std::wstring& value)
        {
            if (value.empty())
            {
                return {};
            }

            const int bytes = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
            if (bytes <= 1)
            {
                return {};
            }

            std::string out(static_cast<std::size_t>(bytes - 1), '\0');
            (void)WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, out.data(), bytes, nullptr, nullptr);
            return out;
        }

        bool EnsureDiagFileUnlocked()
        {
            const DWORD pid = GetCurrentProcessId();
            if (g_diagFileHandle != INVALID_HANDLE_VALUE && g_diagFilePid == pid)
            {
                return true;
            }

            if (g_diagFileHandle != INVALID_HANDLE_VALUE)
            {
                CloseHandle(g_diagFileHandle);
                g_diagFileHandle = INVALID_HANDLE_VALUE;
                g_diagFilePid = 0;
                g_diagFilePath.clear();
            }

            wchar_t tempPath[MAX_PATH]{};
            const DWORD tempLen = GetTempPathW(static_cast<DWORD>(std::size(tempPath)), tempPath);
            if (tempLen == 0 || tempLen >= std::size(tempPath))
            {
                return false;
            }

            std::wstring dir = tempPath;
            if (!dir.empty() && dir.back() != L'\\' && dir.back() != L'/')
            {
                dir += L'\\';
            }
            dir += L"HotkeyTranslator";
            (void)CreateDirectoryW(dir.c_str(), nullptr);

            wchar_t fileName[128]{};
            (void)swprintf_s(fileName, L"hook_dx9_%lu.log", static_cast<unsigned long>(pid));
            std::wstring filePath = dir;
            filePath += L'\\';
            filePath += fileName;

            HANDLE file = CreateFileW(
                filePath.c_str(),
                FILE_APPEND_DATA,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr,
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
            if (file == INVALID_HANDLE_VALUE)
            {
                return false;
            }

            g_diagFileHandle = file;
            g_diagFilePid = pid;
            g_diagFilePath = std::move(filePath);

            const auto pathUtf8 = WideToUtf8(g_diagFilePath);
            if (!pathUtf8.empty())
            {
                char openMsg[512]{};
                (void)_snprintf_s(
                    openMsg,
                    sizeof(openMsg),
                    _TRUNCATE,
                    "stage=hook_dx9 event=file_log_open pid=%lu path=\"%s\".",
                    static_cast<unsigned long>(pid),
                    pathUtf8.c_str());
                OutputDebugStringA(openMsg);
                OutputDebugStringA("\n");
            }

            return true;
        }

        void AppendDiagFileLine(const char* line)
        {
            if (line == nullptr || line[0] == '\0')
            {
                return;
            }

            std::lock_guard<std::mutex> lock(g_diagFileMutex);
            if (!EnsureDiagFileUnlocked())
            {
                return;
            }

            SYSTEMTIME st{};
            GetLocalTime(&st);
            char prefix[64]{};
            (void)_snprintf_s(
                prefix,
                sizeof(prefix),
                _TRUNCATE,
                "%02u:%02u:%02u.%03u ",
                static_cast<unsigned int>(st.wHour),
                static_cast<unsigned int>(st.wMinute),
                static_cast<unsigned int>(st.wSecond),
                static_cast<unsigned int>(st.wMilliseconds));

            DWORD written = 0;
            (void)WriteFile(g_diagFileHandle, prefix, static_cast<DWORD>(std::strlen(prefix)), &written, nullptr);
            (void)WriteFile(g_diagFileHandle, line, static_cast<DWORD>(std::strlen(line)), &written, nullptr);
            static constexpr char kNewLine[] = "\r\n";
            (void)WriteFile(g_diagFileHandle, kNewLine, static_cast<DWORD>(sizeof(kNewLine) - 1), &written, nullptr);
        }

        void CloseDiagFile()
        {
            std::lock_guard<std::mutex> lock(g_diagFileMutex);
            if (g_diagFileHandle != INVALID_HANDLE_VALUE)
            {
                CloseHandle(g_diagFileHandle);
                g_diagFileHandle = INVALID_HANDLE_VALUE;
            }
            g_diagFilePid = 0;
            g_diagFilePath.clear();
        }

        void LogDx9(const char* format, ...)
        {
            char message[1024]{};
            va_list args;
            va_start(args, format);
            (void)vsnprintf_s(message, sizeof(message), _TRUNCATE, format, args);
            va_end(args);

            char line[1200]{};
            (void)snprintf(line, sizeof(line), "stage=hook_dx9 %s\n", message);
            OutputDebugStringA(line);
            const std::size_t lineLen = std::strlen(line);
            if (lineLen > 0 && line[lineLen - 1] == '\n')
            {
                line[lineLen - 1] = '\0';
            }
            AppendDiagFileLine(line);
        }

        void ReleaseCaptureSurfacesLocked(Dx9Runtime& rt, const char* reason)
        {
            const bool hadStaging = rt.stagingSurface != nullptr;
            const bool hadResolved = rt.resolvedSurface != nullptr;

            if (rt.stagingSurface != nullptr)
            {
                rt.stagingSurface->Release();
                rt.stagingSurface = nullptr;
            }

            if (rt.resolvedSurface != nullptr)
            {
                rt.resolvedSurface->Release();
                rt.resolvedSurface = nullptr;
            }

            rt.surfaceWidth = 0;
            rt.surfaceHeight = 0;
            rt.surfaceFormat = 0;
            rt.surfaceMsaaType = 0;
            rt.surfaceMsaaQuality = 0;

            if (hadStaging || hadResolved)
            {
                LogDx9(
                    "event=capture_surfaces_release reason=%s hadStaging=%u hadResolved=%u.",
                    reason != nullptr ? reason : "none",
                    hadStaging ? 1u : 0u,
                    hadResolved ? 1u : 0u);
            }
        }

        bool ShouldLogCaptureFailureLocked(const Dx9Runtime& rt, HRESULT hr, std::uint64_t nowQpc)
        {
            if (hr != static_cast<HRESULT>(rt.lastCaptureFailureHr))
            {
                return true;
            }

            if (rt.qpcFreq == 0)
            {
                return true;
            }

            constexpr std::uint64_t kFailureLogIntervalQpcSec = 2;
            return (nowQpc - rt.lastCaptureFailureQpc) >= (rt.qpcFreq * kFailureLogIntervalQpcSec);
        }

        void RecordCaptureFailureLocked(Dx9Runtime& rt, const char* reason, HRESULT hr)
        {
            const auto nowQpc = NowQpc();
            if (!ShouldLogCaptureFailureLocked(rt, hr, nowQpc))
            {
                return;
            }

            rt.lastCaptureFailureHr = static_cast<std::uint32_t>(hr);
            rt.lastCaptureFailureQpc = nowQpc;

            LogDx9(
                "event=capture_fail reason=%s hr=0x%08X resetCount=%llu pendingPostResetRebind=%u.",
                reason != nullptr ? reason : "unknown",
                static_cast<unsigned int>(static_cast<std::uint32_t>(hr)),
                static_cast<unsigned long long>(rt.resetCount),
                rt.pendingPostResetRebind ? 1u : 0u);

            if (rt.pendingPostResetRebind)
            {
                LogDx9(
                    "event=post_reset_rebind_failed reason=%s hr=0x%08X resetCount=%llu.",
                    reason != nullptr ? reason : "unknown",
                    static_cast<unsigned int>(static_cast<std::uint32_t>(hr)),
                    static_cast<unsigned long long>(rt.resetCount));
            }
        }

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

            ReleaseCaptureSurfacesLocked(rt, "runtime_reset");
            rt.surfaceRecreateCount = 0;
            rt.resetCount = 0;
            rt.lastResetQpc = 0;
            rt.pendingPostResetRebind = false;
            rt.lastCaptureFailureHr = 0;
            rt.lastCaptureFailureQpc = 0;
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

        bool IsExecutableProtection(DWORD protect)
        {
            const DWORD baseProtect = protect & 0xFFu;
            return
                baseProtect == PAGE_EXECUTE ||
                baseProtect == PAGE_EXECUTE_READ ||
                baseProtect == PAGE_EXECUTE_READWRITE ||
                baseProtect == PAGE_EXECUTE_WRITECOPY;
        }

        bool ValidateHookTargetPointer(void* target, const char* name)
        {
            if (target == nullptr)
            {
                LogDx9("event=install_step step=validate_target result=fail reason=null_target name=%s.", name != nullptr ? name : "unknown");
                return false;
            }

            MEMORY_BASIC_INFORMATION mbi{};
            const SIZE_T queried = VirtualQuery(target, &mbi, sizeof(mbi));
            if (queried != sizeof(mbi))
            {
                LogDx9(
                    "event=install_step step=validate_target result=fail reason=virtual_query_failed name=%s target=%p gle=%lu.",
                    name != nullptr ? name : "unknown",
                    target,
                    static_cast<unsigned long>(GetLastError()));
                return false;
            }

            const bool committed = mbi.State == MEM_COMMIT;
            const bool guarded = (mbi.Protect & PAGE_GUARD) != 0;
            const bool noAccess = (mbi.Protect & PAGE_NOACCESS) != 0;
            const bool executable = IsExecutableProtection(mbi.Protect);
            if (!committed || guarded || noAccess || !executable)
            {
                LogDx9(
                    "event=install_step step=validate_target result=fail reason=invalid_page name=%s target=%p state=0x%08X protect=0x%08X type=0x%08X.",
                    name != nullptr ? name : "unknown",
                    target,
                    static_cast<unsigned int>(mbi.State),
                    static_cast<unsigned int>(mbi.Protect),
                    static_cast<unsigned int>(mbi.Type));
                return false;
            }

            LogDx9(
                "event=install_step step=validate_target result=ok name=%s target=%p protect=0x%08X.",
                name != nullptr ? name : "unknown",
                target,
                static_cast<unsigned int>(mbi.Protect));
            return true;
        }

        bool EnsureCaptureSurfacesLocked(Dx9Runtime& rt, IDirect3DDevice9* device, const D3DSURFACE_DESC& desc)
        {
            if (device == nullptr)
            {
                return false;
            }

            const std::uint32_t width = desc.Width;
            const std::uint32_t height = desc.Height;
            const std::uint32_t format = static_cast<std::uint32_t>(desc.Format);
            const std::uint32_t msaaType = static_cast<std::uint32_t>(desc.MultiSampleType);
            const std::uint32_t msaaQuality = desc.MultiSampleQuality;
            const bool needsResolve = desc.MultiSampleType != D3DMULTISAMPLE_NONE;

            const bool shapeChanged =
                rt.surfaceWidth != width ||
                rt.surfaceHeight != height ||
                rt.surfaceFormat != format ||
                rt.surfaceMsaaType != msaaType ||
                rt.surfaceMsaaQuality != msaaQuality;

            const bool resolveMismatch =
                (needsResolve && rt.resolvedSurface == nullptr) ||
                (!needsResolve && rt.resolvedSurface != nullptr);

            const bool needsRecreate =
                rt.stagingSurface == nullptr ||
                shapeChanged ||
                resolveMismatch ||
                rt.pendingPostResetRebind;

            if (!needsRecreate)
            {
                return true;
            }

            const char* recreateReason = "surface_mismatch";
            if (rt.pendingPostResetRebind)
            {
                recreateReason = "post_reset";
            }
            else if (rt.stagingSurface == nullptr)
            {
                recreateReason = "staging_missing";
            }
            else if (resolveMismatch)
            {
                recreateReason = "resolve_mode_changed";
            }
            else if (shapeChanged)
            {
                recreateReason = "backbuffer_changed";
            }

            ReleaseCaptureSurfacesLocked(rt, "recreate");

            if (needsResolve)
            {
                const auto resolveHr = device->CreateRenderTarget(
                    width,
                    height,
                    desc.Format,
                    D3DMULTISAMPLE_NONE,
                    0,
                    FALSE,
                    &rt.resolvedSurface,
                    nullptr);
                if (FAILED(resolveHr) || rt.resolvedSurface == nullptr)
                {
                    RecordCaptureFailureLocked(rt, "create_resolve_surface_failed", resolveHr);
                    ReleaseCaptureSurfacesLocked(rt, "recreate_failed_resolve");
                    return false;
                }
            }

            const auto stagingHr = device->CreateOffscreenPlainSurface(
                width,
                height,
                desc.Format,
                D3DPOOL_SYSTEMMEM,
                &rt.stagingSurface,
                nullptr);
            if (FAILED(stagingHr) || rt.stagingSurface == nullptr)
            {
                RecordCaptureFailureLocked(rt, "create_staging_surface_failed", stagingHr);
                ReleaseCaptureSurfacesLocked(rt, "recreate_failed_staging");
                return false;
            }

            rt.surfaceWidth = width;
            rt.surfaceHeight = height;
            rt.surfaceFormat = format;
            rt.surfaceMsaaType = msaaType;
            rt.surfaceMsaaQuality = msaaQuality;
            rt.surfaceRecreateCount++;

            LogDx9(
                "event=capture_surfaces_recreate reason=%s resetCount=%llu count=%llu size=%ux%u format=%u msaaType=%u msaaQuality=%u.",
                recreateReason,
                static_cast<unsigned long long>(rt.resetCount),
                static_cast<unsigned long long>(rt.surfaceRecreateCount),
                width,
                height,
                format,
                msaaType,
                msaaQuality);

            if (rt.pendingPostResetRebind)
            {
                rt.pendingPostResetRebind = false;
                LogDx9(
                    "event=post_reset_rebind_ok resetCount=%llu size=%ux%u format=%u.",
                    static_cast<unsigned long long>(rt.resetCount),
                    width,
                    height,
                    format);
            }

            return true;
        }

        bool CaptureAndShareFrameLocked(Dx9Runtime& rt, IDirect3DDevice9* device)
        {
            if (device == nullptr)
            {
                RecordCaptureFailureLocked(rt, "device_null", E_POINTER);
                return false;
            }

            IDirect3DSurface9* backBuffer = nullptr;
            const auto getRtHr = device->GetRenderTarget(0, &backBuffer);
            if (FAILED(getRtHr) || backBuffer == nullptr)
            {
                RecordCaptureFailureLocked(rt, "get_render_target_failed", getRtHr);
                return false;
            }

            D3DSURFACE_DESC desc{};
            const HRESULT descHr = backBuffer->GetDesc(&desc);
            if (FAILED(descHr) || desc.Width == 0 || desc.Height == 0 || !IsSupportedCaptureFormat(desc.Format))
            {
                RecordCaptureFailureLocked(rt, "backbuffer_desc_invalid", descHr);
                backBuffer->Release();
                return false;
            }

            if (!EnsureCaptureSurfacesLocked(rt, device, desc))
            {
                backBuffer->Release();
                return false;
            }

            IDirect3DSurface9* copySource = backBuffer;
            if (desc.MultiSampleType != D3DMULTISAMPLE_NONE)
            {
                if (rt.resolvedSurface == nullptr)
                {
                    RecordCaptureFailureLocked(rt, "resolve_surface_missing", E_FAIL);
                    backBuffer->Release();
                    return false;
                }

                const HRESULT stretchHr = device->StretchRect(backBuffer, nullptr, rt.resolvedSurface, nullptr, D3DTEXF_NONE);
                if (FAILED(stretchHr))
                {
                    RecordCaptureFailureLocked(rt, "stretch_rect_failed", stretchHr);
                    backBuffer->Release();
                    return false;
                }

                copySource = rt.resolvedSurface;
            }

            if (rt.stagingSurface == nullptr)
            {
                RecordCaptureFailureLocked(rt, "staging_surface_missing", E_FAIL);
                backBuffer->Release();
                return false;
            }

            const HRESULT copyHr = device->GetRenderTargetData(copySource, rt.stagingSurface);
            backBuffer->Release();
            if (FAILED(copyHr))
            {
                RecordCaptureFailureLocked(rt, "get_render_target_data_failed", copyHr);
                return false;
            }

            D3DLOCKED_RECT locked{};
            const HRESULT lockHr = rt.stagingSurface->LockRect(&locked, nullptr, D3DLOCK_READONLY);
            if (FAILED(lockHr) || locked.pBits == nullptr || locked.Pitch <= 0)
            {
                RecordCaptureFailureLocked(rt, "staging_lock_failed", lockHr);
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

            rt.stagingSurface->UnlockRect();

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
                RecordCaptureFailureLocked(rt, "shared_frame_write_failed", E_FAIL);
                return false;
            }

            rt.lastCaptureFailureHr = 0;
            rt.lastCaptureFailureQpc = 0;
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
            std::uint64_t presentCount = 0;
            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                ReleaseCaptureSurfacesLocked(g_rt, "reset_begin");
                g_rt.backBufferWidth = 0;
                g_rt.backBufferHeight = 0;
                g_rt.backBufferFormat = 0;
                g_rt.lastCaptureQpc = 0;
                presentCount = g_rt.presentCount;
                original = g_rt.originalReset;
            }

            const auto result = original ? original(device, params) : D3D_OK;

            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                if (SUCCEEDED(result))
                {
                    g_rt.resetCount++;
                    g_rt.lastResetQpc = NowQpc();
                    g_rt.pendingPostResetRebind = true;
                    LogDx9(
                        "event=reset_result result=ok hr=0x%08X resetCount=%llu presentCount=%llu.",
                        static_cast<unsigned int>(static_cast<std::uint32_t>(result)),
                        static_cast<unsigned long long>(g_rt.resetCount),
                        static_cast<unsigned long long>(presentCount));
                }
                else
                {
                    LogDx9(
                        "event=reset_result result=failed hr=0x%08X resetCount=%llu presentCount=%llu.",
                        static_cast<unsigned int>(static_cast<std::uint32_t>(result)),
                        static_cast<unsigned long long>(g_rt.resetCount),
                        static_cast<unsigned long long>(presentCount));
                }
            }

            return result;
        }

        bool CreateDummyDeviceAndGetHookTargets(
            void** outPresentTarget,
            void** outResetTarget,
            std::uint32_t& outExceptionCode,
            void*** outVtable)
        {
            outExceptionCode = 0;
            if (outPresentTarget == nullptr || outResetTarget == nullptr || outVtable == nullptr)
            {
                LogDx9("event=create_dummy_device fail reason=out_target_null.");
                return false;
            }

            *outPresentTarget = nullptr;
            *outResetTarget = nullptr;
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
                LogDx9("event=create_dummy_device fail reason=create_window_failed gle=%lu.", static_cast<unsigned long>(GetLastError()));
                return false;
            }

            IDirect3D9* d3d9 = Direct3DCreate9(D3D_SDK_VERSION);
            if (d3d9 == nullptr)
            {
                LogDx9("event=create_dummy_device fail reason=direct3d_create9_failed.");
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
                LogDx9("event=create_dummy_device fail reason=create_device_failed hr=0x%08X.", static_cast<unsigned int>(static_cast<std::uint32_t>(hr)));
                d3d9->Release();
                DestroyWindow(hwnd);
                return false;
            }

            void** vtable = nullptr;
            __try
            {
                vtable = *reinterpret_cast<void***>(device);
                if (vtable != nullptr)
                {
                    *outResetTarget = vtable[kDeviceResetIndex];
                    *outPresentTarget = vtable[kDevicePresentIndex];
                    *outVtable = vtable;
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                outExceptionCode = static_cast<std::uint32_t>(GetExceptionCode());
            }

            device->Release();
            d3d9->Release();
            DestroyWindow(hwnd);

            if (outExceptionCode != 0)
            {
                LogDx9(
                    "event=create_dummy_device fail reason=vtable_access_failed code=0x%08X.",
                    static_cast<unsigned int>(outExceptionCode));
                return false;
            }

            if (*outVtable == nullptr || *outPresentTarget == nullptr || *outResetTarget == nullptr)
            {
                LogDx9(
                    "event=create_dummy_device fail reason=target_extract_failed vtable=%p presentTarget=%p resetTarget=%p.",
                    *outVtable,
                    *outPresentTarget,
                    *outResetTarget);
                return false;
            }

            LogDx9(
                "event=create_dummy_device result=ok vtable=%p presentTarget=%p resetTarget=%p.",
                *outVtable,
                *outPresentTarget,
                *outResetTarget);
            return true;
        }

        bool InstallPresentHookImpl(Dx9Runtime& rt)
        {
            std::lock_guard<std::mutex> lock(rt.mutex);
            LogDx9("event=install_hook_begin pid=%lu.", static_cast<unsigned long>(GetCurrentProcessId()));
            if (rt.installed.load(std::memory_order_acquire))
            {
                LogDx9("event=install_hook_result result=already_installed.");
                return true;
            }

            rt.qpcFreq = QueryQpcFreq();
            LogDx9("event=install_step step=qpc_freq value=%llu.", static_cast<unsigned long long>(rt.qpcFreq));
            rt.captureIntervalQpc = (rt.qpcFreq > 0) ? (rt.qpcFreq / kDefaultCaptureFps) : 0;
            LogDx9("event=install_step step=capture_interval_qpc value=%llu.", static_cast<unsigned long long>(rt.captureIntervalQpc));
            rt.configuredFpsLimit = kDefaultCaptureFps;
            rt.overlayEnabled = false;

            void** vtable = nullptr;
            void* presentTarget = nullptr;
            void* resetTarget = nullptr;
            std::uint32_t vtableExceptionCode = 0;
            LogDx9("event=install_step step=create_dummy_device begin.");
            if (!CreateDummyDeviceAndGetHookTargets(&presentTarget, &resetTarget, vtableExceptionCode, &vtable))
            {
                if (vtableExceptionCode != 0)
                {
                    LogDx9(
                        "event=install_hook_result result=fail reason=vtable_access_failed code=0x%08X.",
                        static_cast<unsigned int>(vtableExceptionCode));
                }
                else
                {
                    LogDx9("event=install_hook_result result=fail reason=create_dummy_device_failed.");
                }
                return false;
            }
            LogDx9("event=install_step step=create_dummy_device ok vtable=%p.", vtable);

            rt.presentTarget = presentTarget;
            rt.resetTarget = resetTarget;
            LogDx9(
                "event=install_step step=resolve_targets presentTarget=%p resetTarget=%p presentIndex=%d resetIndex=%d.",
                rt.presentTarget,
                rt.resetTarget,
                kDevicePresentIndex,
                kDeviceResetIndex);
            if (rt.resetTarget == nullptr || rt.presentTarget == nullptr)
            {
                LogDx9("event=install_hook_result result=fail reason=vtable_entry_missing_or_null.");
                return false;
            }

            if (!ValidateHookTargetPointer(rt.presentTarget, "presentTarget"))
            {
                LogDx9("event=install_hook_result result=fail reason=invalid_present_target_page.");
                return false;
            }

            if (!ValidateHookTargetPointer(rt.resetTarget, "resetTarget"))
            {
                LogDx9("event=install_hook_result result=fail reason=invalid_reset_target_page.");
                return false;
            }

            LogDx9("event=install_step step=mh_initialize begin.");
            const MH_STATUS initStatus = MH_Initialize();
            LogDx9("event=install_step step=mh_initialize status=%d.", static_cast<int>(initStatus));
            if (initStatus != MH_OK && initStatus != MH_ERROR_ALREADY_INITIALIZED)
            {
                LogDx9("event=install_hook_result result=fail reason=mh_initialize_failed status=%d.", static_cast<int>(initStatus));
                return false;
            }

            LogDx9("event=install_step step=mh_create_present begin target=%p hook=%p.", rt.presentTarget, reinterpret_cast<void*>(&HookedPresent));
            if (MH_CreateHook(
                    rt.presentTarget,
                    reinterpret_cast<LPVOID>(&HookedPresent),
                    reinterpret_cast<LPVOID*>(&rt.originalPresent)) != MH_OK)
            {
                LogDx9("event=install_hook_result result=fail reason=mh_create_present_failed.");
                return false;
            }
            LogDx9("event=install_step step=mh_create_present ok original=%p.", reinterpret_cast<void*>(rt.originalPresent));

            LogDx9("event=install_step step=mh_create_reset begin target=%p hook=%p.", rt.resetTarget, reinterpret_cast<void*>(&HookedReset));
            if (MH_CreateHook(
                    rt.resetTarget,
                    reinterpret_cast<LPVOID>(&HookedReset),
                    reinterpret_cast<LPVOID*>(&rt.originalReset)) != MH_OK)
            {
                LogDx9("event=install_hook_result result=fail reason=mh_create_reset_failed.");
                (void)MH_RemoveHook(rt.presentTarget);
                return false;
            }
            LogDx9("event=install_step step=mh_create_reset ok original=%p.", reinterpret_cast<void*>(rt.originalReset));

            LogDx9("event=install_step step=mh_enable_present begin target=%p.", rt.presentTarget);
            if (MH_EnableHook(rt.presentTarget) != MH_OK)
            {
                LogDx9("event=install_hook_result result=fail reason=mh_enable_present_failed.");
                (void)MH_RemoveHook(rt.resetTarget);
                (void)MH_RemoveHook(rt.presentTarget);
                return false;
            }
            LogDx9("event=install_step step=mh_enable_present ok.");

            LogDx9("event=install_step step=mh_enable_reset begin target=%p.", rt.resetTarget);
            if (MH_EnableHook(rt.resetTarget) != MH_OK)
            {
                LogDx9("event=install_hook_result result=fail reason=mh_enable_reset_failed.");
                (void)MH_DisableHook(rt.presentTarget);
                (void)MH_RemoveHook(rt.resetTarget);
                (void)MH_RemoveHook(rt.presentTarget);
                return false;
            }
            LogDx9("event=install_step step=mh_enable_reset ok.");

            rt.installed.store(true, std::memory_order_release);
            LogDx9("event=install_hook_result result=ok qpcFreq=%llu.", static_cast<unsigned long long>(rt.qpcFreq));
            return true;
        }
    }

    bool InstallPresentHook()
    {
        auto& rt = g_rt;
        __try
        {
            return InstallPresentHookImpl(rt);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            const auto ex = static_cast<std::uint32_t>(GetExceptionCode());
            LogDx9("event=install_exception code=0x%08X.", static_cast<unsigned int>(ex));
            return false;
        }
    }

    void UninstallPresentHook()
    {
        auto& rt = g_rt;
        std::lock_guard<std::mutex> lock(rt.mutex);
        if (!rt.installed.load(std::memory_order_acquire))
        {
            CloseDiagFile();
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
        LogDx9("event=uninstall_hook result=ok.");
        CloseDiagFile();
    }
}

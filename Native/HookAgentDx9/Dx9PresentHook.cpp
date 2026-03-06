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
        using Direct3DCreate9Fn = IDirect3D9* (WINAPI*)(UINT);
        using Direct3DCreate9ExFn = HRESULT(WINAPI*)(UINT, IDirect3D9Ex**);
        using CreateDeviceFn = HRESULT(STDMETHODCALLTYPE*)(
            IDirect3D9*,
            UINT,
            D3DDEVTYPE,
            HWND,
            DWORD,
            D3DPRESENT_PARAMETERS*,
            IDirect3DDevice9**);
        using CreateDeviceExFn = HRESULT(STDMETHODCALLTYPE*)(
            IDirect3D9Ex*,
            UINT,
            D3DDEVTYPE,
            HWND,
            DWORD,
            D3DPRESENT_PARAMETERS*,
            D3DDISPLAYMODEEX*,
            IDirect3DDevice9Ex**);
        using SwapChainPresentFn = HRESULT(STDMETHODCALLTYPE*)(
            IDirect3DSwapChain9*,
            const RECT*,
            const RECT*,
            HWND,
            const RGNDATA*,
            DWORD);
        using PresentExFn = HRESULT(STDMETHODCALLTYPE*)(
            IDirect3DDevice9Ex*,
            const RECT*,
            const RECT*,
            HWND,
            const RGNDATA*,
            DWORD);
        using ResetExFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9Ex*, D3DPRESENT_PARAMETERS*, D3DDISPLAYMODEEX*);

        constexpr int kDeviceResetIndex = 16;
        constexpr int kDevicePresentIndex = 17;
        constexpr int kSwapChainPresentIndex = 3;
        constexpr int kD3D9CreateDeviceIndex = 16;
        constexpr int kD3D9ExCreateDeviceExIndex = 20;
        constexpr int kDevicePresentExIndex = 121;
        constexpr int kDeviceResetExIndex = 132;
        constexpr std::uint32_t kDefaultCaptureFps = 15u;

        // WHY: Some titles can re-enter Present on the same thread. Capture only on outer-most call.
        static thread_local int g_presentDepth = 0;

        struct Dx9Runtime
        {
            std::mutex mutex;
            std::atomic_bool installed{false};

            void* presentTarget = nullptr;
            void* resetTarget = nullptr;
            void* swapChainPresentTarget = nullptr;
            void* presentExTarget = nullptr;
            void* resetExTarget = nullptr;
            void* createDeviceTarget = nullptr;
            void* createDeviceExTarget = nullptr;
            PresentFn originalPresent = nullptr;
            ResetFn originalReset = nullptr;
            SwapChainPresentFn originalSwapChainPresent = nullptr;
            PresentExFn originalPresentEx = nullptr;
            ResetExFn originalResetEx = nullptr;
            CreateDeviceFn originalCreateDevice = nullptr;
            CreateDeviceExFn originalCreateDeviceEx = nullptr;
            Direct3DCreate9Fn originalDirect3DCreate9 = nullptr;
            Direct3DCreate9ExFn originalDirect3DCreate9Ex = nullptr;
            void* direct3DCreate9ApiTarget = nullptr;
            void* direct3DCreate9ExApiTarget = nullptr;

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
            bool perfDiagLogEnabled = false;

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
            bool presentHookSeen = false;
            bool swapChainPresentHookSeen = false;
            bool presentExHookSeen = false;
            bool direct3dCreate9Seen = false;
            bool direct3dCreate9ExSeen = false;
            bool createDeviceHookSeen = false;
            bool createDeviceExHookSeen = false;
            std::uint64_t lastSkipCapturePresentCount = 0;
            ipc::SharedFrameWriter::LastErrorKind lastWriteFrameErrorKind = ipc::SharedFrameWriter::LastErrorKind::None;
            DWORD lastWriteFrameErrorGle = 0;
            std::uint64_t writeFrameErrorStreak = 0;
        };

        Dx9Runtime g_rt;
        std::mutex g_diagFileMutex;
        HANDLE g_diagFileHandle = INVALID_HANDLE_VALUE;
        DWORD g_diagFilePid = 0;
        std::wstring g_diagFilePath;
        std::atomic_bool g_perfDiagLogEnabled{false};
        std::atomic_bool g_diagFileSinkEnabled{false};

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
            if (g_diagFileSinkEnabled.load(std::memory_order_relaxed))
            {
                AppendDiagFileLine(line);
            }
        }

        void LogDx9Perf(const char* format, ...)
        {
            if (!g_perfDiagLogEnabled.load(std::memory_order_relaxed))
            {
                return;
            }

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
            if (g_diagFileSinkEnabled.load(std::memory_order_relaxed))
            {
                AppendDiagFileLine(line);
            }
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

        const char* FrameWriterErrorToString(ipc::SharedFrameWriter::LastErrorKind kind)
        {
            switch (kind)
            {
            case ipc::SharedFrameWriter::LastErrorKind::None:
                return "none";
            case ipc::SharedFrameWriter::LastErrorKind::InvalidArguments:
                return "invalid_arguments";
            case ipc::SharedFrameWriter::LastErrorKind::EnsureCapacityFailed:
                return "ensure_capacity_failed";
            case ipc::SharedFrameWriter::LastErrorKind::MappingSizeInvalid:
                return "mapping_size_invalid";
            case ipc::SharedFrameWriter::LastErrorKind::CreateFileMappingFailed:
                return "create_file_mapping_failed";
            case ipc::SharedFrameWriter::LastErrorKind::MapViewFailed:
                return "map_view_failed";
            default:
                return "unknown";
            }
        }

        void* TryReadVtableEntry(void* objectPtr, int index, std::uint32_t& exCode)
        {
            exCode = 0;
            void* target = nullptr;
            __try
            {
                if (objectPtr != nullptr)
                {
                    void** vtable = *reinterpret_cast<void***>(objectPtr);
                    if (vtable != nullptr && index >= 0)
                    {
                        target = vtable[index];
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                exCode = static_cast<std::uint32_t>(GetExceptionCode());
                target = nullptr;
            }
            return target;
        }

        void ResetRuntimeStateLocked(Dx9Runtime& rt)
        {
            rt.frameWriter.Reset();
            rt.configReader.Reset();
            rt.statusWriter.Reset();
            rt.scratch.clear();

            rt.presentTarget = nullptr;
            rt.resetTarget = nullptr;
            rt.swapChainPresentTarget = nullptr;
            rt.presentExTarget = nullptr;
            rt.resetExTarget = nullptr;
            rt.createDeviceTarget = nullptr;
            rt.createDeviceExTarget = nullptr;
            rt.originalPresent = nullptr;
            rt.originalReset = nullptr;
            rt.originalSwapChainPresent = nullptr;
            rt.originalPresentEx = nullptr;
            rt.originalResetEx = nullptr;
            rt.originalCreateDevice = nullptr;
            rt.originalCreateDeviceEx = nullptr;
            rt.originalDirect3DCreate9 = nullptr;
            rt.originalDirect3DCreate9Ex = nullptr;
            rt.direct3DCreate9ApiTarget = nullptr;
            rt.direct3DCreate9ExApiTarget = nullptr;

            rt.frameId = 0;
            rt.captureIntervalQpc = (rt.qpcFreq > 0) ? (rt.qpcFreq / kDefaultCaptureFps) : 0;
            rt.lastCaptureQpc = 0;
            rt.lastConfigQpc = 0;
            rt.lastFrameWriteQpc = 0;
            rt.configuredFpsLimit = kDefaultCaptureFps;
            rt.overlayEnabled = false;
            rt.perfDiagLogEnabled = false;
            g_perfDiagLogEnabled.store(false, std::memory_order_relaxed);
            const bool wasDiagFileSinkEnabled = g_diagFileSinkEnabled.exchange(false, std::memory_order_relaxed);
            if (wasDiagFileSinkEnabled)
            {
                CloseDiagFile();
            }

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
            rt.presentHookSeen = false;
            rt.swapChainPresentHookSeen = false;
            rt.presentExHookSeen = false;
            rt.direct3dCreate9Seen = false;
            rt.direct3dCreate9ExSeen = false;
            rt.createDeviceHookSeen = false;
            rt.createDeviceExHookSeen = false;
            rt.lastSkipCapturePresentCount = 0;
            rt.lastWriteFrameErrorKind = ipc::SharedFrameWriter::LastErrorKind::None;
            rt.lastWriteFrameErrorGle = 0;
            rt.writeFrameErrorStreak = 0;
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
            rt.perfDiagLogEnabled = (cfg.reserved0 & ipc::kConfigFlagEnablePerfDiagLog) != 0;
            g_perfDiagLogEnabled.store(rt.perfDiagLogEnabled, std::memory_order_relaxed);
            const bool diagFileSinkEnabled = (cfg.reserved0 & ipc::kConfigFlagEnableDiagFileSink) != 0;
            const bool wasDiagFileSinkEnabled = g_diagFileSinkEnabled.exchange(diagFileSinkEnabled, std::memory_order_relaxed);
            if (wasDiagFileSinkEnabled && !diagFileSinkEnabled)
            {
                CloseDiagFile();
            }
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

            LogDx9Perf(
                "event=capture_begin presentCount=%llu presentKind=%u.",
                static_cast<unsigned long long>(rt.presentCount),
                rt.lastPresentKind);

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

            LogDx9Perf(
                "event=capture_backbuffer_desc width=%u height=%u format=%u msaaType=%u msaaQuality=%u.",
                static_cast<unsigned int>(desc.Width),
                static_cast<unsigned int>(desc.Height),
                static_cast<unsigned int>(desc.Format),
                static_cast<unsigned int>(desc.MultiSampleType),
                static_cast<unsigned int>(desc.MultiSampleQuality));

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
            const auto mapName = rt.frameWriter.MappingName();
            const auto mapNameUtf8 = WideToUtf8(mapName);
            LogDx9Perf(
                "event=write_frame_begin frameId=%llu width=%u height=%u stride=%u payloadBytes=%llu map=\"%s\".",
                static_cast<unsigned long long>(frameId),
                width,
                height,
                stride,
                static_cast<unsigned long long>(payloadBytes),
                mapNameUtf8.c_str());
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
                const auto errorKind = rt.frameWriter.LastError();
                const auto errorName = FrameWriterErrorToString(errorKind);
                const auto writerGle = rt.frameWriter.LastWin32Error();
                const auto requestedBytes = rt.frameWriter.LastRequestedPayloadBytes();
                const auto totalBytes = rt.frameWriter.LastTotalBytes();
                const auto mapNameFail = rt.frameWriter.MappingName();
                const auto mapNameFailUtf8 = WideToUtf8(mapNameFail);
                const bool sameFailureAsLast =
                    (rt.lastWriteFrameErrorKind == errorKind) &&
                    (rt.lastWriteFrameErrorGle == writerGle);
                if (sameFailureAsLast)
                {
                    rt.writeFrameErrorStreak++;
                }
                else
                {
                    rt.writeFrameErrorStreak = 1;
                    rt.lastWriteFrameErrorKind = errorKind;
                    rt.lastWriteFrameErrorGle = writerGle;
                }

                const bool shouldLogFailure =
                    (rt.writeFrameErrorStreak == 1) ||
                    ((rt.writeFrameErrorStreak % 60ull) == 0);
                if (shouldLogFailure)
                {
                    LogDx9(
                        "event=write_frame_failed frameId=%llu reason=%s gle=%lu requestedPayloadBytes=%llu totalBytes=%llu map=\"%s\" streak=%llu.",
                        static_cast<unsigned long long>(frameId),
                        errorName,
                        static_cast<unsigned long>(writerGle),
                        static_cast<unsigned long long>(requestedBytes),
                        static_cast<unsigned long long>(totalBytes),
                        mapNameFailUtf8.c_str(),
                        static_cast<unsigned long long>(rt.writeFrameErrorStreak));
                }
                RecordCaptureFailureLocked(rt, "shared_frame_write_failed", E_FAIL);
                return false;
            }

            const auto mapNameOk = rt.frameWriter.MappingName();
            const auto mapNameOkUtf8 = WideToUtf8(mapNameOk);
            if (rt.writeFrameErrorStreak > 0)
            {
                LogDx9Perf(
                    "event=write_frame_recovered frameId=%llu previousReason=%s previousGle=%lu previousStreak=%llu map=\"%s\".",
                    static_cast<unsigned long long>(frameId),
                    FrameWriterErrorToString(rt.lastWriteFrameErrorKind),
                    static_cast<unsigned long>(rt.lastWriteFrameErrorGle),
                    static_cast<unsigned long long>(rt.writeFrameErrorStreak),
                    mapNameOkUtf8.c_str());
                rt.writeFrameErrorStreak = 0;
                rt.lastWriteFrameErrorKind = ipc::SharedFrameWriter::LastErrorKind::None;
                rt.lastWriteFrameErrorGle = 0;
            }
            LogDx9Perf(
                "event=write_frame_ok frameId=%llu width=%u height=%u payloadBytes=%llu map=\"%s\".",
                static_cast<unsigned long long>(frameId),
                width,
                height,
                static_cast<unsigned long long>(payloadBytes),
                mapNameOkUtf8.c_str());

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

        void LogRuntimeTargetsComparisonLocked(Dx9Runtime& rt, IDirect3DDevice9* device, bool fromEx)
        {
            if (device == nullptr)
            {
                return;
            }

            void* runtimePresent = nullptr;
            void* runtimeReset = nullptr;
            void* runtimePresentEx = nullptr;
            void* runtimeResetEx = nullptr;
            void* runtimeSwapChainPresent = nullptr;
            HRESULT swapChainHr = E_FAIL;
            std::uint32_t exCode = 0;

            __try
            {
                void** deviceVtable = *reinterpret_cast<void***>(device);
                if (deviceVtable != nullptr)
                {
                    runtimeReset = deviceVtable[kDeviceResetIndex];
                    runtimePresent = deviceVtable[kDevicePresentIndex];
                    if (fromEx)
                    {
                        runtimePresentEx = deviceVtable[kDevicePresentExIndex];
                        runtimeResetEx = deviceVtable[kDeviceResetExIndex];
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                exCode = static_cast<std::uint32_t>(GetExceptionCode());
            }

            IDirect3DSwapChain9* swapChain = nullptr;
            swapChainHr = device->GetSwapChain(0, &swapChain);
            if (SUCCEEDED(swapChainHr) && swapChain != nullptr)
            {
                __try
                {
                    void** swapVtable = *reinterpret_cast<void***>(swapChain);
                    if (swapVtable != nullptr)
                    {
                        runtimeSwapChainPresent = swapVtable[kSwapChainPresentIndex];
                    }
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                    exCode = static_cast<std::uint32_t>(GetExceptionCode());
                }
                swapChain->Release();
            }

            if (exCode != 0)
            {
                LogDx9Perf("event=runtime_vtable_probe_failed code=0x%08X fromEx=%u.", static_cast<unsigned int>(exCode), fromEx ? 1u : 0u);
                return;
            }

            if (FAILED(swapChainHr))
            {
                LogDx9Perf(
                    "event=runtime_swapchain_probe_failed hr=0x%08X fromEx=%u.",
                    static_cast<unsigned int>(static_cast<std::uint32_t>(swapChainHr)),
                    fromEx ? 1u : 0u);
            }

            LogDx9Perf(
                "event=runtime_targets fromEx=%u present=%p reset=%p swapchainPresent=%p presentEx=%p resetEx=%p.",
                fromEx ? 1u : 0u,
                runtimePresent,
                runtimeReset,
                runtimeSwapChainPresent,
                runtimePresentEx,
                runtimeResetEx);

            const std::uint32_t matchPresent = (runtimePresent == rt.presentTarget) ? 1u : 0u;
            const std::uint32_t matchReset = (runtimeReset == rt.resetTarget) ? 1u : 0u;
            const std::uint32_t matchSwap = (runtimeSwapChainPresent == rt.swapChainPresentTarget) ? 1u : 0u;
            const std::uint32_t matchPresentEx = (rt.presentExTarget == nullptr) ? 2u : ((runtimePresentEx == rt.presentExTarget) ? 1u : 0u);
            const std::uint32_t matchResetEx = (rt.resetExTarget == nullptr) ? 2u : ((runtimeResetEx == rt.resetExTarget) ? 1u : 0u);

            LogDx9Perf(
                "event=runtime_target_compare fromEx=%u matchPresent=%u matchReset=%u matchSwapchainPresent=%u matchPresentEx=%u matchResetEx=%u dummyPresent=%p dummyReset=%p dummySwapchainPresent=%p dummyPresentEx=%p dummyResetEx=%p.",
                fromEx ? 1u : 0u,
                matchPresent,
                matchReset,
                matchSwap,
                matchPresentEx,
                matchResetEx,
                rt.presentTarget,
                rt.resetTarget,
                rt.swapChainPresentTarget,
                rt.presentExTarget,
                rt.resetExTarget);
        }

        HRESULT STDMETHODCALLTYPE HookedCreateDevice(
            IDirect3D9* d3d9,
            UINT adapter,
            D3DDEVTYPE deviceType,
            HWND focusWindow,
            DWORD behaviorFlags,
            D3DPRESENT_PARAMETERS* presentParameters,
            IDirect3DDevice9** returnedDevice)
        {
            CreateDeviceFn original = g_rt.originalCreateDevice;
            const HRESULT hr = original
                ? original(d3d9, adapter, deviceType, focusWindow, behaviorFlags, presentParameters, returnedDevice)
                : D3DERR_INVALIDCALL;

            if (SUCCEEDED(hr) && returnedDevice != nullptr && *returnedDevice != nullptr)
            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                if (!g_rt.createDeviceHookSeen)
                {
                    g_rt.createDeviceHookSeen = true;
                    LogDx9Perf("event=runtime_device_created kind=device9 ptr=%p.", *returnedDevice);
                }
                LogRuntimeTargetsComparisonLocked(g_rt, *returnedDevice, false);
            }

            return hr;
        }

        HRESULT STDMETHODCALLTYPE HookedCreateDeviceEx(
            IDirect3D9Ex* d3d9,
            UINT adapter,
            D3DDEVTYPE deviceType,
            HWND focusWindow,
            DWORD behaviorFlags,
            D3DPRESENT_PARAMETERS* presentParameters,
            D3DDISPLAYMODEEX* fullscreenDisplayMode,
            IDirect3DDevice9Ex** returnedDevice)
        {
            CreateDeviceExFn original = g_rt.originalCreateDeviceEx;
            const HRESULT hr = original
                ? original(d3d9, adapter, deviceType, focusWindow, behaviorFlags, presentParameters, fullscreenDisplayMode, returnedDevice)
                : D3DERR_INVALIDCALL;

            if (SUCCEEDED(hr) && returnedDevice != nullptr && *returnedDevice != nullptr)
            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                if (!g_rt.createDeviceExHookSeen)
                {
                    g_rt.createDeviceExHookSeen = true;
                    LogDx9Perf("event=runtime_device_created kind=device9ex ptr=%p.", *returnedDevice);
                }
                LogRuntimeTargetsComparisonLocked(g_rt, static_cast<IDirect3DDevice9*>(*returnedDevice), true);
            }

            return hr;
        }

        IDirect3D9* WINAPI HookedDirect3DCreate9(UINT sdkVersion)
        {
            Direct3DCreate9Fn original = g_rt.originalDirect3DCreate9;
            IDirect3D9* d3d9 = original ? original(sdkVersion) : nullptr;
            std::lock_guard<std::mutex> lock(g_rt.mutex);
            if (!g_rt.direct3dCreate9Seen)
            {
                g_rt.direct3dCreate9Seen = true;
                LogDx9Perf("event=runtime_create9_hit sdk=%u obj=%p.", static_cast<unsigned int>(sdkVersion), d3d9);
            }

            if (d3d9 != nullptr)
            {
                std::uint32_t exCode = 0;
                void* runtimeCreateDeviceTarget = TryReadVtableEntry(d3d9, kD3D9CreateDeviceIndex, exCode);

                if (exCode != 0)
                {
                    LogDx9Perf("event=runtime_create9_vtable_probe_failed code=0x%08X.", static_cast<unsigned int>(exCode));
                }
                else
                {
                    LogDx9Perf(
                        "event=runtime_create9_target target=%p dummyTarget=%p match=%u.",
                        runtimeCreateDeviceTarget,
                        g_rt.createDeviceTarget,
                        (runtimeCreateDeviceTarget == g_rt.createDeviceTarget) ? 1u : 0u);
                }
            }

            return d3d9;
        }

        HRESULT WINAPI HookedDirect3DCreate9Ex(UINT sdkVersion, IDirect3D9Ex** outD3D9Ex)
        {
            Direct3DCreate9ExFn original = g_rt.originalDirect3DCreate9Ex;
            const HRESULT hr = original ? original(sdkVersion, outD3D9Ex) : D3DERR_INVALIDCALL;
            IDirect3D9Ex* d3d9Ex = (outD3D9Ex != nullptr) ? *outD3D9Ex : nullptr;

            std::lock_guard<std::mutex> lock(g_rt.mutex);
            if (!g_rt.direct3dCreate9ExSeen)
            {
                g_rt.direct3dCreate9ExSeen = true;
                LogDx9Perf(
                    "event=runtime_create9ex_hit sdk=%u hr=0x%08X obj=%p.",
                    static_cast<unsigned int>(sdkVersion),
                    static_cast<unsigned int>(static_cast<std::uint32_t>(hr)),
                    d3d9Ex);
            }

            if (SUCCEEDED(hr) && d3d9Ex != nullptr)
            {
                std::uint32_t exCode = 0;
                void* runtimeCreateDeviceExTarget = TryReadVtableEntry(d3d9Ex, kD3D9ExCreateDeviceExIndex, exCode);

                if (exCode != 0)
                {
                    LogDx9Perf("event=runtime_create9ex_vtable_probe_failed code=0x%08X.", static_cast<unsigned int>(exCode));
                }
                else
                {
                    LogDx9Perf(
                        "event=runtime_create9ex_target target=%p dummyTarget=%p match=%u.",
                        runtimeCreateDeviceExTarget,
                        g_rt.createDeviceExTarget,
                        (runtimeCreateDeviceExTarget == g_rt.createDeviceExTarget) ? 1u : 0u);
                }
            }

            return hr;
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
                if (!g_rt.presentHookSeen)
                {
                    g_rt.presentHookSeen = true;
                    LogDx9Perf(
                        "event=present_hook_hit kind=present presentCount=%llu qpc=%llu.",
                        static_cast<unsigned long long>(g_rt.presentCount),
                        static_cast<unsigned long long>(g_rt.lastPresentQpc));
                }
                (void)RefreshConfigLocked(g_rt);

                const bool shouldCapture = ShouldCaptureNowLocked(g_rt, g_rt.lastPresentQpc);
                if (shouldCapture)
                {
                    (void)CaptureAndShareFrameLocked(g_rt, device);
                }
                else
                {
                    if (g_rt.lastSkipCapturePresentCount == 0 || (g_rt.presentCount - g_rt.lastSkipCapturePresentCount) >= 120)
                    {
                        g_rt.lastSkipCapturePresentCount = g_rt.presentCount;
                        const std::uint64_t elapsed = (g_rt.lastCaptureQpc > 0 && g_rt.lastPresentQpc >= g_rt.lastCaptureQpc)
                            ? (g_rt.lastPresentQpc - g_rt.lastCaptureQpc)
                            : 0;
                        LogDx9Perf(
                            "event=capture_skip reason=interval_gate kind=present presentCount=%llu elapsedQpc=%llu intervalQpc=%llu lastCaptureQpc=%llu.",
                            static_cast<unsigned long long>(g_rt.presentCount),
                            static_cast<unsigned long long>(elapsed),
                            static_cast<unsigned long long>(g_rt.captureIntervalQpc),
                            static_cast<unsigned long long>(g_rt.lastCaptureQpc));
                    }
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

        HRESULT STDMETHODCALLTYPE HookedSwapChainPresent(
            IDirect3DSwapChain9* swapChain,
            const RECT* sourceRect,
            const RECT* destRect,
            HWND destWindowOverride,
            const RGNDATA* dirtyRegion,
            DWORD flags)
        {
            if (g_presentDepth > 0)
            {
                return g_rt.originalSwapChainPresent
                    ? g_rt.originalSwapChainPresent(swapChain, sourceRect, destRect, destWindowOverride, dirtyRegion, flags)
                    : D3D_OK;
            }

            g_presentDepth++;
            SwapChainPresentFn original = nullptr;
            IDirect3DDevice9* device = nullptr;
            const HRESULT getDeviceHr = (swapChain != nullptr) ? swapChain->GetDevice(&device) : E_POINTER;
            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                g_rt.presentCount++;
                g_rt.lastPresentQpc = NowQpc();
                g_rt.lastPresentKind = 3;
                if (!g_rt.swapChainPresentHookSeen)
                {
                    g_rt.swapChainPresentHookSeen = true;
                    LogDx9Perf(
                        "event=present_hook_hit kind=swapchain_present presentCount=%llu qpc=%llu.",
                        static_cast<unsigned long long>(g_rt.presentCount),
                        static_cast<unsigned long long>(g_rt.lastPresentQpc));
                }
                (void)RefreshConfigLocked(g_rt);

                const bool shouldCapture = ShouldCaptureNowLocked(g_rt, g_rt.lastPresentQpc);
                if (shouldCapture)
                {
                    if (SUCCEEDED(getDeviceHr) && device != nullptr)
                    {
                        (void)CaptureAndShareFrameLocked(g_rt, device);
                    }
                    else
                    {
                        LogDx9(
                            "event=capture_skip reason=swapchain_get_device_failed hr=0x%08X presentCount=%llu.",
                            static_cast<unsigned int>(static_cast<std::uint32_t>(getDeviceHr)),
                            static_cast<unsigned long long>(g_rt.presentCount));
                    }
                }
                else
                {
                    if (g_rt.lastSkipCapturePresentCount == 0 || (g_rt.presentCount - g_rt.lastSkipCapturePresentCount) >= 120)
                    {
                        g_rt.lastSkipCapturePresentCount = g_rt.presentCount;
                        const std::uint64_t elapsed = (g_rt.lastCaptureQpc > 0 && g_rt.lastPresentQpc >= g_rt.lastCaptureQpc)
                            ? (g_rt.lastPresentQpc - g_rt.lastCaptureQpc)
                            : 0;
                        LogDx9Perf(
                            "event=capture_skip reason=interval_gate kind=swapchain_present presentCount=%llu elapsedQpc=%llu intervalQpc=%llu lastCaptureQpc=%llu.",
                            static_cast<unsigned long long>(g_rt.presentCount),
                            static_cast<unsigned long long>(elapsed),
                            static_cast<unsigned long long>(g_rt.captureIntervalQpc),
                            static_cast<unsigned long long>(g_rt.lastCaptureQpc));
                    }
                }

                PublishStatusLocked(g_rt);
                original = g_rt.originalSwapChainPresent;
            }

            const HRESULT result = original
                ? original(swapChain, sourceRect, destRect, destWindowOverride, dirtyRegion, flags)
                : D3D_OK;
            if (device != nullptr)
            {
                device->Release();
            }
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

        HRESULT STDMETHODCALLTYPE HookedPresentEx(
            IDirect3DDevice9Ex* device,
            const RECT* sourceRect,
            const RECT* destRect,
            HWND destWindowOverride,
            const RGNDATA* dirtyRegion,
            DWORD flags)
        {
            if (g_presentDepth > 0)
            {
                return g_rt.originalPresentEx
                    ? g_rt.originalPresentEx(device, sourceRect, destRect, destWindowOverride, dirtyRegion, flags)
                    : D3D_OK;
            }

            g_presentDepth++;
            PresentExFn original = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                g_rt.presentCount++;
                g_rt.lastPresentQpc = NowQpc();
                g_rt.lastPresentKind = 2;
                if (!g_rt.presentExHookSeen)
                {
                    g_rt.presentExHookSeen = true;
                    LogDx9Perf(
                        "event=present_hook_hit kind=present_ex presentCount=%llu qpc=%llu.",
                        static_cast<unsigned long long>(g_rt.presentCount),
                        static_cast<unsigned long long>(g_rt.lastPresentQpc));
                }
                (void)RefreshConfigLocked(g_rt);

                const bool shouldCapture = ShouldCaptureNowLocked(g_rt, g_rt.lastPresentQpc);
                if (shouldCapture)
                {
                    (void)CaptureAndShareFrameLocked(g_rt, static_cast<IDirect3DDevice9*>(device));
                }
                else
                {
                    if (g_rt.lastSkipCapturePresentCount == 0 || (g_rt.presentCount - g_rt.lastSkipCapturePresentCount) >= 120)
                    {
                        g_rt.lastSkipCapturePresentCount = g_rt.presentCount;
                        const std::uint64_t elapsed = (g_rt.lastCaptureQpc > 0 && g_rt.lastPresentQpc >= g_rt.lastCaptureQpc)
                            ? (g_rt.lastPresentQpc - g_rt.lastCaptureQpc)
                            : 0;
                        LogDx9Perf(
                            "event=capture_skip reason=interval_gate kind=present_ex presentCount=%llu elapsedQpc=%llu intervalQpc=%llu lastCaptureQpc=%llu.",
                            static_cast<unsigned long long>(g_rt.presentCount),
                            static_cast<unsigned long long>(elapsed),
                            static_cast<unsigned long long>(g_rt.captureIntervalQpc),
                            static_cast<unsigned long long>(g_rt.lastCaptureQpc));
                    }
                }

                PublishStatusLocked(g_rt);
                original = g_rt.originalPresentEx;
            }

            const HRESULT result = original
                ? original(device, sourceRect, destRect, destWindowOverride, dirtyRegion, flags)
                : D3D_OK;
            g_presentDepth--;
            return result;
        }

        HRESULT STDMETHODCALLTYPE HookedResetEx(IDirect3DDevice9Ex* device, D3DPRESENT_PARAMETERS* params, D3DDISPLAYMODEEX* mode)
        {
            ResetExFn original = nullptr;
            std::uint64_t presentCount = 0;
            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                ReleaseCaptureSurfacesLocked(g_rt, "reset_ex_begin");
                g_rt.backBufferWidth = 0;
                g_rt.backBufferHeight = 0;
                g_rt.backBufferFormat = 0;
                g_rt.lastCaptureQpc = 0;
                presentCount = g_rt.presentCount;
                original = g_rt.originalResetEx;
            }

            const auto result = original ? original(device, params, mode) : D3D_OK;

            {
                std::lock_guard<std::mutex> lock(g_rt.mutex);
                if (SUCCEEDED(result))
                {
                    g_rt.resetCount++;
                    g_rt.lastResetQpc = NowQpc();
                    g_rt.pendingPostResetRebind = true;
                    LogDx9(
                        "event=reset_ex_result result=ok hr=0x%08X resetCount=%llu presentCount=%llu.",
                        static_cast<unsigned int>(static_cast<std::uint32_t>(result)),
                        static_cast<unsigned long long>(g_rt.resetCount),
                        static_cast<unsigned long long>(presentCount));
                }
                else
                {
                    LogDx9(
                        "event=reset_ex_result result=failed hr=0x%08X resetCount=%llu presentCount=%llu.",
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
            void** outSwapChainPresentTarget,
            void** outCreateDeviceTarget,
            std::uint32_t& outExceptionCode,
            void*** outVtable)
        {
            outExceptionCode = 0;
            if (outPresentTarget == nullptr || outResetTarget == nullptr || outSwapChainPresentTarget == nullptr || outCreateDeviceTarget == nullptr || outVtable == nullptr)
            {
                LogDx9("event=create_dummy_device fail reason=out_target_null.");
                return false;
            }

            *outPresentTarget = nullptr;
            *outResetTarget = nullptr;
            *outSwapChainPresentTarget = nullptr;
            *outCreateDeviceTarget = nullptr;
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

            __try
            {
                void** d3d9Vtable = *reinterpret_cast<void***>(d3d9);
                if (d3d9Vtable != nullptr)
                {
                    *outCreateDeviceTarget = d3d9Vtable[kD3D9CreateDeviceIndex];
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                outExceptionCode = static_cast<std::uint32_t>(GetExceptionCode());
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

            IDirect3DSwapChain9* swapChain = nullptr;
            HRESULT swapChainHr = device->GetSwapChain(0, &swapChain);
            if (SUCCEEDED(swapChainHr) && swapChain != nullptr)
            {
                void** swapChainVtable = nullptr;
                __try
                {
                    swapChainVtable = *reinterpret_cast<void***>(swapChain);
                    if (swapChainVtable != nullptr)
                    {
                        *outSwapChainPresentTarget = swapChainVtable[kSwapChainPresentIndex];
                    }
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                    outExceptionCode = static_cast<std::uint32_t>(GetExceptionCode());
                }
                swapChain->Release();
            }
            else
            {
                LogDx9(
                    "event=create_dummy_device fail reason=get_swapchain_failed hr=0x%08X.",
                    static_cast<unsigned int>(static_cast<std::uint32_t>(swapChainHr)));
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

            if (*outVtable == nullptr || *outPresentTarget == nullptr || *outResetTarget == nullptr || *outSwapChainPresentTarget == nullptr || *outCreateDeviceTarget == nullptr)
            {
                LogDx9(
                    "event=create_dummy_device fail reason=target_extract_failed vtable=%p presentTarget=%p resetTarget=%p swapChainPresentTarget=%p createDeviceTarget=%p.",
                    *outVtable,
                    *outPresentTarget,
                    *outResetTarget,
                    *outSwapChainPresentTarget,
                    *outCreateDeviceTarget);
                return false;
            }

            LogDx9(
                "event=create_dummy_device result=ok vtable=%p presentTarget=%p resetTarget=%p swapChainPresentTarget=%p createDeviceTarget=%p.",
                *outVtable,
                *outPresentTarget,
                *outResetTarget,
                *outSwapChainPresentTarget,
                *outCreateDeviceTarget);
            return true;
        }

        bool CreateDummyDeviceExAndGetHookTargets(
            void** outPresentExTarget,
            void** outResetExTarget,
            void** outCreateDeviceExTarget,
            std::uint32_t& outExceptionCode,
            void*** outVtable)
        {
            outExceptionCode = 0;
            if (outPresentExTarget == nullptr || outResetExTarget == nullptr || outCreateDeviceExTarget == nullptr || outVtable == nullptr)
            {
                LogDx9("event=create_dummy_device_ex fail reason=out_target_null.");
                return false;
            }

            *outPresentExTarget = nullptr;
            *outResetExTarget = nullptr;
            *outCreateDeviceExTarget = nullptr;
            *outVtable = nullptr;

            WNDCLASSW wc{};
            wc.lpfnWndProc = DefWindowProcW;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.lpszClassName = L"HT_DX9EX_HookDummyWindow";
            (void)RegisterClassW(&wc);

            HWND hwnd = CreateWindowExW(
                0,
                wc.lpszClassName,
                L"HT_DX9EX_HookDummyWindow",
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
                LogDx9("event=create_dummy_device_ex fail reason=create_window_failed gle=%lu.", static_cast<unsigned long>(GetLastError()));
                return false;
            }

            IDirect3D9Ex* d3d9Ex = nullptr;
            HRESULT hr = Direct3DCreate9Ex(D3D_SDK_VERSION, &d3d9Ex);
            if (FAILED(hr) || d3d9Ex == nullptr)
            {
                LogDx9(
                    "event=create_dummy_device_ex fail reason=direct3d_create9ex_failed hr=0x%08X.",
                    static_cast<unsigned int>(static_cast<std::uint32_t>(hr)));
                DestroyWindow(hwnd);
                return false;
            }

            __try
            {
                void** d3d9ExVtable = *reinterpret_cast<void***>(d3d9Ex);
                if (d3d9ExVtable != nullptr)
                {
                    *outCreateDeviceExTarget = d3d9ExVtable[kD3D9ExCreateDeviceExIndex];
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                outExceptionCode = static_cast<std::uint32_t>(GetExceptionCode());
            }

            D3DPRESENT_PARAMETERS pp{};
            pp.Windowed = TRUE;
            pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
            pp.hDeviceWindow = hwnd;
            pp.BackBufferFormat = D3DFMT_A8R8G8B8;
            pp.BackBufferWidth = 2;
            pp.BackBufferHeight = 2;
            pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;

            IDirect3DDevice9Ex* deviceEx = nullptr;
            hr = d3d9Ex->CreateDeviceEx(
                D3DADAPTER_DEFAULT,
                D3DDEVTYPE_HAL,
                hwnd,
                D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE,
                &pp,
                nullptr,
                &deviceEx);
            if (FAILED(hr))
            {
                hr = d3d9Ex->CreateDeviceEx(
                    D3DADAPTER_DEFAULT,
                    D3DDEVTYPE_REF,
                    hwnd,
                    D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_FPU_PRESERVE,
                    &pp,
                    nullptr,
                    &deviceEx);
            }

            if (FAILED(hr) || deviceEx == nullptr)
            {
                LogDx9(
                    "event=create_dummy_device_ex fail reason=create_deviceex_failed hr=0x%08X.",
                    static_cast<unsigned int>(static_cast<std::uint32_t>(hr)));
                d3d9Ex->Release();
                DestroyWindow(hwnd);
                return false;
            }

            void** vtable = nullptr;
            __try
            {
                vtable = *reinterpret_cast<void***>(deviceEx);
                if (vtable != nullptr)
                {
                    *outPresentExTarget = vtable[kDevicePresentExIndex];
                    *outResetExTarget = vtable[kDeviceResetExIndex];
                    *outVtable = vtable;
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                outExceptionCode = static_cast<std::uint32_t>(GetExceptionCode());
            }

            deviceEx->Release();
            d3d9Ex->Release();
            DestroyWindow(hwnd);

            if (outExceptionCode != 0)
            {
                LogDx9(
                    "event=create_dummy_device_ex fail reason=vtable_access_failed code=0x%08X.",
                    static_cast<unsigned int>(outExceptionCode));
                return false;
            }

            if (*outVtable == nullptr || *outPresentExTarget == nullptr || *outResetExTarget == nullptr || *outCreateDeviceExTarget == nullptr)
            {
                LogDx9(
                    "event=create_dummy_device_ex fail reason=target_extract_failed vtable=%p presentExTarget=%p resetExTarget=%p createDeviceExTarget=%p.",
                    *outVtable,
                    *outPresentExTarget,
                    *outResetExTarget,
                    *outCreateDeviceExTarget);
                return false;
            }

            LogDx9(
                "event=create_dummy_device_ex result=ok vtable=%p presentExTarget=%p resetExTarget=%p createDeviceExTarget=%p.",
                *outVtable,
                *outPresentExTarget,
                *outResetExTarget,
                *outCreateDeviceExTarget);
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

            void* presentTarget = nullptr;
            void* resetTarget = nullptr;
            void* swapChainPresentTarget = nullptr;
            void* presentExTarget = nullptr;
            void* resetExTarget = nullptr;
            void* createDeviceTarget = nullptr;
            void* createDeviceExTarget = nullptr;
            void** vtable = nullptr;
            void** vtableEx = nullptr;
            std::uint32_t vtableExceptionCode = 0;
            std::uint32_t vtableExExceptionCode = 0;
            bool hasDx9ExTargets = false;
            LogDx9("event=install_step step=create_dummy_device begin.");
            if (!CreateDummyDeviceAndGetHookTargets(&presentTarget, &resetTarget, &swapChainPresentTarget, &createDeviceTarget, vtableExceptionCode, &vtable))
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

            LogDx9("event=install_step step=create_dummy_device_ex begin.");
            if (CreateDummyDeviceExAndGetHookTargets(&presentExTarget, &resetExTarget, &createDeviceExTarget, vtableExExceptionCode, &vtableEx))
            {
                hasDx9ExTargets = true;
                LogDx9("event=install_step step=create_dummy_device_ex ok vtable=%p.", vtableEx);
            }
            else
            {
                const char* reason = (vtableExExceptionCode != 0) ? "vtable_access_failed" : "create_dummy_device_ex_failed";
                LogDx9(
                    "event=install_step step=create_dummy_device_ex skip reason=%s code=0x%08X.",
                    reason,
                    static_cast<unsigned int>(vtableExExceptionCode));
            }

            rt.presentTarget = presentTarget;
            rt.resetTarget = resetTarget;
            rt.swapChainPresentTarget = swapChainPresentTarget;
            rt.presentExTarget = hasDx9ExTargets ? presentExTarget : nullptr;
            rt.resetExTarget = hasDx9ExTargets ? resetExTarget : nullptr;
            rt.createDeviceTarget = createDeviceTarget;
            rt.createDeviceExTarget = hasDx9ExTargets ? createDeviceExTarget : nullptr;
            LogDx9(
                "event=install_step step=resolve_targets presentTarget=%p resetTarget=%p swapChainPresentTarget=%p presentExTarget=%p resetExTarget=%p createDeviceTarget=%p createDeviceExTarget=%p presentIndex=%d resetIndex=%d swapChainPresentIndex=%d presentExIndex=%d resetExIndex=%d createDeviceIndex=%d createDeviceExIndex=%d.",
                rt.presentTarget,
                rt.resetTarget,
                rt.swapChainPresentTarget,
                rt.presentExTarget,
                rt.resetExTarget,
                rt.createDeviceTarget,
                rt.createDeviceExTarget,
                kDevicePresentIndex,
                kDeviceResetIndex,
                kSwapChainPresentIndex,
                kDevicePresentExIndex,
                kDeviceResetExIndex,
                kD3D9CreateDeviceIndex,
                kD3D9ExCreateDeviceExIndex);
            if (rt.resetTarget == nullptr || rt.presentTarget == nullptr || rt.swapChainPresentTarget == nullptr || rt.createDeviceTarget == nullptr)
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

            if (!ValidateHookTargetPointer(rt.swapChainPresentTarget, "swapChainPresentTarget"))
            {
                LogDx9("event=install_hook_result result=fail reason=invalid_swapchain_present_target_page.");
                return false;
            }

            if (!ValidateHookTargetPointer(rt.createDeviceTarget, "createDeviceTarget"))
            {
                LogDx9("event=install_hook_result result=fail reason=invalid_create_device_target_page.");
                return false;
            }

            if (rt.presentExTarget != nullptr && !ValidateHookTargetPointer(rt.presentExTarget, "presentExTarget"))
            {
                LogDx9("event=install_hook_result result=fail reason=invalid_present_ex_target_page.");
                return false;
            }

            if (rt.resetExTarget != nullptr && !ValidateHookTargetPointer(rt.resetExTarget, "resetExTarget"))
            {
                LogDx9("event=install_hook_result result=fail reason=invalid_reset_ex_target_page.");
                return false;
            }

            if (rt.createDeviceExTarget != nullptr && !ValidateHookTargetPointer(rt.createDeviceExTarget, "createDeviceExTarget"))
            {
                LogDx9("event=install_hook_result result=fail reason=invalid_create_device_ex_target_page.");
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
            bool createDeviceHookCreated = false;
            bool createDeviceExHookCreated = false;
            bool create9ApiHookCreated = false;
            bool create9ExApiHookCreated = false;

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

            LogDx9("event=install_step step=mh_create_swapchain_present begin target=%p hook=%p.", rt.swapChainPresentTarget, reinterpret_cast<void*>(&HookedSwapChainPresent));
            if (MH_CreateHook(
                    rt.swapChainPresentTarget,
                    reinterpret_cast<LPVOID>(&HookedSwapChainPresent),
                    reinterpret_cast<LPVOID*>(&rt.originalSwapChainPresent)) != MH_OK)
            {
                LogDx9("event=install_hook_result result=fail reason=mh_create_swapchain_present_failed.");
                (void)MH_RemoveHook(rt.resetTarget);
                (void)MH_RemoveHook(rt.presentTarget);
                return false;
            }
            LogDx9("event=install_step step=mh_create_swapchain_present ok original=%p.", reinterpret_cast<void*>(rt.originalSwapChainPresent));

            if (rt.presentExTarget != nullptr)
            {
                LogDx9("event=install_step step=mh_create_present_ex begin target=%p hook=%p.", rt.presentExTarget, reinterpret_cast<void*>(&HookedPresentEx));
                if (MH_CreateHook(
                        rt.presentExTarget,
                        reinterpret_cast<LPVOID>(&HookedPresentEx),
                        reinterpret_cast<LPVOID*>(&rt.originalPresentEx)) != MH_OK)
                {
                    LogDx9("event=install_hook_result result=fail reason=mh_create_present_ex_failed.");
                    (void)MH_RemoveHook(rt.swapChainPresentTarget);
                    (void)MH_RemoveHook(rt.resetTarget);
                    (void)MH_RemoveHook(rt.presentTarget);
                    return false;
                }
                LogDx9("event=install_step step=mh_create_present_ex ok original=%p.", reinterpret_cast<void*>(rt.originalPresentEx));
            }

            if (rt.resetExTarget != nullptr)
            {
                LogDx9("event=install_step step=mh_create_reset_ex begin target=%p hook=%p.", rt.resetExTarget, reinterpret_cast<void*>(&HookedResetEx));
                if (MH_CreateHook(
                        rt.resetExTarget,
                        reinterpret_cast<LPVOID>(&HookedResetEx),
                        reinterpret_cast<LPVOID*>(&rt.originalResetEx)) != MH_OK)
                {
                    LogDx9("event=install_hook_result result=fail reason=mh_create_reset_ex_failed.");
                    if (rt.presentExTarget != nullptr)
                    {
                        (void)MH_RemoveHook(rt.presentExTarget);
                    }
                    (void)MH_RemoveHook(rt.swapChainPresentTarget);
                    (void)MH_RemoveHook(rt.resetTarget);
                    (void)MH_RemoveHook(rt.presentTarget);
                    return false;
                }
                LogDx9("event=install_step step=mh_create_reset_ex ok original=%p.", reinterpret_cast<void*>(rt.originalResetEx));
            }

            LogDx9("event=install_step step=mh_create_create_device begin target=%p hook=%p.", rt.createDeviceTarget, reinterpret_cast<void*>(&HookedCreateDevice));
            if (MH_CreateHook(
                    rt.createDeviceTarget,
                    reinterpret_cast<LPVOID>(&HookedCreateDevice),
                    reinterpret_cast<LPVOID*>(&rt.originalCreateDevice)) == MH_OK)
            {
                createDeviceHookCreated = true;
                LogDx9("event=install_step step=mh_create_create_device ok original=%p.", reinterpret_cast<void*>(rt.originalCreateDevice));
            }
            else
            {
                LogDx9("event=install_step step=mh_create_create_device skip reason=create_failed.");
            }

            if (rt.createDeviceExTarget != nullptr)
            {
                LogDx9("event=install_step step=mh_create_create_device_ex begin target=%p hook=%p.", rt.createDeviceExTarget, reinterpret_cast<void*>(&HookedCreateDeviceEx));
                if (MH_CreateHook(
                        rt.createDeviceExTarget,
                        reinterpret_cast<LPVOID>(&HookedCreateDeviceEx),
                        reinterpret_cast<LPVOID*>(&rt.originalCreateDeviceEx)) == MH_OK)
                {
                    createDeviceExHookCreated = true;
                    LogDx9("event=install_step step=mh_create_create_device_ex ok original=%p.", reinterpret_cast<void*>(rt.originalCreateDeviceEx));
                }
                else
                {
                    LogDx9("event=install_step step=mh_create_create_device_ex skip reason=create_failed.");
                }
            }

            LogDx9("event=install_step step=mh_create_api_direct3dcreate9 begin.");
            if (MH_CreateHookApiEx(
                    L"d3d9.dll",
                    "Direct3DCreate9",
                    reinterpret_cast<LPVOID>(&HookedDirect3DCreate9),
                    reinterpret_cast<LPVOID*>(&rt.originalDirect3DCreate9),
                    reinterpret_cast<LPVOID*>(&rt.direct3DCreate9ApiTarget)) == MH_OK)
            {
                create9ApiHookCreated = true;
                LogDx9("event=install_step step=mh_create_api_direct3dcreate9 ok original=%p target=%p.", reinterpret_cast<void*>(rt.originalDirect3DCreate9), rt.direct3DCreate9ApiTarget);
            }
            else
            {
                LogDx9("event=install_step step=mh_create_api_direct3dcreate9 skip reason=create_failed.");
            }

            LogDx9("event=install_step step=mh_create_api_direct3dcreate9ex begin.");
            if (MH_CreateHookApiEx(
                    L"d3d9.dll",
                    "Direct3DCreate9Ex",
                    reinterpret_cast<LPVOID>(&HookedDirect3DCreate9Ex),
                    reinterpret_cast<LPVOID*>(&rt.originalDirect3DCreate9Ex),
                    reinterpret_cast<LPVOID*>(&rt.direct3DCreate9ExApiTarget)) == MH_OK)
            {
                create9ExApiHookCreated = true;
                LogDx9("event=install_step step=mh_create_api_direct3dcreate9ex ok original=%p target=%p.", reinterpret_cast<void*>(rt.originalDirect3DCreate9Ex), rt.direct3DCreate9ExApiTarget);
            }
            else
            {
                LogDx9("event=install_step step=mh_create_api_direct3dcreate9ex skip reason=create_failed.");
            }

            LogDx9("event=install_step step=mh_enable_present begin target=%p.", rt.presentTarget);
            if (MH_EnableHook(rt.presentTarget) != MH_OK)
            {
                LogDx9("event=install_hook_result result=fail reason=mh_enable_present_failed.");
                if (rt.resetExTarget != nullptr)
                {
                    (void)MH_RemoveHook(rt.resetExTarget);
                }
                if (rt.presentExTarget != nullptr)
                {
                    (void)MH_RemoveHook(rt.presentExTarget);
                }
                (void)MH_RemoveHook(rt.swapChainPresentTarget);
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
                if (rt.resetExTarget != nullptr)
                {
                    (void)MH_RemoveHook(rt.resetExTarget);
                }
                if (rt.presentExTarget != nullptr)
                {
                    (void)MH_RemoveHook(rt.presentExTarget);
                }
                (void)MH_RemoveHook(rt.swapChainPresentTarget);
                (void)MH_RemoveHook(rt.resetTarget);
                (void)MH_RemoveHook(rt.presentTarget);
                return false;
            }
            LogDx9("event=install_step step=mh_enable_reset ok.");

            LogDx9("event=install_step step=mh_enable_swapchain_present begin target=%p.", rt.swapChainPresentTarget);
            if (MH_EnableHook(rt.swapChainPresentTarget) != MH_OK)
            {
                LogDx9("event=install_hook_result result=fail reason=mh_enable_swapchain_present_failed.");
                (void)MH_DisableHook(rt.resetTarget);
                (void)MH_DisableHook(rt.presentTarget);
                if (rt.resetExTarget != nullptr)
                {
                    (void)MH_RemoveHook(rt.resetExTarget);
                }
                if (rt.presentExTarget != nullptr)
                {
                    (void)MH_RemoveHook(rt.presentExTarget);
                }
                (void)MH_RemoveHook(rt.swapChainPresentTarget);
                (void)MH_RemoveHook(rt.resetTarget);
                (void)MH_RemoveHook(rt.presentTarget);
                return false;
            }
            LogDx9("event=install_step step=mh_enable_swapchain_present ok.");

            if (createDeviceHookCreated)
            {
                LogDx9("event=install_step step=mh_enable_create_device begin target=%p.", rt.createDeviceTarget);
                if (MH_EnableHook(rt.createDeviceTarget) != MH_OK)
                {
                    LogDx9("event=install_step step=mh_enable_create_device skip reason=enable_failed.");
                    (void)MH_RemoveHook(rt.createDeviceTarget);
                    rt.originalCreateDevice = nullptr;
                }
                else
                {
                    LogDx9("event=install_step step=mh_enable_create_device ok.");
                }
            }

            if (createDeviceExHookCreated && rt.createDeviceExTarget != nullptr)
            {
                LogDx9("event=install_step step=mh_enable_create_device_ex begin target=%p.", rt.createDeviceExTarget);
                if (MH_EnableHook(rt.createDeviceExTarget) != MH_OK)
                {
                    LogDx9("event=install_step step=mh_enable_create_device_ex skip reason=enable_failed.");
                    (void)MH_RemoveHook(rt.createDeviceExTarget);
                    rt.originalCreateDeviceEx = nullptr;
                }
                else
                {
                    LogDx9("event=install_step step=mh_enable_create_device_ex ok.");
                }
            }

            if (create9ApiHookCreated)
            {
                LogDx9("event=install_step step=mh_enable_api_direct3dcreate9 begin target=%p.", rt.direct3DCreate9ApiTarget);
                if (rt.direct3DCreate9ApiTarget == nullptr || MH_EnableHook(rt.direct3DCreate9ApiTarget) != MH_OK)
                {
                    LogDx9("event=install_step step=mh_enable_api_direct3dcreate9 skip reason=enable_failed.");
                    if (rt.direct3DCreate9ApiTarget != nullptr)
                    {
                        (void)MH_RemoveHook(rt.direct3DCreate9ApiTarget);
                    }
                    rt.originalDirect3DCreate9 = nullptr;
                    rt.direct3DCreate9ApiTarget = nullptr;
                }
                else
                {
                    LogDx9("event=install_step step=mh_enable_api_direct3dcreate9 ok.");
                }
            }

            if (create9ExApiHookCreated)
            {
                LogDx9("event=install_step step=mh_enable_api_direct3dcreate9ex begin target=%p.", rt.direct3DCreate9ExApiTarget);
                if (rt.direct3DCreate9ExApiTarget == nullptr || MH_EnableHook(rt.direct3DCreate9ExApiTarget) != MH_OK)
                {
                    LogDx9("event=install_step step=mh_enable_api_direct3dcreate9ex skip reason=enable_failed.");
                    if (rt.direct3DCreate9ExApiTarget != nullptr)
                    {
                        (void)MH_RemoveHook(rt.direct3DCreate9ExApiTarget);
                    }
                    rt.originalDirect3DCreate9Ex = nullptr;
                    rt.direct3DCreate9ExApiTarget = nullptr;
                }
                else
                {
                    LogDx9("event=install_step step=mh_enable_api_direct3dcreate9ex ok.");
                }
            }

            if (rt.presentExTarget != nullptr)
            {
                LogDx9("event=install_step step=mh_enable_present_ex begin target=%p.", rt.presentExTarget);
                if (MH_EnableHook(rt.presentExTarget) != MH_OK)
                {
                    LogDx9("event=install_hook_result result=fail reason=mh_enable_present_ex_failed.");
                    (void)MH_DisableHook(rt.resetTarget);
                    (void)MH_DisableHook(rt.presentTarget);
                    if (rt.resetExTarget != nullptr)
                    {
                        (void)MH_RemoveHook(rt.resetExTarget);
                    }
                    (void)MH_DisableHook(rt.swapChainPresentTarget);
                    (void)MH_RemoveHook(rt.presentExTarget);
                    (void)MH_RemoveHook(rt.swapChainPresentTarget);
                    (void)MH_RemoveHook(rt.resetTarget);
                    (void)MH_RemoveHook(rt.presentTarget);
                    return false;
                }
                LogDx9("event=install_step step=mh_enable_present_ex ok.");
            }

            if (rt.resetExTarget != nullptr)
            {
                LogDx9("event=install_step step=mh_enable_reset_ex begin target=%p.", rt.resetExTarget);
                if (MH_EnableHook(rt.resetExTarget) != MH_OK)
                {
                    LogDx9("event=install_hook_result result=fail reason=mh_enable_reset_ex_failed.");
                    if (rt.presentExTarget != nullptr)
                    {
                        (void)MH_DisableHook(rt.presentExTarget);
                    }
                    (void)MH_DisableHook(rt.swapChainPresentTarget);
                    (void)MH_DisableHook(rt.resetTarget);
                    (void)MH_DisableHook(rt.presentTarget);
                    (void)MH_RemoveHook(rt.resetExTarget);
                    if (rt.presentExTarget != nullptr)
                    {
                        (void)MH_RemoveHook(rt.presentExTarget);
                    }
                    (void)MH_RemoveHook(rt.swapChainPresentTarget);
                    (void)MH_RemoveHook(rt.resetTarget);
                    (void)MH_RemoveHook(rt.presentTarget);
                    return false;
                }
                LogDx9("event=install_step step=mh_enable_reset_ex ok.");
            }

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

        if (rt.swapChainPresentTarget != nullptr)
        {
            (void)MH_DisableHook(rt.swapChainPresentTarget);
            (void)MH_RemoveHook(rt.swapChainPresentTarget);
        }

        if (rt.presentExTarget != nullptr)
        {
            (void)MH_DisableHook(rt.presentExTarget);
            (void)MH_RemoveHook(rt.presentExTarget);
        }

        if (rt.resetExTarget != nullptr)
        {
            (void)MH_DisableHook(rt.resetExTarget);
            (void)MH_RemoveHook(rt.resetExTarget);
        }

        if (rt.createDeviceTarget != nullptr)
        {
            (void)MH_DisableHook(rt.createDeviceTarget);
            (void)MH_RemoveHook(rt.createDeviceTarget);
        }

        if (rt.createDeviceExTarget != nullptr)
        {
            (void)MH_DisableHook(rt.createDeviceExTarget);
            (void)MH_RemoveHook(rt.createDeviceExTarget);
        }

        if (rt.direct3DCreate9ApiTarget != nullptr)
        {
            (void)MH_DisableHook(rt.direct3DCreate9ApiTarget);
            (void)MH_RemoveHook(rt.direct3DCreate9ApiTarget);
        }

        if (rt.direct3DCreate9ExApiTarget != nullptr)
        {
            (void)MH_DisableHook(rt.direct3DCreate9ExApiTarget);
            (void)MH_RemoveHook(rt.direct3DCreate9ExApiTarget);
        }

        (void)MH_Uninitialize();

        rt.presentTarget = nullptr;
        rt.resetTarget = nullptr;
        rt.swapChainPresentTarget = nullptr;
        rt.presentExTarget = nullptr;
        rt.resetExTarget = nullptr;
        rt.createDeviceTarget = nullptr;
        rt.createDeviceExTarget = nullptr;
        rt.originalPresent = nullptr;
        rt.originalReset = nullptr;
        rt.originalSwapChainPresent = nullptr;
        rt.originalPresentEx = nullptr;
        rt.originalResetEx = nullptr;
        rt.originalCreateDevice = nullptr;
        rt.originalCreateDeviceEx = nullptr;
        rt.originalDirect3DCreate9 = nullptr;
        rt.originalDirect3DCreate9Ex = nullptr;
        rt.direct3DCreate9ApiTarget = nullptr;
        rt.direct3DCreate9ExApiTarget = nullptr;
        ResetRuntimeStateLocked(rt);
        LogDx9("event=uninstall_hook result=ok.");
        CloseDiagFile();
    }
}

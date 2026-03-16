#include "Dx11PresentHook.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstdio>
#include <cstdint>
#include <cinttypes>
#include <cstring>
#include <deque>
#include <iterator>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi.h>
#include <dxgi1_2.h>

#include <MinHook.h>

#include <imgui.h>
#include <imgui_impl_dx11.h>

#include "../HookCommon/SharedFrameWriter.h"
#include "../HookCommon/SharedHookConfig.h"
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

        // WHY: Some titles/runtime layers call Present from inside Present1 (or re-enter Present) on the same
        // thread. If we hook both, we'd draw/capture twice and can produce visible flicker/ghosting.
        static thread_local int g_presentDepth = 0;

        constexpr int kOverlayV2DebugSamples = 8;
        constexpr int kPresentDebugSamples = 16;
        constexpr int kPresentPerfSamples = 256;
        constexpr int kOverlayFontSteps = 16;
        constexpr std::uint64_t kHookSuccessIndicatorDurationMs = 1500;
        constexpr std::uint64_t kHookSuccessIndicatorFadeInMs = 200;
        constexpr std::uint64_t kHookSuccessIndicatorFadeOutMs = 300;
        constexpr std::uint64_t kPresentPerfFlushIntervalMs = 2000;

        struct OverlayV2DebugSample
        {
            std::uint64_t seq = 0;
            std::uint32_t canvasW = 0;
            std::uint32_t canvasH = 0;
            std::uint32_t blocks = 0;
            std::uint32_t textBytes = 0;
            float x = 0.0f;
            float y = 0.0f;
            float w = 0.0f;
            float h = 0.0f;
        };

        struct PresentDebugSample
        {
            std::uint64_t qpc = 0;
            std::uint32_t kind = 0; // 1=Present, 2=Present1
            std::uint32_t tid = 0;
            std::uintptr_t swapPtr = 0;
            std::uint32_t scBufferCount = 0;
            std::uint32_t scSwapEffect = 0;
            std::uint32_t scFlags = 0;
            std::uint32_t bbW = 0;
            std::uint32_t bbH = 0;
            std::uint64_t ovlSeq = 0;
        };

        struct PresentPerfSample
        {
            std::uint32_t kind = 0; // 1=Present, 2=Present1
            std::uint64_t totalQpc = 0;
            std::uint64_t lockWaitQpc = 0;
            std::uint64_t lockHoldQpc = 0;
            std::uint64_t originalPresentQpc = 0;
            std::uint64_t debugRecordQpc = 0;
            std::uint64_t captureQpc = 0;
            std::uint64_t captureCopyQpc = 0;
            std::uint64_t captureMapQpc = 0;
            std::uint64_t overlayRefreshQpc = 0;
            std::uint64_t overlayDrawQpc = 0;
            std::uint64_t statusQpc = 0;
            std::uint32_t capturePublished = 0;
            std::uint32_t captureSlotBusy = 0;
            std::uint32_t captureMapDeferred = 0;
            std::uint32_t captureIssued = 0;
        };

        enum class CaptureSlotState : std::uint32_t
        {
            Free = 0,
            Pending = 1,
            ReadyToPublish = 2,
        };

        struct CaptureSlot
        {
            ID3D11Texture2D* texture = nullptr;
            UINT width = 0;
            UINT height = 0;
            DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
            std::uint64_t issuedQpc = 0;
            std::uint64_t sourceFrameSeq = 0;
            D3D11_MAPPED_SUBRESOURCE mapped{};
            bool mappedValid = false;
            CaptureSlotState state = CaptureSlotState::Free;
        };

        struct CapturePerfBreakdown
        {
            std::uint64_t totalQpc = 0;
            std::uint64_t copyQpc = 0;
            std::uint64_t mapQpc = 0;
            bool published = false;
            bool slotBusy = false;
            bool mapDeferred = false;
            bool issued = false;
        };

        struct Dx11PublishRequest
        {
            CaptureSlot* slot = nullptr;
            DWORD pid = 0;
            std::uint64_t publishQpc = 0;
        };

        struct OverlayFontSet
        {
            const char* path = nullptr;
            int face = 0;
            float sizesPx[kOverlayFontSteps]{};
            ImFont* fonts[kOverlayFontSteps]{};
            std::uint32_t count = 0;
        };

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
            ht::hook::ipc::SharedOverlayV2Reader overlayV2Reader;
            ht::hook::ipc::SharedHookStatusWriter statusWriter;

            ID3D11Device* device = nullptr;
            ID3D11DeviceContext* context = nullptr;
            ID3D11DeviceContext1* context1 = nullptr;
            ID3D11RenderTargetView* backBufferRtv = nullptr;
            UINT backBufferWidth = 0;
            UINT backBufferHeight = 0;
            DXGI_FORMAT backBufferFormat = DXGI_FORMAT_UNKNOWN;
            UINT stagingWidth = 0;
            UINT stagingHeight = 0;
            DXGI_FORMAT stagingFormat = DXGI_FORMAT_UNKNOWN;
            std::vector<CaptureSlot> captureRing;
            std::uint32_t captureRingSize = 3;
            std::uint64_t lastCaptureIssueQpc = 0;
            std::uint64_t captureSourceFrameSeq = 0;

            std::vector<std::uint8_t> scratch;
            std::vector<std::uint8_t> publishScratch;
            std::atomic_ullong frameId{0};
            std::atomic_ullong lastCaptureQpc{0};
            std::uint64_t captureIntervalQpc = 0;
            std::uint64_t qpcFreq = 0;
            std::uint64_t lastConfigQpc = 0;
            std::uint32_t configuredFpsLimit = 15;
            bool overlayEnabled = true;
            bool diagFileSinkEnabled = false;
            std::uint64_t lastOverlayV2Seq = 0;
            std::uint64_t lastOverlayV2Qpc = 0;
            std::uint64_t lastOverlayTraceDrawSeq = 0;
            std::uint64_t lastOverlayCanvasMismatchSeq = 0;
            ht::hook::ipc::OverlayV2Header overlayV2Header{};
            std::vector<ht::hook::ipc::OverlayTextBlockV2> overlayV2Blocks;
            std::vector<std::uint8_t> overlayV2TextBlob;
            bool hookSuccessIndicatorArmed = false;
            bool hookSuccessIndicatorDone = false;
            std::uint64_t hookSuccessIndicatorStartQpc = 0;

            std::uint64_t presentCount = 0;
            std::uint64_t lastPresentQpc = 0;
            std::uint32_t lastPresentKind = 0; // 1=Present, 2=Present1
            std::uint32_t activePresentKind = 0; // 0=auto(first seen), 1=Present, 2=Present1

            bool imguiInitialized = false;
            ImGuiContext* imguiContext = nullptr;
            std::uint64_t lastImGuiQpc = 0;
            OverlayFontSet overlayFonts{};
            float lastBlock0DesiredFontPx = 0.0f;
            float lastBlock0SelectedFontPx = 0.0f;

            OverlayV2DebugSample overlayV2Debug[kOverlayV2DebugSamples]{};
            std::uint32_t overlayV2DebugNext = 0;
            std::uint32_t overlayV2DebugCount = 0;

            PresentDebugSample presentDebug[kPresentDebugSamples]{};
            std::uint32_t presentDebugNext = 0;
            std::uint32_t presentDebugCount = 0;

            PresentPerfSample presentPerf[kPresentPerfSamples]{};
            std::uint32_t presentPerfNext = 0;
            std::uint32_t presentPerfCount = 0;
            std::uint64_t lastPerfFlushQpc = 0;

            std::uintptr_t lastOmOldRtvPtr = 0;
            std::uintptr_t lastOmOldDsvPtr = 0;
            std::uintptr_t lastImGuiTargetRtvPtr = 0;
            bool lastImGuiUsedOldRtv = false;
            std::uint32_t lastCaptureSourceMode = 0; // 0=GetBuffer, 1=prefer OM RTV, 2=OM RTV only
            std::uintptr_t lastCaptureSelectedTexPtr = 0;
            std::uintptr_t lastCaptureGetBufferTexPtr = 0;
            std::uintptr_t lastCaptureOmRtvPtr = 0;
            std::uintptr_t lastCaptureOmTexPtr = 0;
            bool lastCaptureUsedOmRtv = false;
            bool lastCaptureOmMatchesGetBuffer = false;

            std::mutex publishMutex;
            std::condition_variable publishCv;
            std::deque<Dx11PublishRequest> publishQueue;
            std::vector<CaptureSlot*> publishCompleted;
            std::thread publishThread;
            bool publishStop = false;
            std::uint32_t publishActiveCount = 0;
        };

        Dx11Runtime g_rt;
        std::mutex g_diagFileMutex;
        HANDLE g_diagFileHandle = INVALID_HANDLE_VALUE;
        DWORD g_diagFilePid = 0;
        std::wstring g_diagFilePath;

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

        void DebugLogConfigApplied(const ht::hook::ipc::HookConfigHeader& cfg)
        {
            if (ReadEnvU32(L"HT_HOOK_CFG_DEBUG", 0) == 0)
            {
                return;
            }

            char msg[256]{};
            std::snprintf(
                msg,
                sizeof(msg),
                "HT HookAgentDx11: config applied qpc=%llu fps=%u overlay=%u pid=%u\n",
                static_cast<unsigned long long>(cfg.updatedQpc),
                static_cast<unsigned int>(cfg.captureFpsLimit),
                static_cast<unsigned int>(cfg.overlayEnabled),
                static_cast<unsigned int>(cfg.targetPid));
            OutputDebugStringA(msg);
        }

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

        bool IsPresentPerfTraceEnabled()
        {
            return ReadEnvU32(L"HT_HOOK_PERF_TRACE", 0) != 0;
        }

        bool IsPresentPerfFileEnabled()
        {
            return ReadEnvU32(L"HT_HOOK_PERF_FILE", 0) != 0;
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
            (void)swprintf_s(fileName, L"hook_dx11_perf_%lu.log", static_cast<unsigned long>(pid));
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
                    "stage=hook_dx11 event=perf_file_open pid=%lu path=\"%s\".",
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

        double QpcToMs(std::uint64_t qpc, std::uint64_t qpcFreq)
        {
            if (qpc == 0 || qpcFreq == 0)
            {
                return 0.0;
            }

            return (static_cast<double>(qpc) * 1000.0) / static_cast<double>(qpcFreq);
        }

        template <typename TAccessor>
        bool SummarizePerfFieldLocked(
            const Dx11Runtime& rt,
            TAccessor accessor,
            std::uint64_t& avgQpc,
            std::uint64_t& p95Qpc,
            std::uint64_t& p99Qpc,
            std::uint64_t& maxQpc,
            std::size_t& count)
        {
            avgQpc = 0;
            p95Qpc = 0;
            p99Qpc = 0;
            maxQpc = 0;
            count = 0;
            if (rt.presentPerfCount == 0)
            {
                return false;
            }

            std::vector<std::uint64_t> values;
            values.reserve(rt.presentPerfCount);

            std::uint64_t sumQpc = 0;
            for (std::uint32_t i = 0; i < rt.presentPerfCount; i++)
            {
                const std::uint64_t valueQpc = accessor(rt.presentPerf[i]);
                if (valueQpc == 0)
                {
                    continue;
                }

                values.push_back(valueQpc);
                sumQpc += valueQpc;
            }

            if (values.empty())
            {
                return false;
            }

            std::sort(values.begin(), values.end());
            count = values.size();
            avgQpc = sumQpc / static_cast<std::uint64_t>(count);
            maxQpc = values.back();

            const auto percentileIndex = [count](std::size_t percentile) -> std::size_t
            {
                const std::size_t ceilRank = ((count * percentile) + 99u) / 100u;
                return std::min<std::size_t>(count - 1u, std::max<std::size_t>(0u, ceilRank > 0 ? (ceilRank - 1u) : 0u));
            };

            p95Qpc = values[percentileIndex(95)];
            p99Qpc = values[percentileIndex(99)];
            return true;
        }

        void EmitPresentPerfSummaryLocked(Dx11Runtime& rt, std::uint64_t nowQpc)
        {
            if (rt.presentPerfCount == 0)
            {
                rt.lastPerfFlushQpc = nowQpc;
                return;
            }

            std::uint32_t presentCalls = 0;
            std::uint32_t present1Calls = 0;
            for (std::uint32_t i = 0; i < rt.presentPerfCount; i++)
            {
                if (rt.presentPerf[i].kind == 1)
                {
                    presentCalls++;
                }
                else if (rt.presentPerf[i].kind == 2)
                {
                    present1Calls++;
                }
            }

            std::uint64_t totalAvgQpc = 0;
            std::uint64_t totalP95Qpc = 0;
            std::uint64_t totalP99Qpc = 0;
            std::uint64_t totalMaxQpc = 0;
            std::size_t totalCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.totalQpc; },
                totalAvgQpc,
                totalP95Qpc,
                totalP99Qpc,
                totalMaxQpc,
                totalCount);

            std::uint64_t lockWaitAvgQpc = 0;
            std::uint64_t lockWaitP95Qpc = 0;
            std::uint64_t lockWaitP99Qpc = 0;
            std::uint64_t lockWaitMaxQpc = 0;
            std::size_t lockWaitCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.lockWaitQpc; },
                lockWaitAvgQpc,
                lockWaitP95Qpc,
                lockWaitP99Qpc,
                lockWaitMaxQpc,
                lockWaitCount);

            std::uint64_t lockHoldAvgQpc = 0;
            std::uint64_t lockHoldP95Qpc = 0;
            std::uint64_t lockHoldP99Qpc = 0;
            std::uint64_t lockHoldMaxQpc = 0;
            std::size_t lockHoldCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.lockHoldQpc; },
                lockHoldAvgQpc,
                lockHoldP95Qpc,
                lockHoldP99Qpc,
                lockHoldMaxQpc,
                lockHoldCount);

            std::uint64_t originalAvgQpc = 0;
            std::uint64_t originalP95Qpc = 0;
            std::uint64_t originalP99Qpc = 0;
            std::uint64_t originalMaxQpc = 0;
            std::size_t originalCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.originalPresentQpc; },
                originalAvgQpc,
                originalP95Qpc,
                originalP99Qpc,
                originalMaxQpc,
                originalCount);

            std::uint64_t debugAvgQpc = 0;
            std::uint64_t debugP95Qpc = 0;
            std::uint64_t debugP99Qpc = 0;
            std::uint64_t debugMaxQpc = 0;
            std::size_t debugCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.debugRecordQpc; },
                debugAvgQpc,
                debugP95Qpc,
                debugP99Qpc,
                debugMaxQpc,
                debugCount);

            std::uint64_t captureAvgQpc = 0;
            std::uint64_t captureP95Qpc = 0;
            std::uint64_t captureP99Qpc = 0;
            std::uint64_t captureMaxQpc = 0;
            std::size_t captureCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.captureQpc; },
                captureAvgQpc,
                captureP95Qpc,
                captureP99Qpc,
                captureMaxQpc,
                captureCount);

            std::uint64_t captureCopyAvgQpc = 0;
            std::uint64_t captureCopyP95Qpc = 0;
            std::uint64_t captureCopyP99Qpc = 0;
            std::uint64_t captureCopyMaxQpc = 0;
            std::size_t captureCopyCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.captureCopyQpc; },
                captureCopyAvgQpc,
                captureCopyP95Qpc,
                captureCopyP99Qpc,
                captureCopyMaxQpc,
                captureCopyCount);

            std::uint64_t captureMapAvgQpc = 0;
            std::uint64_t captureMapP95Qpc = 0;
            std::uint64_t captureMapP99Qpc = 0;
            std::uint64_t captureMapMaxQpc = 0;
            std::size_t captureMapCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.captureMapQpc; },
                captureMapAvgQpc,
                captureMapP95Qpc,
                captureMapP99Qpc,
                captureMapMaxQpc,
                captureMapCount);

            std::uint32_t capturePublishedCount = 0;
            std::uint32_t captureSlotBusyCount = 0;
            std::uint32_t captureMapDeferredCount = 0;
            std::uint32_t captureIssuedCount = 0;
            for (std::uint32_t i = 0; i < rt.presentPerfCount; i++)
            {
                capturePublishedCount += rt.presentPerf[i].capturePublished;
                captureSlotBusyCount += rt.presentPerf[i].captureSlotBusy;
                captureMapDeferredCount += rt.presentPerf[i].captureMapDeferred;
                captureIssuedCount += rt.presentPerf[i].captureIssued;
            }

            std::uint64_t overlayRefreshAvgQpc = 0;
            std::uint64_t overlayRefreshP95Qpc = 0;
            std::uint64_t overlayRefreshP99Qpc = 0;
            std::uint64_t overlayRefreshMaxQpc = 0;
            std::size_t overlayRefreshCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.overlayRefreshQpc; },
                overlayRefreshAvgQpc,
                overlayRefreshP95Qpc,
                overlayRefreshP99Qpc,
                overlayRefreshMaxQpc,
                overlayRefreshCount);

            std::uint64_t overlayDrawAvgQpc = 0;
            std::uint64_t overlayDrawP95Qpc = 0;
            std::uint64_t overlayDrawP99Qpc = 0;
            std::uint64_t overlayDrawMaxQpc = 0;
            std::size_t overlayDrawCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.overlayDrawQpc; },
                overlayDrawAvgQpc,
                overlayDrawP95Qpc,
                overlayDrawP99Qpc,
                overlayDrawMaxQpc,
                overlayDrawCount);

            std::uint64_t statusAvgQpc = 0;
            std::uint64_t statusP95Qpc = 0;
            std::uint64_t statusP99Qpc = 0;
            std::uint64_t statusMaxQpc = 0;
            std::size_t statusCount = 0;
            (void)SummarizePerfFieldLocked(
                rt,
                [](const PresentPerfSample& s) { return s.statusQpc; },
                statusAvgQpc,
                statusP95Qpc,
                statusP99Qpc,
                statusMaxQpc,
                statusCount);

            char msg[2048]{};
            std::snprintf(
                msg,
                sizeof(msg),
                "HT HookAgentDx11: perf samples=%u present=%u present1=%u total_ms=%.3f/%.3f/%.3f/%.3f lock_wait_ms=%.3f/%.3f/%.3f/%.3f lock_hold_ms=%.3f/%.3f/%.3f/%.3f orig_ms=%.3f/%.3f/%.3f/%.3f dbg_ms=%.3f/%.3f/%.3f/%.3f capture_ms=%.3f/%.3f/%.3f/%.3f capture_copy_ms=%.3f/%.3f/%.3f/%.3f capture_map_ms=%.3f/%.3f/%.3f/%.3f capture_issue=%u capture_publish=%u capture_defer=%u capture_busy=%u ovl_refresh_ms=%.3f/%.3f/%.3f/%.3f ovl_draw_ms=%.3f/%.3f/%.3f/%.3f status_ms=%.3f/%.3f/%.3f/%.3f\n",
                static_cast<unsigned int>(rt.presentPerfCount),
                static_cast<unsigned int>(presentCalls),
                static_cast<unsigned int>(present1Calls),
                QpcToMs(totalAvgQpc, rt.qpcFreq),
                QpcToMs(totalP95Qpc, rt.qpcFreq),
                QpcToMs(totalP99Qpc, rt.qpcFreq),
                QpcToMs(totalMaxQpc, rt.qpcFreq),
                QpcToMs(lockWaitAvgQpc, rt.qpcFreq),
                QpcToMs(lockWaitP95Qpc, rt.qpcFreq),
                QpcToMs(lockWaitP99Qpc, rt.qpcFreq),
                QpcToMs(lockWaitMaxQpc, rt.qpcFreq),
                QpcToMs(lockHoldAvgQpc, rt.qpcFreq),
                QpcToMs(lockHoldP95Qpc, rt.qpcFreq),
                QpcToMs(lockHoldP99Qpc, rt.qpcFreq),
                QpcToMs(lockHoldMaxQpc, rt.qpcFreq),
                QpcToMs(originalAvgQpc, rt.qpcFreq),
                QpcToMs(originalP95Qpc, rt.qpcFreq),
                QpcToMs(originalP99Qpc, rt.qpcFreq),
                QpcToMs(originalMaxQpc, rt.qpcFreq),
                QpcToMs(debugAvgQpc, rt.qpcFreq),
                QpcToMs(debugP95Qpc, rt.qpcFreq),
                QpcToMs(debugP99Qpc, rt.qpcFreq),
                QpcToMs(debugMaxQpc, rt.qpcFreq),
                QpcToMs(captureAvgQpc, rt.qpcFreq),
                QpcToMs(captureP95Qpc, rt.qpcFreq),
                QpcToMs(captureP99Qpc, rt.qpcFreq),
                QpcToMs(captureMaxQpc, rt.qpcFreq),
                QpcToMs(captureCopyAvgQpc, rt.qpcFreq),
                QpcToMs(captureCopyP95Qpc, rt.qpcFreq),
                QpcToMs(captureCopyP99Qpc, rt.qpcFreq),
                QpcToMs(captureCopyMaxQpc, rt.qpcFreq),
                QpcToMs(captureMapAvgQpc, rt.qpcFreq),
                QpcToMs(captureMapP95Qpc, rt.qpcFreq),
                QpcToMs(captureMapP99Qpc, rt.qpcFreq),
                QpcToMs(captureMapMaxQpc, rt.qpcFreq),
                static_cast<unsigned int>(captureIssuedCount),
                static_cast<unsigned int>(capturePublishedCount),
                static_cast<unsigned int>(captureMapDeferredCount),
                static_cast<unsigned int>(captureSlotBusyCount),
                QpcToMs(overlayRefreshAvgQpc, rt.qpcFreq),
                QpcToMs(overlayRefreshP95Qpc, rt.qpcFreq),
                QpcToMs(overlayRefreshP99Qpc, rt.qpcFreq),
                QpcToMs(overlayRefreshMaxQpc, rt.qpcFreq),
                QpcToMs(overlayDrawAvgQpc, rt.qpcFreq),
                QpcToMs(overlayDrawP95Qpc, rt.qpcFreq),
                QpcToMs(overlayDrawP99Qpc, rt.qpcFreq),
                QpcToMs(overlayDrawMaxQpc, rt.qpcFreq),
                QpcToMs(statusAvgQpc, rt.qpcFreq),
                QpcToMs(statusP95Qpc, rt.qpcFreq),
                QpcToMs(statusP99Qpc, rt.qpcFreq),
                QpcToMs(statusMaxQpc, rt.qpcFreq));
            OutputDebugStringA(msg);
            if (rt.diagFileSinkEnabled || IsPresentPerfFileEnabled())
            {
                // WHY: Persist periodic perf summaries so Steam/launcher runs can be compared without a live debugger.
                AppendDiagFileLine(msg);
            }

            rt.presentPerfNext = 0;
            rt.presentPerfCount = 0;
            rt.lastPerfFlushQpc = nowQpc;
        }

        void RecordPresentPerfLocked(Dx11Runtime& rt, const PresentPerfSample& sample)
        {
            if (!IsPresentPerfTraceEnabled())
            {
                return;
            }

            const std::uint32_t slot = rt.presentPerfNext % kPresentPerfSamples;
            rt.presentPerf[slot] = sample;
            rt.presentPerfNext = slot + 1;
            rt.presentPerfCount = std::min<std::uint32_t>(rt.presentPerfCount + 1, kPresentPerfSamples);

            const auto nowQpc = NowQpc();
            if (rt.lastPerfFlushQpc == 0)
            {
                rt.lastPerfFlushQpc = nowQpc;
            }

            const std::uint64_t flushIntervalQpc =
                (rt.qpcFreq != 0)
                    ? ((rt.qpcFreq * kPresentPerfFlushIntervalMs) / 1000ull)
                    : 0;
            const bool shouldFlush =
                rt.presentPerfCount >= kPresentPerfSamples ||
                (flushIntervalQpc != 0 && nowQpc >= rt.lastPerfFlushQpc && (nowQpc - rt.lastPerfFlushQpc) >= flushIntervalQpc);
            if (shouldFlush)
            {
                EmitPresentPerfSummaryLocked(rt, nowQpc);
            }
        }

        std::uint32_t ForceAlpha(std::uint32_t argb, std::uint32_t a)
        {
            return (argb & 0x00FFFFFFu) | ((a & 0xFFu) << 24);
        }

        ImFont* SelectOverlayFontLocked(Dx11Runtime& rt, float desiredPx)
        {
            ImGuiIO& io = ImGui::GetIO();
            if (desiredPx <= 0.0f || rt.overlayFonts.count == 0)
            {
                return io.FontDefault;
            }

            ImFont* best = nullptr;
            float bestDist = 1.0e9f;
            for (std::uint32_t i = 0; i < rt.overlayFonts.count; i++)
            {
                ImFont* f = rt.overlayFonts.fonts[i];
                const float sz = rt.overlayFonts.sizesPx[i];
                if (f == nullptr || sz <= 0.0f)
                {
                    continue;
                }

                const float d = std::fabs(sz - desiredPx);
                if (best == nullptr || d < bestDist)
                {
                    best = f;
                    bestDist = d;
                }
            }

            return (best != nullptr) ? best : io.FontDefault;
        }

        void RecordPresentDebugLocked(Dx11Runtime& rt, IDXGISwapChain* swap, std::uint32_t kind)
        {
            const std::uint32_t slot = rt.presentDebugNext % kPresentDebugSamples;
            auto& s = rt.presentDebug[slot];
            s.qpc = NowQpc();
            s.kind = kind;
            s.tid = GetCurrentThreadId();
            s.swapPtr = reinterpret_cast<std::uintptr_t>(swap);

            DXGI_SWAP_CHAIN_DESC desc{};
            if (swap != nullptr && swap->GetDesc(&desc) == S_OK)
            {
                s.scBufferCount = desc.BufferCount;
                s.scSwapEffect = static_cast<std::uint32_t>(desc.SwapEffect);
                s.scFlags = desc.Flags;
            }
            else
            {
                s.scBufferCount = 0;
                s.scSwapEffect = 0;
                s.scFlags = 0;
            }

            s.bbW = rt.backBufferWidth;
            s.bbH = rt.backBufferHeight;
            s.ovlSeq = rt.lastOverlayV2Seq;

            rt.presentDebugNext = slot + 1;
            rt.presentDebugCount = std::min<std::uint32_t>(rt.presentDebugCount + 1, kPresentDebugSamples);
        }

        void SafeRelease(IUnknown*& ptr)
        {
            if (ptr != nullptr)
            {
                ptr->Release();
                ptr = nullptr;
            }
        }

        std::uint32_t ResolveCaptureRingSize()
        {
            return std::clamp(ReadEnvU32(L"HT_HOOK_CAPTURE_RING_SIZE", 3), 2u, 8u);
        }

        bool TryResolveCaptureFormat(DXGI_FORMAT format, bool& rgbaNeedsSwap)
        {
            rgbaNeedsSwap = false;
            switch (format)
            {
            case DXGI_FORMAT_B8G8R8A8_UNORM:
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
                return true;
            case DXGI_FORMAT_R8G8B8A8_UNORM:
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
                rgbaNeedsSwap = true;
                return true;
            default:
                return false;
            }
        }

        void PublishWorkerMain(Dx11Runtime* runtime)
        {
            if (runtime == nullptr)
            {
                return;
            }

            for (;;)
            {
                Dx11PublishRequest request{};
                {
                    std::unique_lock<std::mutex> lock(runtime->publishMutex);
                    runtime->publishCv.wait(lock, [&]
                    {
                        return runtime->publishStop || !runtime->publishQueue.empty();
                    });

                    if (runtime->publishQueue.empty())
                    {
                        if (runtime->publishStop)
                        {
                            break;
                        }

                        continue;
                    }

                    request = runtime->publishQueue.front();
                    runtime->publishQueue.pop_front();
                    runtime->publishActiveCount++;
                }

                auto* slot = request.slot;
                if (slot != nullptr && slot->mappedValid && slot->mapped.pData != nullptr)
                {
                    bool rgbaNeedsSwap = false;
                    if (TryResolveCaptureFormat(slot->format, rgbaNeedsSwap))
                    {
                        const std::uint32_t width = slot->width;
                        const std::uint32_t height = slot->height;
                        const std::uint32_t stride = static_cast<std::uint32_t>(slot->mapped.RowPitch);
                        const std::size_t payloadBytes = static_cast<std::size_t>(stride) * static_cast<std::size_t>(height);

                        runtime->publishScratch.resize(payloadBytes);
                        std::memcpy(runtime->publishScratch.data(), slot->mapped.pData, payloadBytes);

                        if (rgbaNeedsSwap)
                        {
                            const std::uint32_t rowBytes = width * 4;
                            for (std::uint32_t y = 0; y < height; y++)
                            {
                                auto* row = runtime->publishScratch.data() + (static_cast<std::size_t>(y) * stride);
                                for (std::uint32_t x = 0; x < rowBytes; x += 4)
                                {
                                    std::swap(row[x + 0], row[x + 2]); // RGBA -> BGRA
                                }
                            }
                        }

                        const auto frameId = runtime->frameId.fetch_add(1, std::memory_order_relaxed) + 1;
                        const bool wrote = runtime->frameWriter.WriteFrame(
                            request.pid,
                            ht::hook::ipc::GraphicsApi::Dx11,
                            frameId,
                            width,
                            height,
                            stride,
                            request.publishQpc,
                            runtime->publishScratch.data(),
                            runtime->publishScratch.size());
                        if (wrote)
                        {
                            runtime->lastCaptureQpc.store(request.publishQpc, std::memory_order_release);
                        }
                    }
                }

                {
                    std::lock_guard<std::mutex> lock(runtime->publishMutex);
                    if (slot != nullptr)
                    {
                        runtime->publishCompleted.push_back(slot);
                    }

                    if (runtime->publishActiveCount > 0)
                    {
                        runtime->publishActiveCount--;
                    }
                }

                runtime->publishCv.notify_all();
            }
        }

        bool EnsurePublishWorkerLocked(Dx11Runtime& rt)
        {
            if (rt.publishThread.joinable())
            {
                return true;
            }

            {
                std::lock_guard<std::mutex> lock(rt.publishMutex);
                rt.publishStop = false;
                rt.publishQueue.clear();
                rt.publishCompleted.clear();
                rt.publishActiveCount = 0;
            }

            try
            {
                rt.publishThread = std::thread(&PublishWorkerMain, &rt);
                return true;
            }
            catch (...)
            {
                return false;
            }
        }

        void CleanupCompletedCaptureSlotsLocked(Dx11Runtime& rt)
        {
            std::vector<CaptureSlot*> completed;
            {
                std::lock_guard<std::mutex> lock(rt.publishMutex);
                completed.swap(rt.publishCompleted);
            }

            for (auto* slot : completed)
            {
                if (slot == nullptr)
                {
                    continue;
                }

                // WHY: Keep Unmap on the Present thread so the immediate context does not cross thread boundaries.
                if (slot->mappedValid && rt.context != nullptr && slot->texture != nullptr)
                {
                    rt.context->Unmap(slot->texture, 0);
                }

                slot->mapped = {};
                slot->mappedValid = false;
                slot->issuedQpc = 0;
                slot->sourceFrameSeq = 0;
                slot->state = CaptureSlotState::Free;
            }
        }

        void DrainPublishQueueLocked(Dx11Runtime& rt)
        {
            if (!rt.publishThread.joinable())
            {
                CleanupCompletedCaptureSlotsLocked(rt);
                return;
            }

            std::unique_lock<std::mutex> lock(rt.publishMutex);
            rt.publishCv.wait(lock, [&]
            {
                return rt.publishQueue.empty() && rt.publishActiveCount == 0;
            });
            lock.unlock();

            CleanupCompletedCaptureSlotsLocked(rt);
        }

        void StopPublishWorkerLocked(Dx11Runtime& rt)
        {
            if (!rt.publishThread.joinable())
            {
                CleanupCompletedCaptureSlotsLocked(rt);
                return;
            }

            {
                std::lock_guard<std::mutex> lock(rt.publishMutex);
                rt.publishStop = true;
            }
            rt.publishCv.notify_all();
            rt.publishThread.join();

            {
                std::lock_guard<std::mutex> lock(rt.publishMutex);
                rt.publishStop = false;
                rt.publishQueue.clear();
                rt.publishActiveCount = 0;
            }

            CleanupCompletedCaptureSlotsLocked(rt);
        }

        bool EnqueuePublishRequestLocked(Dx11Runtime& rt, const Dx11PublishRequest& request)
        {
            if (!rt.publishThread.joinable())
            {
                return false;
            }

            {
                std::lock_guard<std::mutex> lock(rt.publishMutex);
                rt.publishQueue.push_back(request);
            }

            rt.publishCv.notify_one();
            return true;
        }

        void ResetCaptureRingLocked(Dx11Runtime& rt)
        {
            DrainPublishQueueLocked(rt);

            if (rt.context != nullptr)
            {
                rt.context->Flush();
            }

            for (auto& slot : rt.captureRing)
            {
                IUnknown* tex = slot.texture;
                slot.texture = nullptr;
                SafeRelease(tex);
                slot.width = 0;
                slot.height = 0;
                slot.format = DXGI_FORMAT_UNKNOWN;
                slot.issuedQpc = 0;
                slot.sourceFrameSeq = 0;
                slot.mapped = {};
                slot.mappedValid = false;
                slot.state = CaptureSlotState::Free;
            }

            rt.captureRing.clear();
            rt.stagingWidth = 0;
            rt.stagingHeight = 0;
            rt.stagingFormat = DXGI_FORMAT_UNKNOWN;
            rt.lastCaptureIssueQpc = 0;
            rt.captureSourceFrameSeq = 0;
        }

        bool EnsureCaptureRingLocked(Dx11Runtime& rt, ID3D11Texture2D* source)
        {
            if (source == nullptr || rt.device == nullptr)
            {
                return false;
            }

            D3D11_TEXTURE2D_DESC desc{};
            source->GetDesc(&desc);
            const std::uint32_t desiredRingSize = ResolveCaptureRingSize();
            const bool needsRecreate =
                rt.captureRing.empty() ||
                rt.captureRingSize != desiredRingSize ||
                rt.stagingWidth != desc.Width ||
                rt.stagingHeight != desc.Height ||
                rt.stagingFormat != desc.Format;
            if (!needsRecreate)
            {
                return true;
            }

            ResetCaptureRingLocked(rt);

            D3D11_TEXTURE2D_DESC stagingDesc = desc;
            stagingDesc.BindFlags = 0;
            stagingDesc.MiscFlags = 0;
            stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            stagingDesc.Usage = D3D11_USAGE_STAGING;

            rt.captureRingSize = desiredRingSize;
            rt.captureRing.resize(rt.captureRingSize);
            for (std::uint32_t i = 0; i < rt.captureRingSize; i++)
            {
                ID3D11Texture2D* staging = nullptr;
                if (rt.device->CreateTexture2D(&stagingDesc, nullptr, &staging) != S_OK || staging == nullptr)
                {
                    ResetCaptureRingLocked(rt);
                    return false;
                }

                auto& slot = rt.captureRing[i];
                slot.texture = staging;
                slot.width = desc.Width;
                slot.height = desc.Height;
                slot.format = desc.Format;
                slot.issuedQpc = 0;
                slot.sourceFrameSeq = 0;
                slot.state = CaptureSlotState::Free;
            }

            rt.stagingWidth = desc.Width;
            rt.stagingHeight = desc.Height;
            rt.stagingFormat = desc.Format;
            return true;
        }

        void ResetImGuiLocked(Dx11Runtime& rt)
        {
            if (!rt.imguiInitialized && rt.imguiContext == nullptr)
            {
                return;
            }

            // WHY: When D3D resources are reset (ResizeBuffers/Alt+Tab), ImGui's DX11 backend must be shut down
            // before we release the device/context. It's safer to re-init next Present than to risk stale pointers.
            ImGui::SetCurrentContext(rt.imguiContext);
            ImGui_ImplDX11_Shutdown();
            ImGui::DestroyContext(rt.imguiContext);

            rt.imguiInitialized = false;
            rt.imguiContext = nullptr;
            rt.lastImGuiQpc = 0;
            rt.overlayFonts = {};
            rt.lastBlock0DesiredFontPx = 0.0f;
            rt.lastBlock0SelectedFontPx = 0.0f;
        }

        void ResetDeviceStateLocked(Dx11Runtime& rt)
        {
            ResetImGuiLocked(rt);

            // WHY: ResizeBuffers/Alt+Tab can invalidate backbuffer resources; reset so next Present can re-init.
            ResetCaptureRingLocked(rt);

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
            rt.backBufferFormat = DXGI_FORMAT_UNKNOWN;
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
            st.lastFrameIdWritten = rt.frameId.load(std::memory_order_relaxed);
            st.lastFrameWriteQpc = rt.lastCaptureQpc.load(std::memory_order_relaxed);
            // COMPAT: Keep v1 status fields populated from v2 overlay updates until status schema migration.
            st.lastCmdQpc = rt.lastOverlayV2Qpc;
            st.lastCmdCount = static_cast<std::uint32_t>(rt.overlayV2Blocks.size());
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
            const bool diagFileSinkEnabled = (cfg.reserved0 & ht::hook::ipc::kConfigFlagEnableDiagFileSink) != 0;
            if (rt.diagFileSinkEnabled && !diagFileSinkEnabled)
            {
                CloseDiagFile();
            }
            rt.diagFileSinkEnabled = diagFileSinkEnabled;
            rt.captureIntervalQpc = (rt.qpcFreq != 0) ? (rt.qpcFreq / rt.configuredFpsLimit) : 0;
            DebugLogConfigApplied(cfg);
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

            const bool needsRefresh =
                (rt.backBufferRtv == nullptr) ||
                (rt.backBufferWidth != bbDesc.Width) ||
                (rt.backBufferHeight != bbDesc.Height) ||
                (rt.backBufferFormat != bbDesc.Format);
            if (!needsRefresh)
            {
                backBuffer->Release();
                return true;
            }

            const auto oldW = rt.backBufferWidth;
            const auto oldH = rt.backBufferHeight;
            const auto oldFmt = rt.backBufferFormat;
            if (rt.backBufferRtv != nullptr)
            {
                IUnknown* oldRtv = rt.backBufferRtv;
                rt.backBufferRtv = nullptr;
                SafeRelease(oldRtv);
            }

            // WHY: Capture ring textures must match the current backbuffer dimensions/format after fullscreen transitions.
            ResetCaptureRingLocked(rt);

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
            rt.backBufferFormat = bbDesc.Format;
            char msg[320]{};
            std::snprintf(
                msg,
                sizeof(msg),
                "HT HookAgentDx11: backbuffer_refresh old=%ux%u fmt=%u new=%ux%u fmt=%u\n",
                static_cast<unsigned int>(oldW),
                static_cast<unsigned int>(oldH),
                static_cast<unsigned int>(oldFmt),
                static_cast<unsigned int>(rt.backBufferWidth),
                static_cast<unsigned int>(rt.backBufferHeight),
                static_cast<unsigned int>(rt.backBufferFormat));
            OutputDebugStringA(msg);
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
            rt.lastOverlayV2Qpc = NowQpc();
            rt.overlayV2Header = header;
            // WHY: Once v2 payload arrives, the attach-success indicator is no longer useful.
            rt.hookSuccessIndicatorArmed = false;
            rt.hookSuccessIndicatorDone = true;
            rt.hookSuccessIndicatorStartQpc = 0;

            // NOTE: Track recent v2 coordinates to diagnose "alternating/jittering" IPC updates.
            {
                const std::uint32_t slot = rt.overlayV2DebugNext % kOverlayV2DebugSamples;
                auto& s = rt.overlayV2Debug[slot];
                s.seq = header.updatedSeq;
                s.canvasW = header.canvasW;
                s.canvasH = header.canvasH;
                s.blocks = header.textBlockCount;
                s.textBytes = header.textBytes;
                if (!rt.overlayV2Blocks.empty())
                {
                    const auto& b0 = rt.overlayV2Blocks[0];
                    s.x = b0.x;
                    s.y = b0.y;
                    s.w = b0.w;
                    s.h = b0.h;
                }
                else
                {
                    s.x = s.y = s.w = s.h = 0.0f;
                }

                rt.overlayV2DebugNext = slot + 1;
                rt.overlayV2DebugCount = std::min<std::uint32_t>(rt.overlayV2DebugCount + 1, kOverlayV2DebugSamples);
            }

            if (ReadEnvU32(L"HT_HOOK_OVL_TRACE", 0) != 0)
            {
                char msg[512]{};
                if (!rt.overlayV2Blocks.empty())
                {
                    const auto& b0 = rt.overlayV2Blocks[0];
                    std::snprintf(
                        msg,
                        sizeof(msg),
                        "HT HookAgentDx11: ovl_v2_refresh seq=%llu canvas=%ux%u blocks=%u textBytes=%u b0=[%.1f,%.1f,%.1f,%.1f]\n",
                        static_cast<unsigned long long>(header.updatedSeq),
                        static_cast<unsigned int>(header.canvasW),
                        static_cast<unsigned int>(header.canvasH),
                        static_cast<unsigned int>(header.textBlockCount),
                        static_cast<unsigned int>(header.textBytes),
                        static_cast<double>(b0.x),
                        static_cast<double>(b0.y),
                        static_cast<double>(b0.w),
                        static_cast<double>(b0.h));
                }
                else
                {
                    std::snprintf(
                        msg,
                        sizeof(msg),
                        "HT HookAgentDx11: ovl_v2_refresh seq=%llu canvas=%ux%u blocks=%u textBytes=%u b0=none\n",
                        static_cast<unsigned long long>(header.updatedSeq),
                        static_cast<unsigned int>(header.canvasW),
                        static_cast<unsigned int>(header.canvasH),
                        static_cast<unsigned int>(header.textBlockCount),
                        static_cast<unsigned int>(header.textBytes));
                }

                OutputDebugStringA(msg);
            }
            return true;
        }

        bool EnsureImGuiLocked(Dx11Runtime& rt)
        {
            if (rt.imguiInitialized)
            {
                ImGui::SetCurrentContext(rt.imguiContext);
                return true;
            }

            if (rt.device == nullptr || rt.context == nullptr)
            {
                return false;
            }

            IMGUI_CHECKVERSION();
            rt.imguiContext = ImGui::CreateContext();
            ImGui::SetCurrentContext(rt.imguiContext);

            // WHY: Hook agent must not write config/log files into the target process working directory.
            ImGuiIO& io = ImGui::GetIO();
            io.IniFilename = nullptr;
            io.LogFilename = nullptr;

            // WHY: The default ImGui font only covers basic Latin. OCR/translation output often includes CJK and
            // other scripts, which would render as '?' without a font that includes those glyphs.
            // We prefer system fonts (stable path, no extra packaging) and fall back to the default font.
            //
            // NOTE: Glyph ranges affect which codepoints are baked into the atlas. Using only Japanese ranges is
            // sufficient for many Japanese translations, but will still yield '?' for other scripts even if the
            // font file contains those glyphs.
            const char* fontCandidates[] = {
                "C:\\Windows\\Fonts\\meiryo.ttc",
                "C:\\Windows\\Fonts\\msgothic.ttc",
                "C:\\Windows\\Fonts\\YuGothR.ttc",
                "C:\\Windows\\Fonts\\msyh.ttc",
                "C:\\Windows\\Fonts\\malgun.ttf",
                "C:\\Windows\\Fonts\\segoeui.ttf",
            };

            ImFontConfig cfg{};
            cfg.OversampleH = 2;
            cfg.OversampleV = 2;
            cfg.PixelSnapH = true;
            cfg.FontNo = 0;

            // WHY: Keep glyph ranges alive until the atlas is built. ImGui stores the pointer from ImFontConfig and
            // uses it later during font atlas build.
            static ImVector<ImWchar> s_glyphRanges;
            if (s_glyphRanges.empty())
            {
                ImFontGlyphRangesBuilder builder;
                builder.AddRanges(io.Fonts->GetGlyphRangesDefault());
                builder.AddRanges(io.Fonts->GetGlyphRangesJapanese());
                builder.AddRanges(io.Fonts->GetGlyphRangesChineseSimplifiedCommon());
                builder.AddRanges(io.Fonts->GetGlyphRangesKorean());
                builder.AddRanges(io.Fonts->GetGlyphRangesCyrillic());
                builder.AddRanges(io.Fonts->GetGlyphRangesVietnamese());
                builder.BuildRanges(&s_glyphRanges);
            }

            // NOTE: 16-step mapping in [14..120] inclusive (linear). This favors stability over perfect matching
            // and avoids runtime font scaling.
            static float kFontSizesPx[kOverlayFontSteps]{};
            static bool kFontSizesInited = false;
            if (!kFontSizesInited)
            {
                for (int i = 0; i < kOverlayFontSteps; i++)
                {
                    const float t = (kOverlayFontSteps > 1) ? (static_cast<float>(i) / static_cast<float>(kOverlayFontSteps - 1)) : 0.0f;
                    kFontSizesPx[i] = 14.0f + (120.0f - 14.0f) * t;
                }
                kFontSizesInited = true;
            }

            ImFont* fontDefault = nullptr;
            const char* loadedPath = nullptr;
            int loadedFace = 0;
            for (const char* path : fontCandidates)
            {
                const DWORD attr = GetFileAttributesA(path);
                if (attr == INVALID_FILE_ATTRIBUTES || (attr & FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    continue;
                }

                // NOTE: TTC collections may have multiple faces. Try a few indices to find a usable face.
                const char* ext = std::strrchr(path, '.');
                const bool isTtc = (ext != nullptr) && (_stricmp(ext, ".ttc") == 0);
                const int faceMax = isTtc ? 4 : 1;
                for (int face = 0; face < faceMax; face++)
                {
                    cfg.FontNo = face;
                    ImFont* test = io.Fonts->AddFontFromFileTTF(path, kFontSizesPx[0], &cfg, s_glyphRanges.Data);
                    if (test == nullptr)
                    {
                        continue;
                    }

                    // WHY: Avoid SetWindowFontScale. Preload multiple sizes and pick nearest per block.
                    loadedPath = path;
                    loadedFace = face;
                    rt.overlayFonts = {};
                    rt.overlayFonts.path = loadedPath;
                    rt.overlayFonts.face = loadedFace;
                    rt.overlayFonts.count = kOverlayFontSteps;
                    for (std::uint32_t i = 0; i < kOverlayFontSteps; i++)
                    {
                        rt.overlayFonts.sizesPx[i] = kFontSizesPx[i];
                        rt.overlayFonts.fonts[i] = nullptr;
                    }

                    // Reuse the probe font for slot 0.
                    rt.overlayFonts.fonts[0] = test;

                    for (std::uint32_t i = 0; i < kOverlayFontSteps; i++)
                    {
                        if (rt.overlayFonts.fonts[i] != nullptr)
                        {
                            continue;
                        }
                        cfg.FontNo = loadedFace;
                        rt.overlayFonts.fonts[i] = io.Fonts->AddFontFromFileTTF(path, kFontSizesPx[i], &cfg, s_glyphRanges.Data);
                    }

                    // Pick a readable default size near 24px.
                    std::uint32_t bestIdx = 0;
                    float bestDist = 1.0e9f;
                    for (std::uint32_t i = 0; i < kOverlayFontSteps; i++)
                    {
                        const float d = std::fabs(kFontSizesPx[i] - 24.0f);
                        if (i == 0 || d < bestDist)
                        {
                            bestIdx = i;
                            bestDist = d;
                        }
                    }
                    fontDefault = rt.overlayFonts.fonts[bestIdx];

                    break;
                }
                if (loadedPath != nullptr)
                {
                    break;
                }
            }
            if (fontDefault != nullptr)
            {
                io.FontDefault = fontDefault;
                std::string msg = "HT HookAgentDx11: ImGui font loaded: ";
                msg += (loadedPath != nullptr ? loadedPath : "(unknown)");
                msg += " face=";
                msg += std::to_string(loadedFace);
                msg += "\n";
                OutputDebugStringA(msg.c_str());
            }
            else
            {
                OutputDebugStringA("HT HookAgentDx11: ImGui font load failed; using default font (may render non-Latin as '?').\n");
            }

            ImGui_ImplDX11_Init(rt.device, rt.context);
            rt.imguiInitialized = true;
            rt.lastImGuiQpc = 0;
            return true;
        }

        ImU32 ArgbToImU32(std::uint32_t argb)
        {
            const std::uint32_t a = (argb >> 24) & 0xFF;
            const std::uint32_t r = (argb >> 16) & 0xFF;
            const std::uint32_t g = (argb >> 8) & 0xFF;
            const std::uint32_t b = (argb >> 0) & 0xFF;
            return IM_COL32(static_cast<int>(r), static_cast<int>(g), static_cast<int>(b), static_cast<int>(a));
        }

        ImVec4 ArgbToImVec4(std::uint32_t argb)
        {
            const float a = static_cast<float>((argb >> 24) & 0xFF) / 255.0f;
            const float r = static_cast<float>((argb >> 16) & 0xFF) / 255.0f;
            const float g = static_cast<float>((argb >> 8) & 0xFF) / 255.0f;
            const float b = static_cast<float>((argb >> 0) & 0xFF) / 255.0f;
            return ImVec4(r, g, b, a);
        }

        void DrawImGuiOverlayV2Locked(Dx11Runtime& rt, IDXGISwapChain* swap)
        {
            if (!rt.overlayEnabled)
            {
                return;
            }

            if (!EnsureDeviceLocked(rt, swap) || rt.device == nullptr || rt.context == nullptr)
            {
                return;
            }

            if (!EnsureBackBufferRtvLocked(rt, swap))
            {
                return;
            }

            if (rt.backBufferWidth == 0 || rt.backBufferHeight == 0)
            {
                return;
            }

            if (!EnsureImGuiLocked(rt))
            {
                return;
            }

            if (rt.qpcFreq == 0)
            {
                rt.qpcFreq = QpcFreq();
            }

            const auto now = NowQpc();
            float dt = 1.0f / 60.0f;
            if (rt.lastImGuiQpc != 0 && rt.qpcFreq != 0)
            {
                const auto diff = now - rt.lastImGuiQpc;
                dt = static_cast<float>(static_cast<double>(diff) / static_cast<double>(rt.qpcFreq));
                dt = std::max(1.0f / 240.0f, std::min(dt, 0.1f));
            }
            rt.lastImGuiQpc = now;

            ImGuiIO& io = ImGui::GetIO();
            io.DisplaySize = ImVec2(static_cast<float>(rt.backBufferWidth), static_cast<float>(rt.backBufferHeight));
            io.DeltaTime = dt;

            ImGui_ImplDX11_NewFrame();
            ImGui::NewFrame();

            const bool testMode = ReadEnvU32(L"HT_HOOK_IMGUI_TEST", 0) != 0;
            const bool debugMode = ReadEnvU32(L"HT_HOOK_OVL_DEBUG", 0) != 0;
            const bool forceOpaqueBg = ReadEnvU32(L"HT_HOOK_OVL_FORCE_OPAQUE_BG", 0) != 0;
            const bool skipText = ReadEnvU32(L"HT_HOOK_OVL_SKIP_TEXT", 0) != 0;
            const bool forceAsciiText = ReadEnvU32(L"HT_HOOK_OVL_FORCE_ASCII_TEXT", 0) != 0;
            // WHY: Prefer DrawList by default to avoid per-window border artifacts observed in some titles.
            // Set HT_HOOK_OVL_TEXT_DRAWLIST=0 to force the legacy window-text path for troubleshooting.
            const bool drawTextViaDrawList = ReadEnvU32(L"HT_HOOK_OVL_TEXT_DRAWLIST", 1) != 0;
            const bool ignoreFontPx = ReadEnvU32(L"HT_HOOK_OVL_IGNORE_FONT_PX", 0) != 0;
            const bool overlayTrace = ReadEnvU32(L"HT_HOOK_OVL_TRACE", 0) != 0;
            if (testMode)
            {
                // WHY: Native-only rendering test. This isolates the rendering path from IPC/coordinate conversion.
                // Text includes UTF-8 bytes for a few JP glyphs to validate non-Latin glyph coverage without relying
                // on the source file encoding.
                const char* text = "IMGUI TEST: \xE6\x97\xA5\xE6\x9C\xAC\xE8\xAA\x9E ABC 123";
                const ImVec2 pos(40.0f, 40.0f);
                const ImVec2 sz = ImGui::CalcTextSize(text);
                const float pad = 10.0f;
                ImDrawList* fg = ImGui::GetForegroundDrawList();
                fg->AddRectFilled(
                    ImVec2(pos.x - pad, pos.y - pad),
                    ImVec2(pos.x + sz.x + pad, pos.y + sz.y + pad),
                    IM_COL32(10, 10, 10, 180),
                    8.0f);
                fg->AddText(pos, IM_COL32(255, 255, 255, 255), text);
            }
            else
            {
            // NOTE: ROI preview blocks may not carry text (textBlob can be empty), so block existence is enough.
            const bool hasV2 = (rt.lastOverlayV2Seq != 0) && (!rt.overlayV2Blocks.empty());
            const bool hasCanvasSize = (rt.overlayV2Header.canvasW > 0) && (rt.overlayV2Header.canvasH > 0);
            const bool canvasMismatch =
                hasV2 &&
                hasCanvasSize &&
                (std::abs(static_cast<int>(rt.backBufferWidth) - static_cast<int>(rt.overlayV2Header.canvasW)) > 2 ||
                 std::abs(static_cast<int>(rt.backBufferHeight) - static_cast<int>(rt.overlayV2Header.canvasH)) > 2);
            if (canvasMismatch && rt.lastOverlayCanvasMismatchSeq != rt.lastOverlayV2Seq)
            {
                rt.lastOverlayCanvasMismatchSeq = rt.lastOverlayV2Seq;
                char msg[320]{};
                std::snprintf(
                    msg,
                    sizeof(msg),
                    "HT HookAgentDx11: ovl_v2_mismatch seq=%llu bb=%ux%u canvas=%ux%u blocks=%zu\n",
                    static_cast<unsigned long long>(rt.lastOverlayV2Seq),
                    static_cast<unsigned int>(rt.backBufferWidth),
                    static_cast<unsigned int>(rt.backBufferHeight),
                    static_cast<unsigned int>(rt.overlayV2Header.canvasW),
                    static_cast<unsigned int>(rt.overlayV2Header.canvasH),
                    static_cast<std::size_t>(rt.overlayV2Blocks.size()));
                OutputDebugStringA(msg);
            }
            if (hasV2)
            {
                ImDrawList* bg = ImGui::GetBackgroundDrawList();
                ImDrawList* fg = ImGui::GetForegroundDrawList();
                const auto blobBytes = rt.overlayV2TextBlob.size();
                std::size_t traceVisibleBlocks = 0;
                std::size_t traceTextDrawBlocks = 0;
                std::size_t traceSkipSize = 0;
                std::size_t traceSkipBlob = 0;
                std::size_t traceOffscreen = 0;
                std::size_t traceRoiPreviewBlocks = 0;
                bool traceFirstRoiRectSet = false;
                float traceFirstRoiX = 0.0f;
                float traceFirstRoiY = 0.0f;
                float traceFirstRoiW = 0.0f;
                float traceFirstRoiH = 0.0f;
                bool traceFirstRectSet = false;
                float traceFirstX = 0.0f;
                float traceFirstY = 0.0f;
                float traceFirstW = 0.0f;
                float traceFirstH = 0.0f;
                std::vector<const ht::hook::ipc::OverlayTextBlockV2*> roiPreviewBlocks;

                for (std::size_t i = 0; i < rt.overlayV2Blocks.size(); i++)
                {
                    const auto& b = rt.overlayV2Blocks[i];
                    if (b.w <= 1.0f || b.h <= 1.0f)
                    {
                        traceSkipSize++;
                        continue;
                    }

                    traceVisibleBlocks++;
                    if (!traceFirstRectSet)
                    {
                        traceFirstRectSet = true;
                        traceFirstX = b.x;
                        traceFirstY = b.y;
                        traceFirstW = b.w;
                        traceFirstH = b.h;
                    }

                    if ((b.x + b.w) <= 0.0f || (b.y + b.h) <= 0.0f || b.x >= io.DisplaySize.x || b.y >= io.DisplaySize.y)
                    {
                        traceOffscreen++;
                    }

                    const bool isRoiPreview = (b.textLen == 0 && b.wrap == 2u);
                    if (isRoiPreview)
                    {
                        // NOTE: Wrap=2 + empty text is reserved for ROI preview border blocks from WPF ROI selector.
                        roiPreviewBlocks.push_back(&b);
                        traceRoiPreviewBlocks++;
                        if (!traceFirstRoiRectSet)
                        {
                            traceFirstRoiRectSet = true;
                            traceFirstRoiX = b.x;
                            traceFirstRoiY = b.y;
                            traceFirstRoiW = b.w;
                            traceFirstRoiH = b.h;
                        }
                        continue;
                    }

                    const float pad = std::max(0.0f, b.paddingPx);
                    const float rounding = std::max(0.0f, b.roundingPx);
                    const float baseFontPx = ImGui::GetFontSize();
                    const float desiredFontPx = (ignoreFontPx || b.fontPx <= 0.0f) ? 0.0f : b.fontPx;
                    ImFont* selectedFont = SelectOverlayFontLocked(rt, desiredFontPx);
                    const float selectedFontPx = (selectedFont != nullptr) ? selectedFont->LegacySize : baseFontPx;
                    const float innerW = std::max(1.0f, b.w - (pad * 2.0f));
                    const float innerH = std::max(1.0f, b.h - (pad * 2.0f));
                    if (i == 0)
                    {
                        rt.lastBlock0DesiredFontPx = (desiredFontPx > 0.0f) ? desiredFontPx : baseFontPx;
                        rt.lastBlock0SelectedFontPx = selectedFontPx;
                    }

                    const ImVec2 p0(b.x, b.y);
                    const ImVec2 p1(b.x + b.w, b.y + b.h);
                    const std::uint32_t bgArgb = forceOpaqueBg ? ForceAlpha(b.bgArgb, 0xFFu) : b.bgArgb;
                    bg->AddRectFilled(p0, p1, ArgbToImU32(bgArgb), rounding);

                    if (b.textLen == 0)
                    {
                        continue;
                    }
                    if (skipText)
                    {
                        continue;
                    }

                    const std::size_t off = static_cast<std::size_t>(b.textOffset);
                    const std::size_t len = static_cast<std::size_t>(b.textLen);
                    if (off >= blobBytes || len > blobBytes || off + len > blobBytes)
                    {
                        traceSkipBlob++;
                        continue;
                    }

                    traceTextDrawBlocks++;

                    const char* textBegin = nullptr;
                    const char* textEnd = nullptr;
                    const char* forced = "OVL ASCII TEST: overlay-only";
                    if (forceAsciiText)
                    {
                        textBegin = forced;
                        textEnd = forced + std::strlen(forced);
                    }
                    else
                    {
                        textBegin = reinterpret_cast<const char*>(rt.overlayV2TextBlob.data() + off);
                        textEnd = textBegin + len;
                    }

                    if (drawTextViaDrawList)
                    {
                        // WHY: Use DrawList directly to avoid per-window FontScale/layout edge cases.
                        const ImVec2 pos(b.x + pad, b.y + pad);
                        const float wrapW = (b.wrap != 0) ? innerW : 0.0f;
                        const ImVec4 clip(pos.x, pos.y, pos.x + innerW, pos.y + innerH);
                        fg->AddText(
                            selectedFont != nullptr ? selectedFont : ImGui::GetFont(),
                            selectedFontPx,
                            pos,
                            ArgbToImU32(b.fgArgb),
                            textBegin,
                            textEnd,
                            wrapW,
                            &clip);
                        continue;
                    }

                    ImGui::SetNextWindowPos(ImVec2(b.x + pad, b.y + pad));
                    ImGui::SetNextWindowSize(ImVec2(innerW, innerH));
                    ImGui::SetNextWindowBgAlpha(0.0f);

                    // WHY: Overlay is display-only. Disable inputs and avoid saving any ImGui ini state.
                    const ImGuiWindowFlags flags =
                        ImGuiWindowFlags_NoDecoration |
                        ImGuiWindowFlags_NoBackground |
                        ImGuiWindowFlags_NoSavedSettings |
                        ImGuiWindowFlags_NoMove |
                        ImGuiWindowFlags_NoResize |
                        ImGuiWindowFlags_NoNav |
                        ImGuiWindowFlags_NoInputs |
                        ImGuiWindowFlags_NoBringToFrontOnFocus;

                    char name[64]{};
                    std::snprintf(name, sizeof(name), "##ht_ovl_v2_%zu", i);
                    // WHY: Even in fallback path, force border off to avoid title-dependent white outlines.
                    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 0.0f);
                    ImGui::PushStyleColor(ImGuiCol_Border, ImVec4(0.0f, 0.0f, 0.0f, 0.0f));
                    if (ImGui::Begin(name, nullptr, flags))
                    {
                        // WHY: Prefer selecting a real font size over SetWindowFontScale to avoid visual flicker.
                        if (selectedFont != nullptr)
                        {
                            ImGui::PushFont(selectedFont);
                        }

                        ImGui::PushStyleColor(ImGuiCol_Text, ArgbToImVec4(b.fgArgb));
                        if (b.wrap != 0)
                        {
                            ImGui::PushTextWrapPos(0.0f);
                        }

                        ImGui::TextUnformatted(textBegin, textEnd);

                        if (b.wrap != 0)
                        {
                            ImGui::PopTextWrapPos();
                        }
                        ImGui::PopStyleColor();
                        if (selectedFont != nullptr)
                        {
                            ImGui::PopFont();
                        }
                    }
                    ImGui::End();
                    ImGui::PopStyleColor();
                    ImGui::PopStyleVar();
                }

                // WHY: Draw ROI preview last so it stays on top of translation overlays.
                for (const auto* rb : roiPreviewBlocks)
                {
                    if (rb == nullptr)
                    {
                        continue;
                    }

                    const float stroke = std::max(1.0f, rb->paddingPx);
                    const float inset = stroke * 0.5f;
                    const float rounding = std::max(0.0f, rb->roundingPx);
                    const ImVec2 p0(rb->x + inset, rb->y + inset);
                    const ImVec2 p1(rb->x + rb->w - inset, rb->y + rb->h - inset);
                    fg->AddRect(p0, p1, ArgbToImU32(rb->fgArgb), rounding, 0, stroke);
                }

                if (overlayTrace && rt.lastOverlayTraceDrawSeq != rt.lastOverlayV2Seq)
                {
                    const float scaleX = (rt.overlayV2Header.canvasW > 0)
                        ? (static_cast<float>(rt.backBufferWidth) / static_cast<float>(rt.overlayV2Header.canvasW))
                        : 0.0f;
                    const float scaleY = (rt.overlayV2Header.canvasH > 0)
                        ? (static_cast<float>(rt.backBufferHeight) / static_cast<float>(rt.overlayV2Header.canvasH))
                        : 0.0f;

                    char msg[640]{};
                    std::snprintf(
                        msg,
                        sizeof(msg),
                        "HT HookAgentDx11: ovl_v2_draw seq=%llu bb=%ux%u canvas=%ux%u scale=[%.3f,%.3f] blocks=%zu visible=%zu text=%zu roi=%zu skipSize=%zu skipBlob=%zu offscreen=%zu drawList=%u first=[%.1f,%.1f,%.1f,%.1f] roi_first=[%.1f,%.1f,%.1f,%.1f]\n",
                        static_cast<unsigned long long>(rt.lastOverlayV2Seq),
                        static_cast<unsigned int>(rt.backBufferWidth),
                        static_cast<unsigned int>(rt.backBufferHeight),
                        static_cast<unsigned int>(rt.overlayV2Header.canvasW),
                        static_cast<unsigned int>(rt.overlayV2Header.canvasH),
                        static_cast<double>(scaleX),
                        static_cast<double>(scaleY),
                        static_cast<std::size_t>(rt.overlayV2Blocks.size()),
                        traceVisibleBlocks,
                        traceTextDrawBlocks,
                        traceRoiPreviewBlocks,
                        traceSkipSize,
                        traceSkipBlob,
                        traceOffscreen,
                        static_cast<unsigned int>(drawTextViaDrawList ? 1 : 0),
                        static_cast<double>(traceFirstX),
                        static_cast<double>(traceFirstY),
                        static_cast<double>(traceFirstW),
                        static_cast<double>(traceFirstH),
                        static_cast<double>(traceFirstRoiX),
                        static_cast<double>(traceFirstRoiY),
                        static_cast<double>(traceFirstRoiW),
                        static_cast<double>(traceFirstRoiH));
                    OutputDebugStringA(msg);
                    rt.lastOverlayTraceDrawSeq = rt.lastOverlayV2Seq;
                }
            }
            else
            {
                // WHY: Replace persistent fallback panel with a short attach-success indicator to avoid obstructing
                // gameplay when no overlay text is published yet.
                if (!rt.hookSuccessIndicatorDone)
                {
                    if (rt.hookSuccessIndicatorArmed && rt.hookSuccessIndicatorStartQpc == 0)
                    {
                        rt.hookSuccessIndicatorStartQpc = now;
                        rt.hookSuccessIndicatorArmed = false;
                    }

                    if (rt.hookSuccessIndicatorStartQpc != 0 && rt.qpcFreq != 0)
                    {
                        const auto elapsedQpc = now - rt.hookSuccessIndicatorStartQpc;
                        const auto elapsedMs = static_cast<std::uint64_t>(
                            (elapsedQpc * 1000ull) / std::max<std::uint64_t>(1ull, rt.qpcFreq));
                        if (elapsedMs >= kHookSuccessIndicatorDurationMs)
                        {
                            rt.hookSuccessIndicatorDone = true;
                        }
                        else
                        {
                            float alpha = 1.0f;
                            if (elapsedMs < kHookSuccessIndicatorFadeInMs)
                            {
                                alpha = static_cast<float>(elapsedMs) /
                                    static_cast<float>(std::max<std::uint64_t>(1ull, kHookSuccessIndicatorFadeInMs));
                            }
                            else if (elapsedMs > (kHookSuccessIndicatorDurationMs - kHookSuccessIndicatorFadeOutMs))
                            {
                                const auto tailMs = kHookSuccessIndicatorDurationMs - elapsedMs;
                                alpha = static_cast<float>(tailMs) /
                                    static_cast<float>(std::max<std::uint64_t>(1ull, kHookSuccessIndicatorFadeOutMs));
                            }
                            alpha = std::max(0.0f, std::min(alpha, 1.0f));

                            const int plateA = static_cast<int>(std::lround(170.0f * alpha));
                            const int ringA = static_cast<int>(std::lround(235.0f * alpha));
                            const int tickA = static_cast<int>(std::lround(245.0f * alpha));

                            const ImVec2 c(34.0f, 34.0f);
                            ImDrawList* fg = ImGui::GetForegroundDrawList();
                            fg->AddCircleFilled(c, 16.0f, IM_COL32(16, 16, 16, plateA), 24);
                            fg->AddCircle(c, 15.0f, IM_COL32(83, 214, 108, ringA), 24, 2.0f);
                            fg->AddLine(ImVec2(c.x - 6.0f, c.y + 0.5f), ImVec2(c.x - 1.5f, c.y + 5.5f), IM_COL32(255, 255, 255, tickA), 2.4f);
                            fg->AddLine(ImVec2(c.x - 1.5f, c.y + 5.5f), ImVec2(c.x + 8.0f, c.y - 5.0f), IM_COL32(255, 255, 255, tickA), 2.4f);
                        }
                    }
                }
            }
            }

            if (debugMode)
            {
                // WHY: Diagnose IPC coordinate instability by showing recent (seq, x/y/w/h) samples on-screen.
                ImGui::SetNextWindowPos(ImVec2(10.0f, 10.0f), ImGuiCond_Always);
                ImGui::SetNextWindowBgAlpha(0.45f);
                const ImGuiWindowFlags flags =
                    ImGuiWindowFlags_NoDecoration |
                    ImGuiWindowFlags_NoSavedSettings |
                    ImGuiWindowFlags_NoMove |
                    ImGuiWindowFlags_NoNav |
                    ImGuiWindowFlags_NoInputs |
                    ImGuiWindowFlags_AlwaysAutoResize;
                if (ImGui::Begin("##ht_ovl_dbg", nullptr, flags))
                {
                    ImGui::Text("HT OVL DEBUG");
                    ImGui::Text("present_kind=%u active=%u", rt.lastPresentKind, rt.activePresentKind);
                    ImGui::Text("bb=%ux%u canvas=%ux%u", rt.backBufferWidth, rt.backBufferHeight, rt.overlayV2Header.canvasW, rt.overlayV2Header.canvasH);
                    ImGui::Text("v2 seq=%llu blocks=%u text=%u",
                        static_cast<unsigned long long>(rt.lastOverlayV2Seq),
                        static_cast<unsigned int>(rt.overlayV2Blocks.size()),
                        static_cast<unsigned int>(rt.overlayV2TextBlob.size()));
                    ImGui::Text(
                        "flags: opaque_bg=%s skip_text=%s ascii_text=%s",
                        forceOpaqueBg ? "on" : "off",
                        skipText ? "on" : "off",
                        forceAsciiText ? "on" : "off");
                    ImGui::Text(
                        "text_path: drawlist=%s ignore_font_px=%s",
                        drawTextViaDrawList ? "on" : "off",
                        ignoreFontPx ? "on" : "off");
                    ImGui::Text(
                        "block0 font: desired=%.1f selected=%.1f steps=%u",
                        rt.lastBlock0DesiredFontPx,
                        rt.lastBlock0SelectedFontPx,
                        rt.overlayFonts.count);

                    // Present/swapchain diagnostics: detect alternating swapchains or unexpected swap effects.
                    ImGui::Separator();
                    ImGui::Text("present samples (latest first):");
                    const std::uint32_t pCount = rt.presentDebugCount;
                    const std::uint32_t pLast = (rt.presentDebugNext == 0) ? 0 : (rt.presentDebugNext - 1);
                    for (std::uint32_t i = 0; i < std::min<std::uint32_t>(pCount, 6u); i++)
                    {
                        const std::uint32_t idx = (pLast + kPresentDebugSamples - i) % kPresentDebugSamples;
                        const auto& p = rt.presentDebug[idx];
                        ImGui::Text(
                            "k=%u tid=%u swap=0x%016" PRIXPTR " bc=%u eff=%u flg=0x%X bb=%ux%u ovl=%llu",
                            p.kind,
                            p.tid,
                            p.swapPtr,
                            p.scBufferCount,
                            p.scSwapEffect,
                            p.scFlags,
                            p.bbW,
                            p.bbH,
                            static_cast<unsigned long long>(p.ovlSeq));
                    }

                    ImGui::Separator();
                    const std::uint32_t count = rt.overlayV2DebugCount;
                    const std::uint32_t last = (rt.overlayV2DebugNext == 0) ? 0 : (rt.overlayV2DebugNext - 1);
                    for (std::uint32_t i = 0; i < std::min<std::uint32_t>(count, 6u); i++)
                    {
                        const std::uint32_t idx = (last + kOverlayV2DebugSamples - i) % kOverlayV2DebugSamples;
                        const auto& s = rt.overlayV2Debug[idx];
                        ImGui::Text("[%llu] x=%.2f y=%.2f w=%.2f h=%.2f",
                            static_cast<unsigned long long>(s.seq),
                            s.x, s.y, s.w, s.h);
                    }

                    ImGui::Separator();
                    ImGui::Text(
                        "OM oldRTV=0x%016" PRIXPTR " oldDSV=0x%016" PRIXPTR,
                        rt.lastOmOldRtvPtr,
                        rt.lastOmOldDsvPtr);
                    ImGui::Text(
                        "ImGui targetRTV=0x%016" PRIXPTR " (use_old=%s)",
                        rt.lastImGuiTargetRtvPtr,
                        rt.lastImGuiUsedOldRtv ? "yes" : "no");
                    ImGui::Text(
                        "capture src: mode=%u used_om=%s om_eq_getbuf=%s",
                        rt.lastCaptureSourceMode,
                        rt.lastCaptureUsedOmRtv ? "yes" : "no",
                        rt.lastCaptureOmMatchesGetBuffer ? "yes" : "no");
                    ImGui::Text(
                        "capture tex: selected=0x%016" PRIXPTR " getbuf=0x%016" PRIXPTR,
                        rt.lastCaptureSelectedTexPtr,
                        rt.lastCaptureGetBufferTexPtr);
                    ImGui::Text(
                        "capture om: rtv=0x%016" PRIXPTR " tex=0x%016" PRIXPTR,
                        rt.lastCaptureOmRtvPtr,
                        rt.lastCaptureOmTexPtr);
                }
                ImGui::End();
            }

            ImGui::Render();

            // NOTE: Some titles may not have the backbuffer bound at Present. Bind it temporarily and restore.
            ID3D11RenderTargetView* oldRtv = nullptr;
            ID3D11DepthStencilView* oldDsv = nullptr;
            rt.context->OMGetRenderTargets(1, &oldRtv, &oldDsv);

            // WHY: In flip model swapchains, the game may rotate render targets. At Present time the RTV currently
            // bound (oldRtv) is the most reliable "this frame's" backbuffer. Prefer it when available.
            const bool haveOldRtv = (oldRtv != nullptr);
            ID3D11RenderTargetView* targetRtv = haveOldRtv ? oldRtv : rt.backBufferRtv;
            ID3D11DepthStencilView* targetDsv = haveOldRtv ? oldDsv : nullptr;

            rt.lastOmOldRtvPtr = reinterpret_cast<std::uintptr_t>(oldRtv);
            rt.lastOmOldDsvPtr = reinterpret_cast<std::uintptr_t>(oldDsv);
            rt.lastImGuiTargetRtvPtr = reinterpret_cast<std::uintptr_t>(targetRtv);
            rt.lastImGuiUsedOldRtv = haveOldRtv;

            ID3D11RenderTargetView* rtvs[1] = {targetRtv};
            rt.context->OMSetRenderTargets(1, rtvs, targetDsv);
            ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());

            rt.context->OMSetRenderTargets(1, &oldRtv, oldDsv);
            if (oldRtv != nullptr)
            {
                oldRtv->Release();
            }
            if (oldDsv != nullptr)
            {
                oldDsv->Release();
            }
        }

        CaptureSlot* FindFreeCaptureSlotLocked(Dx11Runtime& rt)
        {
            for (auto& slot : rt.captureRing)
            {
                if (slot.state == CaptureSlotState::Free && slot.texture != nullptr)
                {
                    return &slot;
                }
            }

            return nullptr;
        }

        CaptureSlot* FindOldestPendingCaptureSlotLocked(Dx11Runtime& rt)
        {
            CaptureSlot* best = nullptr;
            for (auto& slot : rt.captureRing)
            {
                if (slot.state != CaptureSlotState::Pending || slot.texture == nullptr)
                {
                    continue;
                }

                if (best == nullptr || slot.sourceFrameSeq < best->sourceFrameSeq)
                {
                    best = &slot;
                }
            }

            return best;
        }

        bool TryPublishPendingCaptureLocked(Dx11Runtime& rt, std::uint64_t nowQpc, CapturePerfBreakdown& perf)
        {
            auto* slot = FindOldestPendingCaptureSlotLocked(rt);
            if (slot == nullptr || slot->texture == nullptr || rt.context == nullptr)
            {
                return false;
            }

            const auto mapStartQpc = NowQpc();
            D3D11_MAPPED_SUBRESOURCE mapped{};
            const HRESULT mapHr = rt.context->Map(slot->texture, 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
            perf.mapQpc += (NowQpc() - mapStartQpc);
            if (mapHr == DXGI_ERROR_WAS_STILL_DRAWING)
            {
                return false;
            }
            if (mapHr != S_OK || mapped.pData == nullptr)
            {
                if (mapHr == S_OK)
                {
                    rt.context->Unmap(slot->texture, 0);
                }
                slot->state = CaptureSlotState::Free;
                slot->issuedQpc = 0;
                slot->sourceFrameSeq = 0;
                return false;
            }

            bool rgbaNeedsSwap = false;
            if (!TryResolveCaptureFormat(slot->format, rgbaNeedsSwap))
            {
                rt.context->Unmap(slot->texture, 0);
                slot->state = CaptureSlotState::Free;
                slot->issuedQpc = 0;
                slot->sourceFrameSeq = 0;
                return false;
            }

            slot->mapped = mapped;
            slot->mappedValid = true;
            slot->state = CaptureSlotState::ReadyToPublish;

            const bool enqueued = EnqueuePublishRequestLocked(
                rt,
                Dx11PublishRequest
                {
                    slot,
                    GetCurrentProcessId(),
                    nowQpc,
                });
            if (!enqueued)
            {
                rt.context->Unmap(slot->texture, 0);
                slot->mapped = {};
                slot->mappedValid = false;
                slot->state = CaptureSlotState::Free;
                slot->issuedQpc = 0;
                slot->sourceFrameSeq = 0;
                return false;
            }

            perf.published = true;
            return true;
        }

        bool TryIssueCaptureCopyLocked(Dx11Runtime& rt, ID3D11Texture2D* captureTex, std::uint64_t nowQpc, CapturePerfBreakdown& perf)
        {
            auto* freeSlot = FindFreeCaptureSlotLocked(rt);
            if (freeSlot == nullptr || freeSlot->texture == nullptr)
            {
                perf.slotBusy = true;
                return false;
            }

            const auto copyStartQpc = NowQpc();
            rt.context->CopyResource(freeSlot->texture, captureTex);
            perf.copyQpc += (NowQpc() - copyStartQpc);
            rt.lastCaptureIssueQpc = nowQpc;
            freeSlot->issuedQpc = nowQpc;
            freeSlot->sourceFrameSeq = ++rt.captureSourceFrameSeq;
            freeSlot->state = CaptureSlotState::Pending;
            perf.issued = true;
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

        bool CaptureAndShareFrameLocked(Dx11Runtime& rt, IDXGISwapChain* swap, CapturePerfBreakdown* perfOut = nullptr)
        {
            CapturePerfBreakdown perf{};
            const auto captureStartQpc = NowQpc();
            RefreshConfigLocked(rt);

            if (!EnsureDeviceLocked(rt, swap))
            {
                if (perfOut != nullptr)
                {
                    perf.totalQpc = NowQpc() - captureStartQpc;
                    *perfOut = perf;
                }
                return false;
            }

            CleanupCompletedCaptureSlotsLocked(rt);

            const std::uint32_t captureSourceMode = ReadEnvU32(L"HT_HOOK_CAPTURE_FROM_OM_RTV", 0);
            const bool disableDelayedReadback = ReadEnvU32(L"HT_HOOK_DISABLE_DELAYED_READBACK", 0) != 0;
            const auto now = NowQpc();

            ID3D11Texture2D* getBufferTex = nullptr;
            const HRESULT hr = swap->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&getBufferTex));
            if (hr != S_OK || getBufferTex == nullptr)
            {
                if (perfOut != nullptr)
                {
                    perf.totalQpc = NowQpc() - captureStartQpc;
                    *perfOut = perf;
                }
                return false;
            }

            ID3D11Texture2D* omTex = nullptr;
            ID3D11RenderTargetView* omRtv = nullptr;
            ID3D11DepthStencilView* omDsv = nullptr;
            if (captureSourceMode != 0)
            {
                rt.context->OMGetRenderTargets(1, &omRtv, &omDsv);
                if (omRtv != nullptr)
                {
                    ID3D11Resource* omRes = nullptr;
                    omRtv->GetResource(&omRes);
                    if (omRes != nullptr)
                    {
                        (void)omRes->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&omTex));
                        omRes->Release();
                    }
                }
            }

            ID3D11Texture2D* captureTex = getBufferTex;
            if (captureSourceMode == 1)
            {
                // WHY: In flip-model swapchains, OM-bound RTV is often closer to "current present target".
                if (omTex != nullptr)
                {
                    captureTex = omTex;
                }
            }
            else if (captureSourceMode >= 2)
            {
                // WHY: Strict mode for diagnosis. If OM RTV is unavailable, fail instead of silently falling back.
                if (omTex == nullptr)
                {
                    if (omRtv != nullptr)
                    {
                        omRtv->Release();
                    }
                    if (omDsv != nullptr)
                    {
                        omDsv->Release();
                    }
                    getBufferTex->Release();
                    if (perfOut != nullptr)
                    {
                        perf.totalQpc = NowQpc() - captureStartQpc;
                        *perfOut = perf;
                    }
                    return false;
                }
                captureTex = omTex;
            }

            rt.lastCaptureSourceMode = captureSourceMode;
            rt.lastCaptureSelectedTexPtr = reinterpret_cast<std::uintptr_t>(captureTex);
            rt.lastCaptureGetBufferTexPtr = reinterpret_cast<std::uintptr_t>(getBufferTex);
            rt.lastCaptureOmRtvPtr = reinterpret_cast<std::uintptr_t>(omRtv);
            rt.lastCaptureOmTexPtr = reinterpret_cast<std::uintptr_t>(omTex);
            rt.lastCaptureUsedOmRtv = (captureTex == omTex && omTex != nullptr);
            rt.lastCaptureOmMatchesGetBuffer = (omTex != nullptr && omTex == getBufferTex);

            if (omRtv != nullptr)
            {
                omRtv->Release();
            }
            if (omDsv != nullptr)
            {
                omDsv->Release();
            }

            const bool okRing = EnsureCaptureRingLocked(rt, captureTex);
            if (!okRing)
            {
                if (omTex != nullptr && omTex != getBufferTex)
                {
                    omTex->Release();
                }
                getBufferTex->Release();
                if (perfOut != nullptr)
                {
                    perf.totalQpc = NowQpc() - captureStartQpc;
                    *perfOut = perf;
                }
                return false;
            }

            bool published = false;
            if (disableDelayedReadback)
            {
                // WHY: Keep a direct path available for diagnosis so ring behavior can be compared against legacy sync readback.
                auto* slot = FindFreeCaptureSlotLocked(rt);
                if (slot != nullptr && slot->texture != nullptr)
                {
                    const auto copyStartQpc = NowQpc();
                    rt.context->CopyResource(slot->texture, captureTex);
                    perf.copyQpc += (NowQpc() - copyStartQpc);
                    slot->issuedQpc = now;
                    slot->sourceFrameSeq = ++rt.captureSourceFrameSeq;
                    slot->state = CaptureSlotState::Pending;
                    rt.lastCaptureIssueQpc = now;
                    perf.issued = true;

                    D3D11_MAPPED_SUBRESOURCE mapped{};
                    const auto mapStartQpc = NowQpc();
                    const HRESULT mapHr = rt.context->Map(slot->texture, 0, D3D11_MAP_READ, 0, &mapped);
                    perf.mapQpc += (NowQpc() - mapStartQpc);
                    if (mapHr == S_OK && mapped.pData != nullptr)
                    {
                        const bool isBgra8 = (slot->format == DXGI_FORMAT_B8G8R8A8_UNORM || slot->format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB);
                        const bool isRgba8 = (slot->format == DXGI_FORMAT_R8G8B8A8_UNORM || slot->format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB);
                        if (isBgra8 || isRgba8)
                        {
                            const std::uint32_t width = slot->width;
                            const std::uint32_t height = slot->height;
                            const std::uint32_t stride = static_cast<std::uint32_t>(mapped.RowPitch);
                            const std::size_t payloadBytes = static_cast<std::size_t>(stride) * static_cast<std::size_t>(height);
                            rt.scratch.resize(payloadBytes);
                            std::memcpy(rt.scratch.data(), mapped.pData, payloadBytes);
                            rt.context->Unmap(slot->texture, 0);

                            if (isRgba8)
                            {
                                const std::uint32_t rowBytes = width * 4;
                                for (std::uint32_t y = 0; y < height; y++)
                                {
                                    auto* row = rt.scratch.data() + (static_cast<std::size_t>(y) * stride);
                                    for (std::uint32_t x = 0; x < rowBytes; x += 4)
                                    {
                                        std::swap(row[x + 0], row[x + 2]);
                                    }
                                }
                            }

                            const DWORD pid = GetCurrentProcessId();
                            const auto frameId = rt.frameId.fetch_add(1, std::memory_order_relaxed) + 1;
                            published = rt.frameWriter.WriteFrame(
                                pid,
                                ht::hook::ipc::GraphicsApi::Dx11,
                                frameId,
                                width,
                                height,
                                stride,
                                now,
                                rt.scratch.data(),
                                rt.scratch.size());
                            if (published)
                            {
                                rt.lastCaptureQpc.store(now, std::memory_order_release);
                                perf.published = true;
                            }
                        }
                        else
                        {
                            rt.context->Unmap(slot->texture, 0);
                        }
                    }
                    slot->state = CaptureSlotState::Free;
                    slot->issuedQpc = 0;
                    slot->sourceFrameSeq = 0;
                }
                else
                {
                    perf.slotBusy = true;
                }
            }
            else
            {
                if (!TryPublishPendingCaptureLocked(rt, now, perf))
                {
                    if (FindOldestPendingCaptureSlotLocked(rt) != nullptr)
                    {
                        perf.mapDeferred = true;
                    }
                }

                const bool captureDue =
                    rt.captureIntervalQpc == 0 ||
                    rt.lastCaptureIssueQpc == 0 ||
                    (now - rt.lastCaptureIssueQpc) >= rt.captureIntervalQpc;
                if (captureDue)
                {
                    (void)TryIssueCaptureCopyLocked(rt, captureTex, now, perf);
                }
            }

            if (omTex != nullptr && omTex != getBufferTex)
            {
                omTex->Release();
            }
            getBufferTex->Release();
            perf.totalQpc = NowQpc() - captureStartQpc;
            if (perfOut != nullptr)
            {
                *perfOut = perf;
            }
            return published || perf.issued || perf.mapDeferred || !perf.slotBusy;
        }

        HRESULT HookedPresentLocked(IDXGISwapChain* swap, UINT syncInterval, UINT flags, std::uint64_t lockWaitQpc)
        {
            const auto totalStartQpc = NowQpc();
            PresentPerfSample perfSample{};
            perfSample.kind = 1;
            perfSample.lockWaitQpc = lockWaitQpc;

            const bool disablePresentDebug = ReadEnvU32(L"HT_HOOK_DISABLE_PRESENT_DEBUG", 0) != 0;
            const bool disableCapture = ReadEnvU32(L"HT_HOOK_DISABLE_CAPTURE", 0) != 0;
            const bool disableOverlayRefresh = ReadEnvU32(L"HT_HOOK_DISABLE_OVL_REFRESH", 0) != 0;
            const bool disableOverlayDraw =
                ReadEnvU32(L"HT_HOOK_DISABLE_DRAW", 0) != 0 ||
                ReadEnvU32(L"HT_HOOK_OVL_DISABLE_ALL_DRAW", 0) != 0;
            const bool disableStatus = ReadEnvU32(L"HT_HOOK_DISABLE_STATUS", 0) != 0;

            if (g_rt.installed.load(std::memory_order_acquire) && swap != nullptr)
            {
                if (g_rt.activePresentKind == 0)
                {
                    // WHY: Some titles call both Present and Present1 (not necessarily nested). If we draw on both,
                    // we can render two different overlay snapshots within the same frame, which looks like
                    // offset/ghosted text. Stick to the first present kind we observe unless overridden.
                    g_rt.activePresentKind = 1;
                }
                if (g_rt.activePresentKind == 1)
                {
                    if (!disablePresentDebug)
                    {
                        const auto debugStartQpc = NowQpc();
                        RecordPresentDebugLocked(g_rt, swap, 1);
                        perfSample.debugRecordQpc = NowQpc() - debugStartQpc;
                    }

                    g_rt.presentCount++;
                    g_rt.lastPresentQpc = NowQpc();
                    g_rt.lastPresentKind = 1;

                    if (!disableCapture)
                    {
                        const auto captureStartQpc = NowQpc();
                        CapturePerfBreakdown capturePerf{};
                        (void)CaptureAndShareFrameLocked(g_rt, swap, &capturePerf);
                        perfSample.captureQpc = NowQpc() - captureStartQpc;
                        perfSample.captureCopyQpc = capturePerf.copyQpc;
                        perfSample.captureMapQpc = capturePerf.mapQpc;
                        perfSample.capturePublished = capturePerf.published ? 1u : 0u;
                        perfSample.captureSlotBusy = capturePerf.slotBusy ? 1u : 0u;
                        perfSample.captureMapDeferred = capturePerf.mapDeferred ? 1u : 0u;
                        perfSample.captureIssued = capturePerf.issued ? 1u : 0u;
                    }

                    if (!disableOverlayRefresh)
                    {
                        const auto overlayRefreshStartQpc = NowQpc();
                        (void)RefreshOverlayV2Locked(g_rt);
                        perfSample.overlayRefreshQpc = NowQpc() - overlayRefreshStartQpc;
                    }

                    if (!disableOverlayDraw)
                    {
                        const auto overlayDrawStartQpc = NowQpc();
                        DrawImGuiOverlayV2Locked(g_rt, swap);
                        perfSample.overlayDrawQpc = NowQpc() - overlayDrawStartQpc;
                    }

                    if (!disableStatus)
                    {
                        const auto statusStartQpc = NowQpc();
                        PublishStatusLocked(g_rt);
                        perfSample.statusQpc = NowQpc() - statusStartQpc;
                    }
                }
            }

            const auto originalPresentStartQpc = NowQpc();
            const HRESULT result = g_rt.originalPresent ? g_rt.originalPresent(swap, syncInterval, flags) : S_OK;
            const auto endQpc = NowQpc();
            perfSample.originalPresentQpc = endQpc - originalPresentStartQpc;
            perfSample.totalQpc = endQpc - totalStartQpc;
            perfSample.lockHoldQpc = endQpc - totalStartQpc;
            RecordPresentPerfLocked(g_rt, perfSample);
            return result;
        }

        HRESULT HookedPresentLockingImpl(IDXGISwapChain* swap, UINT syncInterval, UINT flags)
        {
            const auto lockWaitStartQpc = NowQpc();
            std::lock_guard<std::mutex> lock(g_rt.mutex);
            const auto lockAcquiredQpc = NowQpc();
            return HookedPresentLocked(swap, syncInterval, flags, lockAcquiredQpc - lockWaitStartQpc);
        }

        HRESULT __stdcall HookedPresent(IDXGISwapChain* swap, UINT syncInterval, UINT flags)
        {
            if (g_presentDepth > 0)
            {
                return g_rt.originalPresent ? g_rt.originalPresent(swap, syncInterval, flags) : S_OK;
            }
            if (ReadEnvU32(L"HT_HOOK_PASS_THROUGH", 0) != 0)
            {
                // WHY: Pure pass-through mode isolates the cost of "being hooked" from all runtime bookkeeping.
                return g_rt.originalPresent ? g_rt.originalPresent(swap, syncInterval, flags) : S_OK;
            }
            g_presentDepth++;
            HRESULT result = S_OK;
            // WHY: Hook code must never crash the host process. SEH must live in a function without C++ unwinding.
            __try
            {
                result = HookedPresentLockingImpl(swap, syncInterval, flags);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                result = g_rt.originalPresent ? g_rt.originalPresent(swap, syncInterval, flags) : S_OK;
            }
            g_presentDepth--;
            return result;
        }

        HRESULT HookedPresent1Locked(
            IDXGISwapChain1* swap,
            UINT syncInterval,
            UINT flags,
            const DXGI_PRESENT_PARAMETERS* params,
            std::uint64_t lockWaitQpc)
        {
            const auto totalStartQpc = NowQpc();
            PresentPerfSample perfSample{};
            perfSample.kind = 2;
            perfSample.lockWaitQpc = lockWaitQpc;

            const bool disablePresentDebug = ReadEnvU32(L"HT_HOOK_DISABLE_PRESENT_DEBUG", 0) != 0;
            const bool disableCapture = ReadEnvU32(L"HT_HOOK_DISABLE_CAPTURE", 0) != 0;
            const bool disableOverlayRefresh = ReadEnvU32(L"HT_HOOK_DISABLE_OVL_REFRESH", 0) != 0;
            const bool disableOverlayDraw =
                ReadEnvU32(L"HT_HOOK_DISABLE_DRAW", 0) != 0 ||
                ReadEnvU32(L"HT_HOOK_OVL_DISABLE_ALL_DRAW", 0) != 0;
            const bool disableStatus = ReadEnvU32(L"HT_HOOK_DISABLE_STATUS", 0) != 0;

            if (g_rt.installed.load(std::memory_order_acquire) && swap != nullptr)
            {
                if (g_rt.activePresentKind == 0)
                {
                    g_rt.activePresentKind = 2;
                }
                if (g_rt.activePresentKind == 2)
                {
                    if (!disablePresentDebug)
                    {
                        const auto debugStartQpc = NowQpc();
                        RecordPresentDebugLocked(g_rt, swap, 2);
                        perfSample.debugRecordQpc = NowQpc() - debugStartQpc;
                    }

                    g_rt.presentCount++;
                    g_rt.lastPresentQpc = NowQpc();
                    g_rt.lastPresentKind = 2;

                    if (!disableCapture)
                    {
                        const auto captureStartQpc = NowQpc();
                        CapturePerfBreakdown capturePerf{};
                        (void)CaptureAndShareFrameLocked(g_rt, swap, &capturePerf);
                        perfSample.captureQpc = NowQpc() - captureStartQpc;
                        perfSample.captureCopyQpc = capturePerf.copyQpc;
                        perfSample.captureMapQpc = capturePerf.mapQpc;
                        perfSample.capturePublished = capturePerf.published ? 1u : 0u;
                        perfSample.captureSlotBusy = capturePerf.slotBusy ? 1u : 0u;
                        perfSample.captureMapDeferred = capturePerf.mapDeferred ? 1u : 0u;
                        perfSample.captureIssued = capturePerf.issued ? 1u : 0u;
                    }

                    if (!disableOverlayRefresh)
                    {
                        const auto overlayRefreshStartQpc = NowQpc();
                        (void)RefreshOverlayV2Locked(g_rt);
                        perfSample.overlayRefreshQpc = NowQpc() - overlayRefreshStartQpc;
                    }

                    if (!disableOverlayDraw)
                    {
                        const auto overlayDrawStartQpc = NowQpc();
                        DrawImGuiOverlayV2Locked(g_rt, swap);
                        perfSample.overlayDrawQpc = NowQpc() - overlayDrawStartQpc;
                    }

                    if (!disableStatus)
                    {
                        const auto statusStartQpc = NowQpc();
                        PublishStatusLocked(g_rt);
                        perfSample.statusQpc = NowQpc() - statusStartQpc;
                    }
                }
            }

            const auto originalPresentStartQpc = NowQpc();
            const HRESULT result = g_rt.originalPresent1 ? g_rt.originalPresent1(swap, syncInterval, flags, params) : S_OK;
            const auto endQpc = NowQpc();
            perfSample.originalPresentQpc = endQpc - originalPresentStartQpc;
            perfSample.totalQpc = endQpc - totalStartQpc;
            perfSample.lockHoldQpc = endQpc - totalStartQpc;
            RecordPresentPerfLocked(g_rt, perfSample);
            return result;
        }

        HRESULT HookedPresent1LockingImpl(IDXGISwapChain1* swap, UINT syncInterval, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
        {
            const auto lockWaitStartQpc = NowQpc();
            std::lock_guard<std::mutex> lock(g_rt.mutex);
            const auto lockAcquiredQpc = NowQpc();
            return HookedPresent1Locked(swap, syncInterval, flags, params, lockAcquiredQpc - lockWaitStartQpc);
        }

        HRESULT __stdcall HookedPresent1(IDXGISwapChain1* swap, UINT syncInterval, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
        {
            if (g_presentDepth > 0)
            {
                return g_rt.originalPresent1 ? g_rt.originalPresent1(swap, syncInterval, flags, params) : S_OK;
            }
            if (ReadEnvU32(L"HT_HOOK_PASS_THROUGH", 0) != 0)
            {
                return g_rt.originalPresent1 ? g_rt.originalPresent1(swap, syncInterval, flags, params) : S_OK;
            }
            g_presentDepth++;
            HRESULT result = S_OK;
            __try
            {
                result = HookedPresent1LockingImpl(swap, syncInterval, flags, params);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                result = g_rt.originalPresent1 ? g_rt.originalPresent1(swap, syncInterval, flags, params) : S_OK;
            }
            g_presentDepth--;
            return result;
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
        g_rt.hookSuccessIndicatorArmed = true;
        g_rt.hookSuccessIndicatorDone = false;
        g_rt.hookSuccessIndicatorStartQpc = 0;
        g_rt.frameId.store(0, std::memory_order_relaxed);
        g_rt.lastCaptureQpc.store(0, std::memory_order_relaxed);
        g_rt.activePresentKind = ReadEnvU32(L"HT_HOOK_PRESENT_KIND", 0);
        if (g_rt.activePresentKind != 0 && g_rt.activePresentKind != 1 && g_rt.activePresentKind != 2)
        {
            g_rt.activePresentKind = 0;
        }

        if (!EnsurePublishWorkerLocked(g_rt))
        {
            return false;
        }
        const bool publishWorkerStarted = true;

        void** vtable = nullptr;
        void** vtable1 = nullptr;
        if (!CreateDummySwapChainAndGetVtables(&vtable, &vtable1) || vtable == nullptr)
        {
            if (publishWorkerStarted)
            {
                StopPublishWorkerLocked(g_rt);
            }
            return false;
        }

        g_rt.presentTarget = vtable[kSwapChainPresentIndex];
        g_rt.resizeBuffersTarget = vtable[kSwapChainResizeBuffersIndex];
        g_rt.present1Target = (vtable1 != nullptr) ? vtable1[kSwapChain1Present1Index] : nullptr;

        const MH_STATUS init = MH_Initialize();
        if (init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED)
        {
            if (publishWorkerStarted)
            {
                StopPublishWorkerLocked(g_rt);
            }
            return false;
        }

        // WHY: Keep hooks explicitly paired with stored targets to support safe disable/remove at detach time.
        if (MH_CreateHook(g_rt.presentTarget, reinterpret_cast<LPVOID>(&HookedPresent), reinterpret_cast<LPVOID*>(&g_rt.originalPresent)) != MH_OK)
        {
            if (publishWorkerStarted)
            {
                StopPublishWorkerLocked(g_rt);
            }
            return false;
        }

        if (g_rt.present1Target != nullptr)
        {
            if (MH_CreateHook(g_rt.present1Target, reinterpret_cast<LPVOID>(&HookedPresent1), reinterpret_cast<LPVOID*>(&g_rt.originalPresent1)) != MH_OK)
            {
                (void)MH_RemoveHook(g_rt.presentTarget);
                if (publishWorkerStarted)
                {
                    StopPublishWorkerLocked(g_rt);
                }
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
            if (publishWorkerStarted)
            {
                StopPublishWorkerLocked(g_rt);
            }
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
            if (publishWorkerStarted)
            {
                StopPublishWorkerLocked(g_rt);
            }
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
                if (publishWorkerStarted)
                {
                    StopPublishWorkerLocked(g_rt);
                }
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
            if (publishWorkerStarted)
            {
                StopPublishWorkerLocked(g_rt);
            }
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

        StopPublishWorkerLocked(g_rt);
        ResetDeviceStateLocked(g_rt);
        g_rt.frameWriter.Reset();
        g_rt.configReader.Reset();
        g_rt.statusWriter.Reset();
        g_rt.scratch.clear();
        g_rt.publishScratch.clear();

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
        g_rt.frameId.store(0, std::memory_order_relaxed);
        g_rt.lastCaptureQpc.store(0, std::memory_order_relaxed);
        g_rt.lastOverlayTraceDrawSeq = 0;
        g_rt.lastOverlayCanvasMismatchSeq = 0;
        g_rt.hookSuccessIndicatorArmed = false;
        g_rt.hookSuccessIndicatorDone = false;
        g_rt.hookSuccessIndicatorStartQpc = 0;
        g_rt.presentPerfNext = 0;
        g_rt.presentPerfCount = 0;
        g_rt.lastPerfFlushQpc = 0;
        g_rt.diagFileSinkEnabled = false;
        CloseDiagFile();
    }
}

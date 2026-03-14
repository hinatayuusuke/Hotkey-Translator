#include "VulkanPresentHook.h"

#include <algorithm>
#include <atomic>
#include <array>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include <windows.h>

#include <vulkan/vulkan.h>

#include <MinHook.h>

#include <imgui.h>
#include <imgui_impl_vulkan.h>

#include "../HookCommon/SharedFrameWriter.h"
#include "../HookCommon/SharedHookConfig.h"
#include "../HookCommon/SharedHookStatus.h"
#include "../HookCommon/SharedOverlayV2.h"

namespace ht::hook::vulkan
{
    namespace
    {
        constexpr std::uint32_t kDefaultCaptureFps = 15u;
        constexpr std::uint32_t kMinCaptureFps = 1u;
        constexpr std::uint32_t kMaxCaptureFps = 240u;
        constexpr std::uint32_t kOverlayFontBasePx = 32u;
        constexpr std::uint32_t kOverlayMaxBlocks = 64u;
        constexpr std::uint64_t kDiagLogMinIntervalMs = 1000u;
        constexpr std::uint64_t kDiagSummaryIntervalMs = 2000u;
        constexpr std::uint64_t kHookSuccessIndicatorDurationMs = 1500u;
        constexpr std::uint64_t kHookSuccessIndicatorFadeInMs = 200u;
        constexpr std::uint64_t kHookSuccessIndicatorFadeOutMs = 300u;
#if defined(_WIN64)
        constexpr bool kEnableDirectDeviceExportHooks = true;
#else
        constexpr bool kEnableDirectDeviceExportHooks = false;
#endif

        enum class CaptureSkipReason : std::size_t
        {
            QueueNotFound = 0,
            SwapchainNotFound,
            SwapchainImagesMissing,
            DeviceNotFound,
            EnsureQueueGpuStateFailed,
            QueueStateNotFound,
            OverlaySwapchainStateFailed,
            OverlayStateMissing,
            OverlayFramebufferMissing,
            EnsureImGuiFailed,
            VkResetCommandPoolFailed,
            VkResetFencesFailed,
            VkBeginCommandBufferFailed,
            TargetImageNull,
            VkEndCommandBufferFailed,
            VkQueueSubmitFailed,
            VkWaitForFencesFailed,
            CaptureFormatUnsupported,
            VkMapMemoryFailed,
            WriteFrameFailed,
            Count
        };

        struct DeviceInfo
        {
            VkPhysicalDevice physicalDevice = VK_NULL_HANDLE;
        };

        struct QueueInfo
        {
            VkDevice device = VK_NULL_HANDLE;
            std::uint32_t familyIndex = std::numeric_limits<std::uint32_t>::max();
            bool valid = false;
        };

        struct SwapchainInfo
        {
            VkDevice device = VK_NULL_HANDLE;
            VkFormat format = VK_FORMAT_UNDEFINED;
            VkExtent2D extent{};
            std::vector<VkImage> images;
        };

        struct QueueGpuState
        {
            VkDevice device = VK_NULL_HANDLE;
            std::uint32_t queueFamily = std::numeric_limits<std::uint32_t>::max();
            VkCommandPool commandPool = VK_NULL_HANDLE;
            VkCommandBuffer commandBuffer = VK_NULL_HANDLE;
            VkFence fence = VK_NULL_HANDLE;

            VkBuffer stagingBuffer = VK_NULL_HANDLE;
            VkDeviceMemory stagingMemory = VK_NULL_HANDLE;
            void* stagingMapped = nullptr;
            bool stagingHostCached = false;
            VkDeviceSize stagingBytes = 0;
            std::uint32_t width = 0;
            std::uint32_t height = 0;
            VkFormat format = VK_FORMAT_UNDEFINED;
            std::vector<std::uint8_t> scratch;
        };

        struct OverlaySwapchainState
        {
            VkRenderPass renderPass = VK_NULL_HANDLE;
            std::vector<VkImageView> imageViews;
            std::vector<VkFramebuffer> framebuffers;
            VkFormat format = VK_FORMAT_UNDEFINED;
            std::uint32_t width = 0;
            std::uint32_t height = 0;
            std::uint32_t imageCount = 0;
        };

        struct VulkanRuntime
        {
            std::mutex mutex;
            std::atomic_bool installed{false};

            ipc::SharedFrameWriter frameWriter;
            ipc::SharedHookConfigReader configReader;
            ipc::SharedHookStatusWriter statusWriter;
            ipc::SharedOverlayV2Reader overlayV2Reader;

            PFN_vkGetDeviceProcAddr originalGetDeviceProcAddr = nullptr;
            PFN_vkGetInstanceProcAddr originalGetInstanceProcAddr = nullptr;
            PFN_vkCreateInstance originalCreateInstance = nullptr;
            PFN_vkDestroyInstance originalDestroyInstance = nullptr;
            PFN_vkCreateDevice originalCreateDevice = nullptr;
            PFN_vkDestroyDevice originalDestroyDevice = nullptr;
            PFN_vkGetDeviceQueue originalGetDeviceQueue = nullptr;
            PFN_vkGetDeviceQueue2 originalGetDeviceQueue2 = nullptr;
            PFN_vkCreateSwapchainKHR originalCreateSwapchainKHR = nullptr;
            PFN_vkDestroySwapchainKHR originalDestroySwapchainKHR = nullptr;
            PFN_vkAcquireNextImageKHR originalAcquireNextImageKHR = nullptr;
            PFN_vkAcquireNextImage2KHR originalAcquireNextImage2KHR = nullptr;
            PFN_vkGetSwapchainImagesKHR originalGetSwapchainImagesKHR = nullptr;
            PFN_vkQueuePresentKHR originalQueuePresentKHR = nullptr;

            VkInstance instance = VK_NULL_HANDLE;
            std::uint32_t apiVersion = VK_API_VERSION_1_0;

            std::unordered_map<VkDevice, DeviceInfo> devices;
            std::unordered_map<VkQueue, QueueInfo> queues;
            std::unordered_map<VkSwapchainKHR, SwapchainInfo> swapchains;
            std::unordered_map<VkQueue, QueueGpuState> queueGpuStates;
            std::unordered_map<VkSwapchainKHR, OverlaySwapchainState> overlaySwapchains;

            std::uint64_t qpcFreq = 0;
            std::uint64_t frameId = 0;
            std::uint64_t captureIntervalQpc = 0;
            std::uint64_t lastCaptureQpc = 0;
            std::uint64_t lastConfigQpc = 0;
            std::uint64_t lastFrameWriteQpc = 0;
            std::uint32_t configuredFpsLimit = kDefaultCaptureFps;
            bool overlayEnabled = false;
            bool perfDiagLogEnabled = false;

            std::uint64_t lastOverlayV2Seq = 0;
            std::uint64_t lastOverlayV2Qpc = 0;
            ipc::OverlayV2Header overlayV2Header{};
            std::vector<ipc::OverlayTextBlockV2> overlayV2Blocks;
            std::vector<std::uint8_t> overlayV2TextBlob;
            bool hookSuccessIndicatorArmed = false;
            bool hookSuccessIndicatorDone = false;
            std::uint64_t hookSuccessIndicatorStartQpc = 0;

            bool imguiInitialized = false;
            ImGuiContext* imguiContext = nullptr;
            ImFont* overlayFont = nullptr;
            VkDescriptorPool imguiDescriptorPool = VK_NULL_HANDLE;
            VkDevice imguiDevice = VK_NULL_HANDLE;
            VkSwapchainKHR imguiBoundSwapchain = VK_NULL_HANDLE;
            std::uint32_t imguiImageCount = 0;
            std::uint64_t lastImGuiQpc = 0;

            std::uint64_t presentCount = 0;
            std::uint64_t lastPresentQpc = 0;
            std::uint32_t lastPresentKind = 1;
            std::uint32_t lastFormat = 0;
            std::uint32_t lastWidth = 0;
            std::uint32_t lastHeight = 0;

            std::array<std::uint64_t, static_cast<std::size_t>(CaptureSkipReason::Count)> captureSkipLastLogQpc{};
            std::array<std::uint32_t, static_cast<std::size_t>(CaptureSkipReason::Count)> captureSkipPendingCount{};
            std::uint64_t lastPresentSummaryLogQpc = 0;
            std::uint64_t lastPresentPerfLogQpc = 0;
            std::uint64_t lastSwapchainEnsureFailQpc = 0;
            std::uint64_t lastGpuStateFailQpc = 0;
            std::uint64_t lastWriteFrameOkQpc = 0;
            std::uint64_t lastWriteFrameFailQpc = 0;
            std::uint64_t lastQueuePresentEnterLogQpc = 0;
        };

        VulkanRuntime g_rt;
        std::mutex g_diagFileMutex;
        HANDLE g_diagFileHandle = INVALID_HANDLE_VALUE;
        DWORD g_diagFilePid = 0;
        std::wstring g_diagFilePath;
        std::atomic_bool g_diagFileSinkEnabled{false};
        std::atomic_bool g_loggedFirstCreateDeviceHit{false};
        std::atomic_bool g_loggedFirstCreateSwapchainHit{false};

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
            return std::clamp(fps, kMinCaptureFps, kMaxCaptureFps);
        }

        std::uint64_t SwapchainHandleToLogValue(VkSwapchainKHR swapchain)
        {
            // WHY: Vulkan non-dispatchable handles are pointer-typed on 64-bit builds and integer-typed on 32-bit builds.
#if defined(VK_USE_64_BIT_PTR_DEFINES) && VK_USE_64_BIT_PTR_DEFINES
            return static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(swapchain));
#else
            return static_cast<std::uint64_t>(swapchain);
#endif
        }

        const char* CaptureSkipReasonToString(CaptureSkipReason reason)
        {
            switch (reason)
            {
                case CaptureSkipReason::QueueNotFound: return "queue_not_found";
                case CaptureSkipReason::SwapchainNotFound: return "swapchain_not_found";
                case CaptureSkipReason::SwapchainImagesMissing: return "swapchain_images_missing";
                case CaptureSkipReason::DeviceNotFound: return "device_not_found";
                case CaptureSkipReason::EnsureQueueGpuStateFailed: return "ensure_queue_gpu_state_failed";
                case CaptureSkipReason::QueueStateNotFound: return "queue_state_not_found";
                case CaptureSkipReason::OverlaySwapchainStateFailed: return "overlay_swapchain_state_failed";
                case CaptureSkipReason::OverlayStateMissing: return "overlay_state_missing";
                case CaptureSkipReason::OverlayFramebufferMissing: return "overlay_framebuffer_missing";
                case CaptureSkipReason::EnsureImGuiFailed: return "ensure_imgui_failed";
                case CaptureSkipReason::VkResetCommandPoolFailed: return "vk_reset_command_pool_failed";
                case CaptureSkipReason::VkResetFencesFailed: return "vk_reset_fences_failed";
                case CaptureSkipReason::VkBeginCommandBufferFailed: return "vk_begin_command_buffer_failed";
                case CaptureSkipReason::TargetImageNull: return "target_image_null";
                case CaptureSkipReason::VkEndCommandBufferFailed: return "vk_end_command_buffer_failed";
                case CaptureSkipReason::VkQueueSubmitFailed: return "vk_queue_submit_failed";
                case CaptureSkipReason::VkWaitForFencesFailed: return "vk_wait_for_fences_failed";
                case CaptureSkipReason::CaptureFormatUnsupported: return "capture_format_unsupported";
                case CaptureSkipReason::VkMapMemoryFailed: return "vk_map_memory_failed";
                case CaptureSkipReason::WriteFrameFailed: return "write_frame_failed";
                default: return "unknown";
            }
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
            (void)swprintf_s(fileName, L"hook_vulkan_%lu.log", static_cast<unsigned long>(pid));
            std::wstring filePath = dir;
            filePath += L'\\';
            filePath += fileName;

            const HANDLE file = CreateFileW(
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
                char msg[512]{};
                (void)_snprintf_s(
                    msg,
                    sizeof(msg),
                    _TRUNCATE,
                    "stage=hook_vulkan event=file_log_open pid=%lu path=\"%s\".",
                    static_cast<unsigned long>(pid),
                    pathUtf8.c_str());
                OutputDebugStringA(msg);
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

        void DebugLog(const char* fmt, ...)
        {
            if (fmt == nullptr)
            {
                return;
            }

            char buffer[768]{};
            va_list args;
            va_start(args, fmt);
            (void)_vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, fmt, args);
            va_end(args);
            OutputDebugStringA(buffer);
            OutputDebugStringA("\n");
            if (g_diagFileSinkEnabled.load(std::memory_order_relaxed))
            {
                AppendDiagFileLine(buffer);
            }
        }

        void DebugLogInstall(const char* fmt, ...)
        {
            if (fmt == nullptr)
            {
                return;
            }

            char buffer[768]{};
            va_list args;
            va_start(args, fmt);
            (void)_vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, fmt, args);
            va_end(args);
            OutputDebugStringA(buffer);
            OutputDebugStringA("\n");
            AppendDiagFileLine(buffer);
        }

        bool ShouldEmitDiagLog(std::uint64_t nowQpc, std::uint64_t qpcFreq, std::uint64_t& lastQpc, std::uint64_t intervalMs)
        {
            if (qpcFreq == 0)
            {
                lastQpc = nowQpc;
                return true;
            }

            const auto minDelta = (qpcFreq * intervalMs) / 1000u;
            if (lastQpc != 0 && nowQpc > lastQpc && (nowQpc - lastQpc) < minDelta)
            {
                return false;
            }

            lastQpc = nowQpc;
            return true;
        }

        double QpcDeltaToMs(std::uint64_t deltaQpc, std::uint64_t qpcFreq)
        {
            if (qpcFreq == 0)
            {
                return 0.0;
            }

            return (static_cast<double>(deltaQpc) * 1000.0) / static_cast<double>(qpcFreq);
        }

        void LogCaptureSkipLocked(VulkanRuntime& rt, CaptureSkipReason reason, const char* detail = nullptr)
        {
            const auto idx = static_cast<std::size_t>(reason);
            if (idx >= rt.captureSkipLastLogQpc.size())
            {
                return;
            }

            rt.captureSkipPendingCount[idx]++;
            const auto now = NowQpc();
            if (!ShouldEmitDiagLog(now, rt.qpcFreq, rt.captureSkipLastLogQpc[idx], kDiagLogMinIntervalMs))
            {
                return;
            }

            const auto pending = rt.captureSkipPendingCount[idx];
            rt.captureSkipPendingCount[idx] = 0;
            DebugLog(
                "stage=hook_vulkan event=capture_skip reason=%s pending=%u detail=%s.",
                CaptureSkipReasonToString(reason),
                pending,
                detail != nullptr ? detail : "none");
        }

        void LogPresentSummaryLocked(
            VulkanRuntime& rt,
            bool shouldCapture,
            bool hasOverlayBlocks,
            std::size_t swapchainCount)
        {
            const auto now = rt.lastPresentQpc;
            const bool byFrame = (rt.presentCount % 120ull) == 0ull;
            if (!byFrame &&
                !ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastPresentSummaryLogQpc, kDiagSummaryIntervalMs))
            {
                return;
            }

            DebugLog(
                "stage=hook_vulkan event=present_summary pid=%lu presentCount=%llu shouldCapture=%d overlayEnabled=%d hasOverlayBlocks=%d queues=%zu swapchains=%zu.",
                static_cast<unsigned long>(GetCurrentProcessId()),
                static_cast<unsigned long long>(rt.presentCount),
                shouldCapture ? 1 : 0,
                rt.overlayEnabled ? 1 : 0,
                hasOverlayBlocks ? 1 : 0,
                rt.queues.size(),
                swapchainCount);
        }

        ImU32 ArgbToImU32(std::uint32_t argb)
        {
            const std::uint32_t a = (argb >> 24) & 0xFFu;
            const std::uint32_t r = (argb >> 16) & 0xFFu;
            const std::uint32_t g = (argb >> 8) & 0xFFu;
            const std::uint32_t b = argb & 0xFFu;
            return IM_COL32(r, g, b, a);
        }

        void CmdTransitionImageLayout(
            VkCommandBuffer cmd,
            VkImage image,
            VkImageLayout oldLayout,
            VkImageLayout newLayout,
            VkAccessFlags srcAccess,
            VkAccessFlags dstAccess,
            VkPipelineStageFlags srcStage,
            VkPipelineStageFlags dstStage)
        {
            VkImageMemoryBarrier barrier{};
            barrier.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
            barrier.oldLayout = oldLayout;
            barrier.newLayout = newLayout;
            barrier.srcAccessMask = srcAccess;
            barrier.dstAccessMask = dstAccess;
            barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
            barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
            barrier.image = image;
            barrier.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
            barrier.subresourceRange.baseMipLevel = 0;
            barrier.subresourceRange.levelCount = 1;
            barrier.subresourceRange.baseArrayLayer = 0;
            barrier.subresourceRange.layerCount = 1;

            vkCmdPipelineBarrier(
                cmd,
                srcStage,
                dstStage,
                0,
                0,
                nullptr,
                0,
                nullptr,
                1,
                &barrier);
        }

        void DestroyQueueGpuState(QueueGpuState& st)
        {
            if (st.device == VK_NULL_HANDLE)
            {
                st = QueueGpuState{};
                return;
            }

            if (st.stagingMapped != nullptr && st.stagingMemory != VK_NULL_HANDLE)
            {
                vkUnmapMemory(st.device, st.stagingMemory);
                st.stagingMapped = nullptr;
            }

            if (st.fence != VK_NULL_HANDLE)
            {
                vkDestroyFence(st.device, st.fence, nullptr);
                st.fence = VK_NULL_HANDLE;
            }

            if (st.stagingBuffer != VK_NULL_HANDLE)
            {
                vkDestroyBuffer(st.device, st.stagingBuffer, nullptr);
                st.stagingBuffer = VK_NULL_HANDLE;
            }

            if (st.stagingMemory != VK_NULL_HANDLE)
            {
                vkFreeMemory(st.device, st.stagingMemory, nullptr);
                st.stagingMemory = VK_NULL_HANDLE;
            }

            if (st.commandPool != VK_NULL_HANDLE)
            {
                vkDestroyCommandPool(st.device, st.commandPool, nullptr);
                st.commandPool = VK_NULL_HANDLE;
                st.commandBuffer = VK_NULL_HANDLE;
            }

            st = QueueGpuState{};
        }

        void DestroyOverlaySwapchainState(VkDevice device, OverlaySwapchainState& st)
        {
            if (device == VK_NULL_HANDLE)
            {
                st = OverlaySwapchainState{};
                return;
            }

            for (auto framebuffer : st.framebuffers)
            {
                if (framebuffer != VK_NULL_HANDLE)
                {
                    vkDestroyFramebuffer(device, framebuffer, nullptr);
                }
            }
            st.framebuffers.clear();

            for (auto imageView : st.imageViews)
            {
                if (imageView != VK_NULL_HANDLE)
                {
                    vkDestroyImageView(device, imageView, nullptr);
                }
            }
            st.imageViews.clear();

            if (st.renderPass != VK_NULL_HANDLE)
            {
                vkDestroyRenderPass(device, st.renderPass, nullptr);
                st.renderPass = VK_NULL_HANDLE;
            }

            st.format = VK_FORMAT_UNDEFINED;
            st.width = 0;
            st.height = 0;
            st.imageCount = 0;
        }
        void ShutdownImGuiLocked(VulkanRuntime& rt)
        {
            if (rt.imguiContext == nullptr)
            {
                rt.imguiInitialized = false;
                rt.overlayFont = nullptr;
                return;
            }

            ImGui::SetCurrentContext(rt.imguiContext);
            if (rt.imguiInitialized)
            {
                ImGui_ImplVulkan_Shutdown();
            }
            ImGui::DestroyContext(rt.imguiContext);

            rt.imguiContext = nullptr;
            rt.imguiInitialized = false;
            rt.overlayFont = nullptr;
            rt.imguiBoundSwapchain = VK_NULL_HANDLE;
            rt.imguiImageCount = 0;
            rt.lastImGuiQpc = 0;

            if (rt.imguiDescriptorPool != VK_NULL_HANDLE)
            {
                // WHY: Descriptor pool lifetime is tied to ImGui backend lifetime.
                if (rt.imguiDevice != VK_NULL_HANDLE)
                {
                    vkDestroyDescriptorPool(rt.imguiDevice, rt.imguiDescriptorPool, nullptr);
                }
                rt.imguiDescriptorPool = VK_NULL_HANDLE;
            }
            rt.imguiDevice = VK_NULL_HANDLE;
        }

        void RemoveDeviceStateLocked(VulkanRuntime& rt, VkDevice device)
        {
            for (auto it = rt.queueGpuStates.begin(); it != rt.queueGpuStates.end();)
            {
                if (it->second.device == device)
                {
                    DestroyQueueGpuState(it->second);
                    it = rt.queueGpuStates.erase(it);
                    continue;
                }
                ++it;
            }

            for (auto it = rt.queues.begin(); it != rt.queues.end();)
            {
                if (it->second.device == device)
                {
                    it = rt.queues.erase(it);
                    continue;
                }
                ++it;
            }

            for (auto it = rt.overlaySwapchains.begin(); it != rt.overlaySwapchains.end();)
            {
                auto scIt = rt.swapchains.find(it->first);
                if (scIt != rt.swapchains.end() && scIt->second.device == device)
                {
                    DestroyOverlaySwapchainState(device, it->second);
                    it = rt.overlaySwapchains.erase(it);
                    continue;
                }
                ++it;
            }

            for (auto it = rt.swapchains.begin(); it != rt.swapchains.end();)
            {
                if (it->second.device == device)
                {
                    it = rt.swapchains.erase(it);
                    continue;
                }
                ++it;
            }

            rt.devices.erase(device);
        }

        void ResetRuntimeLocked(VulkanRuntime& rt)
        {
            ShutdownImGuiLocked(rt);

            for (auto& entry : rt.overlaySwapchains)
            {
                auto scIt = rt.swapchains.find(entry.first);
                const auto device = (scIt != rt.swapchains.end()) ? scIt->second.device : VK_NULL_HANDLE;
                DestroyOverlaySwapchainState(device, entry.second);
            }

            for (auto& entry : rt.queueGpuStates)
            {
                DestroyQueueGpuState(entry.second);
            }

            rt.overlaySwapchains.clear();
            rt.queueGpuStates.clear();
            rt.queues.clear();
            rt.swapchains.clear();
            rt.devices.clear();

            rt.frameWriter.Reset();
            rt.configReader.Reset();
            rt.statusWriter.Reset();
            rt.overlayV2Reader.Reset();

            rt.frameId = 0;
            rt.lastCaptureQpc = 0;
            rt.lastConfigQpc = 0;
            rt.lastFrameWriteQpc = 0;
            rt.configuredFpsLimit = kDefaultCaptureFps;
            rt.captureIntervalQpc = (rt.qpcFreq > 0) ? (rt.qpcFreq / kDefaultCaptureFps) : 0;
            rt.overlayEnabled = false;
            rt.perfDiagLogEnabled = false;
            g_diagFileSinkEnabled.store(false, std::memory_order_relaxed);

            rt.lastOverlayV2Seq = 0;
            rt.lastOverlayV2Qpc = 0;
            rt.overlayV2Header = {};
            rt.overlayV2Blocks.clear();
            rt.overlayV2TextBlob.clear();
            rt.hookSuccessIndicatorArmed = false;
            rt.hookSuccessIndicatorDone = false;
            rt.hookSuccessIndicatorStartQpc = 0;

            rt.presentCount = 0;
            rt.lastPresentQpc = 0;
            rt.lastPresentKind = 1;
            rt.lastFormat = 0;
            rt.lastWidth = 0;
            rt.lastHeight = 0;
            rt.captureSkipLastLogQpc.fill(0);
            rt.captureSkipPendingCount.fill(0);
            rt.lastPresentSummaryLogQpc = 0;
            rt.lastPresentPerfLogQpc = 0;
            rt.lastSwapchainEnsureFailQpc = 0;
            rt.lastGpuStateFailQpc = 0;
            rt.lastWriteFrameOkQpc = 0;
            rt.lastWriteFrameFailQpc = 0;
            rt.lastQueuePresentEnterLogQpc = 0;
            g_loggedFirstCreateDeviceHit.store(false);
            g_loggedFirstCreateSwapchainHit.store(false);

            rt.instance = VK_NULL_HANDLE;
            rt.apiVersion = VK_API_VERSION_1_0;

            rt.originalGetDeviceProcAddr = nullptr;
            rt.originalGetInstanceProcAddr = nullptr;
            rt.originalCreateInstance = nullptr;
            rt.originalDestroyInstance = nullptr;
            rt.originalCreateDevice = nullptr;
            rt.originalDestroyDevice = nullptr;
            rt.originalGetDeviceQueue = nullptr;
            rt.originalGetDeviceQueue2 = nullptr;
            rt.originalCreateSwapchainKHR = nullptr;
            rt.originalDestroySwapchainKHR = nullptr;
            rt.originalAcquireNextImageKHR = nullptr;
            rt.originalAcquireNextImage2KHR = nullptr;
            rt.originalGetSwapchainImagesKHR = nullptr;
            rt.originalQueuePresentKHR = nullptr;
        }

        bool FindHostVisibleCoherentMemoryType(
            VkPhysicalDevice physicalDevice,
            std::uint32_t typeBits,
            std::uint32_t& selectedType,
            bool& selectedHostCached)
        {
            selectedType = std::numeric_limits<std::uint32_t>::max();
            selectedHostCached = false;

            VkPhysicalDeviceMemoryProperties memProps{};
            vkGetPhysicalDeviceMemoryProperties(physicalDevice, &memProps);

            // WHY: Host-cached + coherent is significantly faster for CPU readback on some drivers.
            for (std::uint32_t i = 0; i < memProps.memoryTypeCount; ++i)
            {
                const bool typeMatches = (typeBits & (1u << i)) != 0;
                const auto flags = memProps.memoryTypes[i].propertyFlags;
                const bool hostVisible = (flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) != 0;
                const bool hostCoherent = (flags & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT) != 0;
                const bool hostCached = (flags & VK_MEMORY_PROPERTY_HOST_CACHED_BIT) != 0;
                if (typeMatches && hostVisible && hostCoherent && hostCached)
                {
                    selectedType = i;
                    selectedHostCached = true;
                    return true;
                }
            }

            for (std::uint32_t i = 0; i < memProps.memoryTypeCount; ++i)
            {
                const bool typeMatches = (typeBits & (1u << i)) != 0;
                const auto flags = memProps.memoryTypes[i].propertyFlags;
                const bool hostVisible = (flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) != 0;
                const bool hostCoherent = (flags & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT) != 0;
                if (typeMatches && hostVisible && hostCoherent)
                {
                    selectedType = i;
                    selectedHostCached = false;
                    return true;
                }
            }

            return false;
        }

        bool IsCaptureFormatSupported(VkFormat format, bool& rgbaNeedsSwap)
        {
            rgbaNeedsSwap = false;
            switch (format)
            {
                case VK_FORMAT_B8G8R8A8_UNORM:
                case VK_FORMAT_B8G8R8A8_SRGB:
                    return true;
                case VK_FORMAT_R8G8B8A8_UNORM:
                case VK_FORMAT_R8G8B8A8_SRGB:
                    rgbaNeedsSwap = true;
                    return true;
                default:
                    return false;
            }
        }

        bool EnsureConfigRefreshedLocked(VulkanRuntime& rt)
        {
            if (rt.qpcFreq == 0)
            {
                rt.qpcFreq = QueryQpcFreq();
            }

            const auto now = NowQpc();
            if (rt.lastConfigQpc != 0 && now - rt.lastConfigQpc < (rt.qpcFreq / 5u))
            {
                return true;
            }

            rt.lastConfigQpc = now;
            const auto pid = GetCurrentProcessId();
            if (!rt.configReader.Ensure(pid, ipc::GraphicsApi::Vulkan))
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

            rt.configuredFpsLimit = ClampFps(cfg.captureFpsLimit);
            rt.captureIntervalQpc = (rt.qpcFreq > 0 && rt.configuredFpsLimit > 0)
                ? (rt.qpcFreq / rt.configuredFpsLimit)
                : 0;
            rt.overlayEnabled = cfg.overlayEnabled != 0;
            rt.perfDiagLogEnabled = (cfg.reserved0 & ipc::kConfigFlagEnablePerfDiagLog) != 0;
            const bool diagFileSinkEnabled = (cfg.reserved0 & ipc::kConfigFlagEnableDiagFileSink) != 0;
            const bool previousDiagFileSinkEnabled =
                g_diagFileSinkEnabled.exchange(diagFileSinkEnabled, std::memory_order_relaxed);
            if (previousDiagFileSinkEnabled && !diagFileSinkEnabled)
            {
                CloseDiagFile();
            }
            return true;
        }

        bool ShouldCaptureNowLocked(const VulkanRuntime& rt, std::uint64_t nowQpc)
        {
            if (rt.captureIntervalQpc == 0 || rt.lastCaptureQpc == 0)
            {
                return true;
            }

            return (nowQpc - rt.lastCaptureQpc) >= rt.captureIntervalQpc;
        }

        bool EnsureSwapchainImagesLocked(VulkanRuntime& rt, SwapchainInfo& info, VkSwapchainKHR swapchain)
        {
            if (info.device == VK_NULL_HANDLE)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastSwapchainEnsureFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_swapchain_images fail reason=device_null swapchain=0x%llX.",
                        static_cast<unsigned long long>(SwapchainHandleToLogValue(swapchain)));
                }
                return false;
            }

            auto getImages = rt.originalGetSwapchainImagesKHR;
            if (getImages == nullptr && rt.originalGetDeviceProcAddr != nullptr)
            {
                const auto fn = rt.originalGetDeviceProcAddr(info.device, "vkGetSwapchainImagesKHR");
                if (fn != nullptr)
                {
                    getImages = reinterpret_cast<PFN_vkGetSwapchainImagesKHR>(fn);
                    rt.originalGetSwapchainImagesKHR = getImages;
                }
            }

            if (getImages == nullptr)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastSwapchainEnsureFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_swapchain_images fail reason=get_swapchain_images_missing device=%p swapchain=0x%llX.",
                        info.device,
                        static_cast<unsigned long long>(SwapchainHandleToLogValue(swapchain)));
                }
                return false;
            }

            std::uint32_t imageCount = 0;
            const auto countResult = getImages(info.device, swapchain, &imageCount, nullptr);
            if (countResult != VK_SUCCESS || imageCount == 0)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastSwapchainEnsureFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_swapchain_images fail reason=query_count_failed vk=%d count=%u device=%p swapchain=0x%llX.",
                        static_cast<int>(countResult),
                        imageCount,
                        info.device,
                        static_cast<unsigned long long>(SwapchainHandleToLogValue(swapchain)));
                }
                return false;
            }

            std::vector<VkImage> images(imageCount);
            const auto fillResult = getImages(info.device, swapchain, &imageCount, images.data());
            if (fillResult != VK_SUCCESS || imageCount == 0)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastSwapchainEnsureFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_swapchain_images fail reason=fetch_images_failed vk=%d count=%u device=%p swapchain=0x%llX.",
                        static_cast<int>(fillResult),
                        imageCount,
                        info.device,
                        static_cast<unsigned long long>(SwapchainHandleToLogValue(swapchain)));
                }
                return false;
            }

            images.resize(imageCount);
            info.images = std::move(images);
            return true;
        }

        void UpsertSwapchainFromCreateLocked(VulkanRuntime& rt, VkDevice device, VkSwapchainKHR swapchain, const VkSwapchainCreateInfoKHR* createInfo)
        {
            if (swapchain == VK_NULL_HANDLE)
            {
                return;
            }

            auto& info = rt.swapchains[swapchain];
            info.device = device;
            if (createInfo != nullptr)
            {
                info.format = createInfo->imageFormat;
                info.extent = createInfo->imageExtent;
            }

            (void)EnsureSwapchainImagesLocked(rt, info, swapchain);
        }

        void UpsertSwapchainFromAcquireLocked(VulkanRuntime& rt, VkDevice device, VkSwapchainKHR swapchain)
        {
            if (swapchain == VK_NULL_HANDLE)
            {
                return;
            }

            auto& info = rt.swapchains[swapchain];
            if (info.device == VK_NULL_HANDLE)
            {
                info.device = device;
            }

            (void)EnsureSwapchainImagesLocked(rt, info, swapchain);
        }
        bool EnsureQueueGpuStateLocked(
            VulkanRuntime& rt,
            VkQueue queue,
            const QueueInfo& queueInfo,
            const DeviceInfo& deviceInfo,
            const SwapchainInfo& swapInfo)
        {
            if (swapInfo.extent.width == 0 || swapInfo.extent.height == 0)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=invalid_extent queue=%p width=%u height=%u.",
                        queue,
                        swapInfo.extent.width,
                        swapInfo.extent.height);
                }
                return false;
            }

            bool rgbaNeedsSwap = false;
            if (!IsCaptureFormatSupported(swapInfo.format, rgbaNeedsSwap))
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=unsupported_format queue=%p format=%u.",
                        queue,
                        static_cast<std::uint32_t>(swapInfo.format));
                }
                return false;
            }

            const VkDevice device = queueInfo.device;
            if (device == VK_NULL_HANDLE || deviceInfo.physicalDevice == VK_NULL_HANDLE || !queueInfo.valid)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=device_info_invalid queue=%p device=%p physical=%p queueValid=%d.",
                        queue,
                        device,
                        deviceInfo.physicalDevice,
                        queueInfo.valid ? 1 : 0);
                }
                return false;
            }

            auto& state = rt.queueGpuStates[queue];
            const VkDeviceSize requiredBytes =
                static_cast<VkDeviceSize>(swapInfo.extent.width) *
                static_cast<VkDeviceSize>(swapInfo.extent.height) * 4ull;

            const bool mustRecreate =
                state.device != device ||
                state.queueFamily != queueInfo.familyIndex ||
                state.width != swapInfo.extent.width ||
                state.height != swapInfo.extent.height ||
                state.format != swapInfo.format ||
                state.stagingBytes != requiredBytes ||
                state.commandPool == VK_NULL_HANDLE ||
                state.commandBuffer == VK_NULL_HANDLE ||
                state.stagingBuffer == VK_NULL_HANDLE ||
                state.stagingMemory == VK_NULL_HANDLE ||
                state.stagingMapped == nullptr ||
                state.fence == VK_NULL_HANDLE;

            if (!mustRecreate)
            {
                return true;
            }

            DestroyQueueGpuState(state);
            state.device = device;
            state.queueFamily = queueInfo.familyIndex;
            state.width = swapInfo.extent.width;
            state.height = swapInfo.extent.height;
            state.format = swapInfo.format;
            state.stagingBytes = requiredBytes;

            VkCommandPoolCreateInfo poolInfo{};
            poolInfo.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
            poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
            poolInfo.queueFamilyIndex = queueInfo.familyIndex;
            const auto poolResult = vkCreateCommandPool(device, &poolInfo, nullptr, &state.commandPool);
            if (poolResult != VK_SUCCESS)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=create_command_pool_failed vk=%d queue=%p family=%u.",
                        static_cast<int>(poolResult),
                        queue,
                        queueInfo.familyIndex);
                }
                DestroyQueueGpuState(state);
                return false;
            }

            VkCommandBufferAllocateInfo cmdAlloc{};
            cmdAlloc.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
            cmdAlloc.commandPool = state.commandPool;
            cmdAlloc.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
            cmdAlloc.commandBufferCount = 1;
            const auto cmdAllocResult = vkAllocateCommandBuffers(device, &cmdAlloc, &state.commandBuffer);
            if (cmdAllocResult != VK_SUCCESS)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=allocate_command_buffer_failed vk=%d queue=%p.",
                        static_cast<int>(cmdAllocResult),
                        queue);
                }
                DestroyQueueGpuState(state);
                return false;
            }

            VkFenceCreateInfo fenceInfo{};
            fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
            const auto fenceResult = vkCreateFence(device, &fenceInfo, nullptr, &state.fence);
            if (fenceResult != VK_SUCCESS)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=create_fence_failed vk=%d queue=%p.",
                        static_cast<int>(fenceResult),
                        queue);
                }
                DestroyQueueGpuState(state);
                return false;
            }

            VkBufferCreateInfo bufferInfo{};
            bufferInfo.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
            bufferInfo.size = requiredBytes;
            bufferInfo.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
            bufferInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
            const auto bufferResult = vkCreateBuffer(device, &bufferInfo, nullptr, &state.stagingBuffer);
            if (bufferResult != VK_SUCCESS)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=create_staging_buffer_failed vk=%d bytes=%llu queue=%p.",
                        static_cast<int>(bufferResult),
                        static_cast<unsigned long long>(requiredBytes),
                        queue);
                }
                DestroyQueueGpuState(state);
                return false;
            }

            VkMemoryRequirements memReq{};
            vkGetBufferMemoryRequirements(device, state.stagingBuffer, &memReq);
            std::uint32_t memoryType = std::numeric_limits<std::uint32_t>::max();
            bool hostCached = false;
            if (!FindHostVisibleCoherentMemoryType(deviceInfo.physicalDevice, memReq.memoryTypeBits, memoryType, hostCached))
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=host_visible_coherent_memory_not_found queue=%p typeBits=%u.",
                        queue,
                        memReq.memoryTypeBits);
                }
                DestroyQueueGpuState(state);
                return false;
            }

            state.stagingHostCached = hostCached;

            VkMemoryAllocateInfo allocInfo{};
            allocInfo.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
            allocInfo.allocationSize = memReq.size;
            allocInfo.memoryTypeIndex = memoryType;
            const auto allocResult = vkAllocateMemory(device, &allocInfo, nullptr, &state.stagingMemory);
            if (allocResult != VK_SUCCESS)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=allocate_staging_memory_failed vk=%d bytes=%llu queue=%p.",
                        static_cast<int>(allocResult),
                        static_cast<unsigned long long>(memReq.size),
                        queue);
                }
                DestroyQueueGpuState(state);
                return false;
            }

            const auto bindResult = vkBindBufferMemory(device, state.stagingBuffer, state.stagingMemory, 0);
            if (bindResult != VK_SUCCESS)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=bind_staging_buffer_memory_failed vk=%d queue=%p.",
                        static_cast<int>(bindResult),
                        queue);
                }
                DestroyQueueGpuState(state);
                return false;
            }

            void* mapped = nullptr;
            const auto mapResult = vkMapMemory(device, state.stagingMemory, 0, state.stagingBytes, 0, &mapped);
            if (mapResult != VK_SUCCESS || mapped == nullptr)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastGpuStateFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=ensure_queue_gpu_state fail reason=map_staging_memory_failed vk=%d mapped=%d bytes=%llu queue=%p.",
                        static_cast<int>(mapResult),
                        mapped != nullptr ? 1 : 0,
                        static_cast<unsigned long long>(state.stagingBytes),
                        queue);
                }
                DestroyQueueGpuState(state);
                return false;
            }
            state.stagingMapped = mapped;

            DebugLog(
                "stage=hook_vulkan event=staging_memory_selected queue=%p typeIndex=%u hostCached=%d bytes=%llu.",
                queue,
                memoryType,
                state.stagingHostCached ? 1 : 0,
                static_cast<unsigned long long>(state.stagingBytes));

            state.scratch.assign(static_cast<std::size_t>(requiredBytes), 0);
            return true;
        }

        bool EnsureOverlaySwapchainStateLocked(
            VulkanRuntime& rt,
            VkSwapchainKHR swapchain,
            const SwapchainInfo& swapInfo)
        {
            if (swapInfo.device == VK_NULL_HANDLE || swapInfo.images.empty())
            {
                return false;
            }

            auto& ovl = rt.overlaySwapchains[swapchain];
            const bool mustRecreate =
                ovl.renderPass == VK_NULL_HANDLE ||
                ovl.format != swapInfo.format ||
                ovl.width != swapInfo.extent.width ||
                ovl.height != swapInfo.extent.height ||
                ovl.imageCount != static_cast<std::uint32_t>(swapInfo.images.size()) ||
                ovl.framebuffers.size() != swapInfo.images.size() ||
                ovl.imageViews.size() != swapInfo.images.size();

            if (!mustRecreate)
            {
                return true;
            }

            DestroyOverlaySwapchainState(swapInfo.device, ovl);

            VkAttachmentDescription attachment{};
            attachment.format = swapInfo.format;
            attachment.samples = VK_SAMPLE_COUNT_1_BIT;
            attachment.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
            attachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
            attachment.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
            attachment.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
            attachment.initialLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
            attachment.finalLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;

            VkAttachmentReference colorRef{};
            colorRef.attachment = 0;
            colorRef.layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;

            VkSubpassDescription subpass{};
            subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
            subpass.colorAttachmentCount = 1;
            subpass.pColorAttachments = &colorRef;

            VkSubpassDependency dep{};
            dep.srcSubpass = VK_SUBPASS_EXTERNAL;
            dep.dstSubpass = 0;
            dep.srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
            dep.dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
            dep.srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
            dep.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;

            VkRenderPassCreateInfo rpInfo{};
            rpInfo.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
            rpInfo.attachmentCount = 1;
            rpInfo.pAttachments = &attachment;
            rpInfo.subpassCount = 1;
            rpInfo.pSubpasses = &subpass;
            rpInfo.dependencyCount = 1;
            rpInfo.pDependencies = &dep;
            if (vkCreateRenderPass(swapInfo.device, &rpInfo, nullptr, &ovl.renderPass) != VK_SUCCESS)
            {
                DestroyOverlaySwapchainState(swapInfo.device, ovl);
                return false;
            }

            ovl.imageViews.resize(swapInfo.images.size(), VK_NULL_HANDLE);
            ovl.framebuffers.resize(swapInfo.images.size(), VK_NULL_HANDLE);

            for (std::size_t i = 0; i < swapInfo.images.size(); ++i)
            {
                VkImageViewCreateInfo viewInfo{};
                viewInfo.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
                viewInfo.image = swapInfo.images[i];
                viewInfo.viewType = VK_IMAGE_VIEW_TYPE_2D;
                viewInfo.format = swapInfo.format;
                viewInfo.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
                viewInfo.subresourceRange.baseMipLevel = 0;
                viewInfo.subresourceRange.levelCount = 1;
                viewInfo.subresourceRange.baseArrayLayer = 0;
                viewInfo.subresourceRange.layerCount = 1;
                if (vkCreateImageView(swapInfo.device, &viewInfo, nullptr, &ovl.imageViews[i]) != VK_SUCCESS)
                {
                    DestroyOverlaySwapchainState(swapInfo.device, ovl);
                    return false;
                }

                VkFramebufferCreateInfo fbInfo{};
                fbInfo.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
                fbInfo.renderPass = ovl.renderPass;
                fbInfo.attachmentCount = 1;
                fbInfo.pAttachments = &ovl.imageViews[i];
                fbInfo.width = swapInfo.extent.width;
                fbInfo.height = swapInfo.extent.height;
                fbInfo.layers = 1;
                if (vkCreateFramebuffer(swapInfo.device, &fbInfo, nullptr, &ovl.framebuffers[i]) != VK_SUCCESS)
                {
                    DestroyOverlaySwapchainState(swapInfo.device, ovl);
                    return false;
                }
            }

            ovl.format = swapInfo.format;
            ovl.width = swapInfo.extent.width;
            ovl.height = swapInfo.extent.height;
            ovl.imageCount = static_cast<std::uint32_t>(swapInfo.images.size());
            return true;
        }

        bool RefreshOverlayV2Locked(VulkanRuntime& rt)
        {
            const auto pid = GetCurrentProcessId();
            if (!rt.overlayV2Reader.Ensure(pid, ipc::GraphicsApi::Vulkan))
            {
                return false;
            }

            ipc::OverlayV2Header header{};
            std::vector<ipc::OverlayTextBlockV2> blocks;
            std::vector<std::uint8_t> textBlob;
            if (!rt.overlayV2Reader.TryRead(header, blocks, textBlob))
            {
                return false;
            }

            if (header.updatedSeq == 0 || header.updatedSeq == rt.lastOverlayV2Seq)
            {
                return false;
            }

            rt.overlayV2Header = header;
            rt.overlayV2Blocks = std::move(blocks);
            rt.overlayV2TextBlob = std::move(textBlob);
            rt.lastOverlayV2Seq = header.updatedSeq;
            rt.lastOverlayV2Qpc = NowQpc();
            // WHY: Stop the transient attach-success icon once real overlay text arrives.
            rt.hookSuccessIndicatorArmed = false;
            rt.hookSuccessIndicatorDone = true;
            rt.hookSuccessIndicatorStartQpc = 0;
            return true;
        }
        bool EnsureImGuiLocked(
            VulkanRuntime& rt,
            VkQueue queue,
            VkSwapchainKHR swapchain,
            const QueueInfo& queueInfo,
            const DeviceInfo& deviceInfo,
            const OverlaySwapchainState& ovl)
        {
            if (rt.instance == VK_NULL_HANDLE)
            {
                return false;
            }

            if (rt.imguiContext == nullptr)
            {
                rt.imguiContext = ImGui::CreateContext();
                ImGui::SetCurrentContext(rt.imguiContext);
                ImGuiIO& io = ImGui::GetIO();
                io.IniFilename = nullptr;
                io.LogFilename = nullptr;

                constexpr char kMeiryoPath[] = "C:/Windows/Fonts/meiryo.ttc";
                ImFontConfig cfg{};
                cfg.OversampleH = 2;
                cfg.OversampleV = 2;
                rt.overlayFont = io.Fonts->AddFontFromFileTTF(
                    kMeiryoPath,
                    static_cast<float>(kOverlayFontBasePx),
                    &cfg,
                    io.Fonts->GetGlyphRangesJapanese());
                if (rt.overlayFont == nullptr)
                {
                    rt.overlayFont = io.Fonts->AddFontDefault();
                }
            }
            else
            {
                ImGui::SetCurrentContext(rt.imguiContext);
            }

            if (rt.imguiDescriptorPool == VK_NULL_HANDLE)
            {
                const std::array<VkDescriptorPoolSize, 11> poolSizes =
                {
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_SAMPLER, 512},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 2048},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, 512},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 512},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_UNIFORM_TEXEL_BUFFER, 512},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_STORAGE_TEXEL_BUFFER, 512},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 512},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 512},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC, 512},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_STORAGE_BUFFER_DYNAMIC, 512},
                    VkDescriptorPoolSize{VK_DESCRIPTOR_TYPE_INPUT_ATTACHMENT, 512},
                };

                VkDescriptorPoolCreateInfo poolInfo{};
                poolInfo.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
                poolInfo.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
                poolInfo.maxSets = 4096;
                poolInfo.poolSizeCount = static_cast<std::uint32_t>(poolSizes.size());
                poolInfo.pPoolSizes = poolSizes.data();
                if (vkCreateDescriptorPool(queueInfo.device, &poolInfo, nullptr, &rt.imguiDescriptorPool) != VK_SUCCESS)
                {
                    return false;
                }
                rt.imguiDevice = queueInfo.device;
            }

            const auto imageCount = static_cast<std::uint32_t>(ovl.framebuffers.size());
            if (imageCount == 0)
            {
                return false;
            }

            if (!rt.imguiInitialized)
            {
                ImGui_ImplVulkan_InitInfo initInfo{};
                initInfo.ApiVersion = rt.apiVersion;
                initInfo.Instance = rt.instance;
                initInfo.PhysicalDevice = deviceInfo.physicalDevice;
                initInfo.Device = queueInfo.device;
                initInfo.QueueFamily = queueInfo.familyIndex;
                initInfo.Queue = queue;
                initInfo.DescriptorPool = rt.imguiDescriptorPool;
                initInfo.MinImageCount = imageCount;
                initInfo.ImageCount = imageCount;
                initInfo.PipelineInfoMain.RenderPass = ovl.renderPass;
                initInfo.PipelineInfoMain.Subpass = 0;
                initInfo.PipelineInfoMain.MSAASamples = VK_SAMPLE_COUNT_1_BIT;
                initInfo.UseDynamicRendering = false;

                if (!ImGui_ImplVulkan_Init(&initInfo))
                {
                    return false;
                }

                rt.imguiInitialized = true;
                rt.imguiBoundSwapchain = swapchain;
                rt.imguiImageCount = imageCount;
            }
            else if (rt.imguiBoundSwapchain != swapchain || rt.imguiImageCount != imageCount)
            {
                // WHY: swapchain image count change requires backend queued-frame count refresh.
                ImGui_ImplVulkan_SetMinImageCount(imageCount);
                rt.imguiBoundSwapchain = swapchain;
                rt.imguiImageCount = imageCount;
            }

            return true;
        }

        void BuildImGuiOverlayDrawDataLocked(VulkanRuntime& rt, std::uint32_t targetW, std::uint32_t targetH)
        {
            if (!rt.imguiInitialized || rt.imguiContext == nullptr)
            {
                return;
            }

            ImGui::SetCurrentContext(rt.imguiContext);
            const auto now = NowQpc();
            ImGuiIO& io = ImGui::GetIO();
            io.DisplaySize = ImVec2(static_cast<float>(targetW), static_cast<float>(targetH));
            if (rt.lastImGuiQpc != 0 && rt.qpcFreq > 0)
            {
                const double delta = static_cast<double>(now - rt.lastImGuiQpc) / static_cast<double>(rt.qpcFreq);
                io.DeltaTime = static_cast<float>(std::max(1.0 / 240.0, delta));
            }
            else
            {
                io.DeltaTime = 1.0f / static_cast<float>(std::max(1u, rt.configuredFpsLimit));
            }
            rt.lastImGuiQpc = now;

            ImGui_ImplVulkan_NewFrame();
            ImGui::NewFrame();

            const auto canvasW = std::max(1u, rt.overlayV2Header.canvasW);
            const auto canvasH = std::max(1u, rt.overlayV2Header.canvasH);
            const float scaleX = static_cast<float>(targetW) / static_cast<float>(canvasW);
            const float scaleY = static_cast<float>(targetH) / static_cast<float>(canvasH);

            ImDrawList* bg = ImGui::GetBackgroundDrawList();
            ImDrawList* fg = ImGui::GetForegroundDrawList();

            const auto blockCount = std::min<std::size_t>(rt.overlayV2Blocks.size(), kOverlayMaxBlocks);
            for (std::size_t i = 0; i < blockCount; ++i)
            {
                const auto& b = rt.overlayV2Blocks[i];
                if (b.w <= 0.0f || b.h <= 0.0f)
                {
                    continue;
                }

                const float x = b.x * scaleX;
                const float y = b.y * scaleY;
                const float w = b.w * scaleX;
                const float h = b.h * scaleY;
                const float pad = std::max(0.0f, b.paddingPx * std::min(scaleX, scaleY));
                const float rounding = std::max(0.0f, b.roundingPx * std::min(scaleX, scaleY));

                const ImVec2 p0(x, y);
                const ImVec2 p1(x + w, y + h);
                if (b.wrap == 2u)
                {
                    fg->AddRect(p0, p1, ArgbToImU32(b.fgArgb), 0.0f, 0, 2.0f);
                    continue;
                }

                if (((b.bgArgb >> 24) & 0xFFu) != 0)
                {
                    bg->AddRectFilled(p0, p1, ArgbToImU32(b.bgArgb), rounding);
                }

                const std::size_t textOffset = static_cast<std::size_t>(b.textOffset);
                const std::size_t textLen = static_cast<std::size_t>(b.textLen);
                if (textLen == 0 || textOffset >= rt.overlayV2TextBlob.size())
                {
                    continue;
                }

                const std::size_t textEndOffset = std::min(rt.overlayV2TextBlob.size(), textOffset + textLen);
                if (textEndOffset <= textOffset)
                {
                    continue;
                }

                const char* textBegin = reinterpret_cast<const char*>(rt.overlayV2TextBlob.data() + textOffset);
                const char* textEnd = reinterpret_cast<const char*>(rt.overlayV2TextBlob.data() + textEndOffset);
                const float textX = x + pad;
                const float textY = y + pad;
                const float wrapWidth = (b.wrap != 0u) ? std::max(0.0f, w - (pad * 2.0f)) : 0.0f;
                const float fontPx = std::max(10.0f, b.fontPx * std::min(scaleX, scaleY));
                ImFont* font = (rt.overlayFont != nullptr) ? rt.overlayFont : ImGui::GetFont();

                fg->AddText(font, fontPx, ImVec2(textX, textY), ArgbToImU32(b.fgArgb), textBegin, textEnd, wrapWidth);
            }

            if (blockCount == 0 && !rt.hookSuccessIndicatorDone)
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
                        alpha = std::clamp(alpha, 0.0f, 1.0f);

                        const int plateA = static_cast<int>(170.0f * alpha + 0.5f);
                        const int ringA = static_cast<int>(235.0f * alpha + 0.5f);
                        const int tickA = static_cast<int>(245.0f * alpha + 0.5f);

                        const ImVec2 c(34.0f, 34.0f);
                        fg->AddCircleFilled(c, 16.0f, IM_COL32(16, 16, 16, plateA), 24);
                        fg->AddCircle(c, 15.0f, IM_COL32(83, 214, 108, ringA), 24, 2.0f);
                        fg->AddLine(
                            ImVec2(c.x - 6.0f, c.y + 0.5f),
                            ImVec2(c.x - 1.5f, c.y + 5.5f),
                            IM_COL32(255, 255, 255, tickA),
                            2.4f);
                        fg->AddLine(
                            ImVec2(c.x - 1.5f, c.y + 5.5f),
                            ImVec2(c.x + 8.0f, c.y - 5.0f),
                            IM_COL32(255, 255, 255, tickA),
                            2.4f);
                    }
                }
            }

            ImGui::Render();
        }
        bool SubmitPresentWorkLocked(
            VulkanRuntime& rt,
            VkQueue queue,
            VkSwapchainKHR swapchain,
            std::uint32_t imageIndex)
        {
            const auto perfBeginQpc = NowQpc();
            std::uint64_t perfAfterPrepQpc = perfBeginQpc;
            std::uint64_t perfAfterCommandRecordQpc = perfBeginQpc;
            std::uint64_t perfSubmitDurationQpc = 0;
            std::uint64_t perfWaitDurationQpc = 0;
            std::uint64_t perfMapDurationQpc = 0;
            std::uint64_t perfCpuCopyDurationQpc = 0;
            std::uint64_t perfWriteDurationQpc = 0;
            std::uint64_t perfCopyCommandDurationQpc = 0;
            std::uint64_t perfOverlayCommandDurationQpc = 0;

            const auto queueIt = rt.queues.find(queue);
            if (queueIt == rt.queues.end() || !queueIt->second.valid)
            {
                char detail[128]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "queue=%p queueCount=%zu", queue, rt.queues.size());
                LogCaptureSkipLocked(rt, CaptureSkipReason::QueueNotFound, detail);
                return false;
            }

            auto swapIt = rt.swapchains.find(swapchain);
            if (swapIt == rt.swapchains.end())
            {
                char detail[128]{};
                (void)_snprintf_s(
                    detail,
                    sizeof(detail),
                    _TRUNCATE,
                    "swapchain=0x%llX swapchainCount=%zu",
                    static_cast<unsigned long long>(SwapchainHandleToLogValue(swapchain)),
                    rt.swapchains.size());
                LogCaptureSkipLocked(rt, CaptureSkipReason::SwapchainNotFound, detail);
                return false;
            }

            auto& swapInfo = swapIt->second;
            if (swapInfo.images.empty())
            {
                (void)EnsureSwapchainImagesLocked(rt, swapInfo, swapchain);
            }
            if (swapInfo.images.empty() || imageIndex >= swapInfo.images.size())
            {
                char detail[160]{};
                (void)_snprintf_s(
                    detail,
                    sizeof(detail),
                    _TRUNCATE,
                    "swapchain=0x%llX imageIndex=%u imageCount=%zu",
                    static_cast<unsigned long long>(SwapchainHandleToLogValue(swapchain)),
                    imageIndex,
                    swapInfo.images.size());
                LogCaptureSkipLocked(rt, CaptureSkipReason::SwapchainImagesMissing, detail);
                return false;
            }

            const auto deviceIt = rt.devices.find(queueIt->second.device);
            if (deviceIt == rt.devices.end())
            {
                char detail[128]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "device=%p deviceCount=%zu", queueIt->second.device, rt.devices.size());
                LogCaptureSkipLocked(rt, CaptureSkipReason::DeviceNotFound, detail);
                return false;
            }

            const bool shouldCapture = ShouldCaptureNowLocked(rt, rt.lastPresentQpc);
            if (rt.overlayEnabled)
            {
                (void)RefreshOverlayV2Locked(rt);
            }

            const bool hasOverlayBlocks = rt.overlayEnabled && !rt.overlayV2Blocks.empty() && rt.lastOverlayV2Seq != 0;
            const bool shouldDrawHookSuccessIndicator = rt.overlayEnabled && !rt.hookSuccessIndicatorDone;
            const bool shouldRenderOverlay = hasOverlayBlocks || shouldDrawHookSuccessIndicator;
            LogPresentSummaryLocked(rt, shouldCapture, hasOverlayBlocks, rt.swapchains.size());
            if (!shouldCapture && !shouldRenderOverlay)
            {
                return true;
            }

            if (!EnsureQueueGpuStateLocked(rt, queue, queueIt->second, deviceIt->second, swapInfo))
            {
                LogCaptureSkipLocked(rt, CaptureSkipReason::EnsureQueueGpuStateFailed, "ensure_queue_gpu_state_failed");
                return false;
            }

            auto queueStateIt = rt.queueGpuStates.find(queue);
            if (queueStateIt == rt.queueGpuStates.end())
            {
                char detail[128]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "queue=%p gpuStateCount=%zu", queue, rt.queueGpuStates.size());
                LogCaptureSkipLocked(rt, CaptureSkipReason::QueueStateNotFound, detail);
                return false;
            }
            auto& gpu = queueStateIt->second;
            perfAfterPrepQpc = NowQpc();

            const auto emitPresentPerfLog = [&](const char* outcome)
            {
                if (!rt.perfDiagLogEnabled)
                {
                    return;
                }

                const auto perfNowQpc = NowQpc();
                if (!ShouldEmitDiagLog(perfNowQpc, rt.qpcFreq, rt.lastPresentPerfLogQpc, kDiagSummaryIntervalMs))
                {
                    return;
                }

                DebugLog(
                    "stage=hook_vulkan event=present_perf pid=%lu presentCount=%llu outcome=%s shouldCapture=%d overlayEnabled=%d hasOverlayBlocks=%d size=%ux%u prepMs=%.2f cmdRecordMs=%.2f copyCmdMs=%.2f overlayCmdMs=%.2f submitMs=%.2f waitMs=%.2f mapMs=%.2f cpuCopyMs=%.2f writeMs=%.2f totalMs=%.2f.",
                    static_cast<unsigned long>(GetCurrentProcessId()),
                    static_cast<unsigned long long>(rt.presentCount),
                    outcome != nullptr ? outcome : "unknown",
                    shouldCapture ? 1 : 0,
                    rt.overlayEnabled ? 1 : 0,
                    hasOverlayBlocks ? 1 : 0,
                    gpu.width,
                    gpu.height,
                    QpcDeltaToMs(perfAfterPrepQpc - perfBeginQpc, rt.qpcFreq),
                    QpcDeltaToMs(perfAfterCommandRecordQpc - perfAfterPrepQpc, rt.qpcFreq),
                    QpcDeltaToMs(perfCopyCommandDurationQpc, rt.qpcFreq),
                    QpcDeltaToMs(perfOverlayCommandDurationQpc, rt.qpcFreq),
                    QpcDeltaToMs(perfSubmitDurationQpc, rt.qpcFreq),
                    QpcDeltaToMs(perfWaitDurationQpc, rt.qpcFreq),
                    QpcDeltaToMs(perfMapDurationQpc, rt.qpcFreq),
                    QpcDeltaToMs(perfCpuCopyDurationQpc, rt.qpcFreq),
                    QpcDeltaToMs(perfWriteDurationQpc, rt.qpcFreq),
                    QpcDeltaToMs(perfNowQpc - perfBeginQpc, rt.qpcFreq));
            };

            OverlaySwapchainState* ovl = nullptr;
            if (shouldRenderOverlay)
            {
                if (!EnsureOverlaySwapchainStateLocked(rt, swapchain, swapInfo))
                {
                    LogCaptureSkipLocked(rt, CaptureSkipReason::OverlaySwapchainStateFailed, "ensure_overlay_swapchain_state_failed");
                    return false;
                }

                auto ovlIt = rt.overlaySwapchains.find(swapchain);
                if (ovlIt == rt.overlaySwapchains.end())
                {
                    LogCaptureSkipLocked(rt, CaptureSkipReason::OverlayStateMissing, "overlay_swapchain_state_missing");
                    return false;
                }
                ovl = &ovlIt->second;
                if (ovl->framebuffers.empty() || imageIndex >= ovl->framebuffers.size())
                {
                    char detail[160]{};
                    (void)_snprintf_s(
                        detail,
                        sizeof(detail),
                        _TRUNCATE,
                        "imageIndex=%u framebufferCount=%zu",
                        imageIndex,
                        ovl->framebuffers.size());
                    LogCaptureSkipLocked(rt, CaptureSkipReason::OverlayFramebufferMissing, detail);
                    return false;
                }

                if (!EnsureImGuiLocked(rt, queue, swapchain, queueIt->second, deviceIt->second, *ovl))
                {
                    LogCaptureSkipLocked(rt, CaptureSkipReason::EnsureImGuiFailed, "ensure_imgui_failed");
                    return false;
                }
            }

            const auto resetPoolResult = vkResetCommandPool(gpu.device, gpu.commandPool, 0);
            if (resetPoolResult != VK_SUCCESS)
            {
                char detail[96]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "vk=%d", static_cast<int>(resetPoolResult));
                LogCaptureSkipLocked(rt, CaptureSkipReason::VkResetCommandPoolFailed, detail);
                return false;
            }
            const auto resetFenceResult = vkResetFences(gpu.device, 1, &gpu.fence);
            if (resetFenceResult != VK_SUCCESS)
            {
                char detail[96]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "vk=%d", static_cast<int>(resetFenceResult));
                LogCaptureSkipLocked(rt, CaptureSkipReason::VkResetFencesFailed, detail);
                return false;
            }

            VkCommandBufferBeginInfo beginInfo{};
            beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
            beginInfo.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
            const auto beginResult = vkBeginCommandBuffer(gpu.commandBuffer, &beginInfo);
            if (beginResult != VK_SUCCESS)
            {
                char detail[96]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "vk=%d", static_cast<int>(beginResult));
                LogCaptureSkipLocked(rt, CaptureSkipReason::VkBeginCommandBufferFailed, detail);
                return false;
            }

            const VkImage targetImage = swapInfo.images[imageIndex];
            if (targetImage == VK_NULL_HANDLE)
            {
                (void)vkEndCommandBuffer(gpu.commandBuffer);
                LogCaptureSkipLocked(rt, CaptureSkipReason::TargetImageNull, "target_image_null");
                return false;
            }

            bool inTransferLayout = false;
            if (shouldCapture)
            {
                const auto copyCmdBeginQpc = NowQpc();
                CmdTransitionImageLayout(
                    gpu.commandBuffer,
                    targetImage,
                    VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    VK_ACCESS_MEMORY_READ_BIT,
                    VK_ACCESS_TRANSFER_READ_BIT,
                    VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                    VK_PIPELINE_STAGE_TRANSFER_BIT);
                inTransferLayout = true;

                VkBufferImageCopy region{};
                region.bufferOffset = 0;
                region.bufferRowLength = 0;
                region.bufferImageHeight = 0;
                region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
                region.imageSubresource.mipLevel = 0;
                region.imageSubresource.baseArrayLayer = 0;
                region.imageSubresource.layerCount = 1;
                region.imageOffset = {0, 0, 0};
                region.imageExtent = {gpu.width, gpu.height, 1};
                vkCmdCopyImageToBuffer(
                    gpu.commandBuffer,
                    targetImage,
                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    gpu.stagingBuffer,
                    1,
                    &region);
                perfCopyCommandDurationQpc += (NowQpc() - copyCmdBeginQpc);
            }

            if (shouldRenderOverlay && ovl != nullptr)
            {
                const auto overlayCmdBeginQpc = NowQpc();
                if (inTransferLayout)
                {
                    CmdTransitionImageLayout(
                        gpu.commandBuffer,
                        targetImage,
                        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                        VK_ACCESS_TRANSFER_READ_BIT,
                        VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                        VK_PIPELINE_STAGE_TRANSFER_BIT,
                        VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
                }
                else
                {
                    CmdTransitionImageLayout(
                        gpu.commandBuffer,
                        targetImage,
                        VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
                        VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                        VK_ACCESS_MEMORY_READ_BIT,
                        VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                        VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                        VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
                }

                BuildImGuiOverlayDrawDataLocked(rt, gpu.width, gpu.height);

                VkRenderPassBeginInfo rpBegin{};
                rpBegin.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
                rpBegin.renderPass = ovl->renderPass;
                rpBegin.framebuffer = ovl->framebuffers[imageIndex];
                rpBegin.renderArea.offset = {0, 0};
                rpBegin.renderArea.extent = {gpu.width, gpu.height};
                vkCmdBeginRenderPass(gpu.commandBuffer, &rpBegin, VK_SUBPASS_CONTENTS_INLINE);
                ImGui_ImplVulkan_RenderDrawData(ImGui::GetDrawData(), gpu.commandBuffer);
                vkCmdEndRenderPass(gpu.commandBuffer);

                CmdTransitionImageLayout(
                    gpu.commandBuffer,
                    targetImage,
                    VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                    VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
                    VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                    VK_ACCESS_MEMORY_READ_BIT,
                    VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                    VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);
                perfOverlayCommandDurationQpc += (NowQpc() - overlayCmdBeginQpc);
            }
            else if (inTransferLayout)
            {
                CmdTransitionImageLayout(
                    gpu.commandBuffer,
                    targetImage,
                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
                    VK_ACCESS_TRANSFER_READ_BIT,
                    VK_ACCESS_MEMORY_READ_BIT,
                    VK_PIPELINE_STAGE_TRANSFER_BIT,
                    VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);
            }

            const auto endResult = vkEndCommandBuffer(gpu.commandBuffer);
            if (endResult != VK_SUCCESS)
            {
                char detail[96]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "vk=%d", static_cast<int>(endResult));
                LogCaptureSkipLocked(rt, CaptureSkipReason::VkEndCommandBufferFailed, detail);
                return false;
            }
            perfAfterCommandRecordQpc = NowQpc();

            VkSubmitInfo submitInfo{};
            submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
            submitInfo.commandBufferCount = 1;
            submitInfo.pCommandBuffers = &gpu.commandBuffer;
            const auto submitBeginQpc = NowQpc();
            const auto submitResult = vkQueueSubmit(queue, 1, &submitInfo, gpu.fence);
            perfSubmitDurationQpc = NowQpc() - submitBeginQpc;
            if (submitResult != VK_SUCCESS)
            {
                char detail[96]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "vk=%d", static_cast<int>(submitResult));
                LogCaptureSkipLocked(rt, CaptureSkipReason::VkQueueSubmitFailed, detail);
                return false;
            }

            const auto waitBeginQpc = NowQpc();
            const auto waitResult = vkWaitForFences(gpu.device, 1, &gpu.fence, VK_TRUE, 1'000'000'000ull);
            perfWaitDurationQpc = NowQpc() - waitBeginQpc;
            if (waitResult != VK_SUCCESS)
            {
                char detail[96]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "vk=%d", static_cast<int>(waitResult));
                LogCaptureSkipLocked(rt, CaptureSkipReason::VkWaitForFencesFailed, detail);
                return false;
            }

            rt.lastFormat = static_cast<std::uint32_t>(gpu.format);
            rt.lastWidth = gpu.width;
            rt.lastHeight = gpu.height;

            if (!shouldCapture)
            {
                emitPresentPerfLog("overlay_only");
                return true;
            }

            bool rgbaNeedsSwap = false;
            if (!IsCaptureFormatSupported(gpu.format, rgbaNeedsSwap))
            {
                char detail[96]{};
                (void)_snprintf_s(detail, sizeof(detail), _TRUNCATE, "format=%u", static_cast<std::uint32_t>(gpu.format));
                LogCaptureSkipLocked(rt, CaptureSkipReason::CaptureFormatUnsupported, detail);
                return false;
            }

            const auto mapBeginQpc = NowQpc();
            const auto* mapped = static_cast<const std::uint8_t*>(gpu.stagingMapped);
            perfMapDurationQpc = NowQpc() - mapBeginQpc;
            if (mapped == nullptr)
            {
                char detail[128]{};
                (void)_snprintf_s(
                    detail,
                    sizeof(detail),
                    _TRUNCATE,
                    "persistent_mapped=%d bytes=%llu",
                    gpu.stagingMapped != nullptr ? 1 : 0,
                    static_cast<unsigned long long>(gpu.stagingBytes));
                LogCaptureSkipLocked(rt, CaptureSkipReason::VkMapMemoryFailed, detail);
                emitPresentPerfLog("map_failed");
                return false;
            }

            const auto copyBeginQpc = NowQpc();
            const auto* src = mapped;
            const auto bytes = static_cast<std::size_t>(gpu.stagingBytes);
            if (gpu.scratch.size() < bytes)
            {
                gpu.scratch.resize(bytes);
            }

            if (!rgbaNeedsSwap)
            {
                std::memcpy(gpu.scratch.data(), src, bytes);
            }
            else
            {
                for (std::size_t i = 0; i + 3 < bytes; i += 4)
                {
                    gpu.scratch[i + 0] = src[i + 2];
                    gpu.scratch[i + 1] = src[i + 1];
                    gpu.scratch[i + 2] = src[i + 0];
                    gpu.scratch[i + 3] = src[i + 3];
                }
            }
            perfCpuCopyDurationQpc = NowQpc() - copyBeginQpc;

            const auto pid = GetCurrentProcessId();
            const auto ts = NowQpc();
            const std::uint32_t stride = gpu.width * 4u;
            const auto writeBeginQpc = NowQpc();
            const bool wrote = rt.frameWriter.WriteFrame(
                pid,
                ipc::GraphicsApi::Vulkan,
                ++rt.frameId,
                gpu.width,
                gpu.height,
                stride,
                ts,
                gpu.scratch.data(),
                bytes);
            perfWriteDurationQpc = NowQpc() - writeBeginQpc;
            if (!wrote)
            {
                const auto now = NowQpc();
                if (ShouldEmitDiagLog(now, rt.qpcFreq, rt.lastWriteFrameFailQpc, kDiagLogMinIntervalMs))
                {
                    DebugLog(
                        "stage=hook_vulkan event=write_frame result=fail pid=%lu frameId=%llu width=%u height=%u bytes=%zu map=Local\\HT_HOOK_FRAME_4_%lu.",
                        static_cast<unsigned long>(pid),
                        static_cast<unsigned long long>(rt.frameId),
                        gpu.width,
                        gpu.height,
                        bytes,
                        static_cast<unsigned long>(pid));
                }
                LogCaptureSkipLocked(rt, CaptureSkipReason::WriteFrameFailed, "shared_frame_write_failed");
                emitPresentPerfLog("write_failed");
                return false;
            }

            if (ShouldEmitDiagLog(ts, rt.qpcFreq, rt.lastWriteFrameOkQpc, kDiagSummaryIntervalMs))
            {
                DebugLog(
                    "stage=hook_vulkan event=write_frame result=ok pid=%lu frameId=%llu width=%u height=%u bytes=%zu map=Local\\HT_HOOK_FRAME_4_%lu.",
                    static_cast<unsigned long>(pid),
                    static_cast<unsigned long long>(rt.frameId),
                    gpu.width,
                    gpu.height,
                    bytes,
                    static_cast<unsigned long>(pid));
            }

            rt.lastCaptureQpc = ts;
            rt.lastFrameWriteQpc = ts;
            emitPresentPerfLog("capture_ok");
            return true;
        }

        void PublishStatusLocked(VulkanRuntime& rt)
        {
            const auto pid = GetCurrentProcessId();
            if (!rt.statusWriter.Ensure(pid, ipc::GraphicsApi::Vulkan))
            {
                return;
            }

            ipc::HookStatusHeader status{};
            status.api = static_cast<std::uint32_t>(ipc::GraphicsApi::Vulkan);
            status.targetPid = pid;
            status.presentCount = rt.presentCount;
            status.lastPresentQpc = rt.lastPresentQpc;
            status.lastPresentKind = rt.lastPresentKind;
            status.backBufferDxgiFormat = rt.lastFormat;
            status.backBufferWidth = rt.lastWidth;
            status.backBufferHeight = rt.lastHeight;
            status.stagingDxgiFormat = rt.lastFormat;
            status.lastFrameIdWritten = rt.frameId;
            status.lastFrameWriteQpc = rt.lastFrameWriteQpc;
            status.lastCmdQpc = rt.lastOverlayV2Qpc;
            status.lastCmdCount = rt.overlayV2Header.textBlockCount;
            (void)rt.statusWriter.Write(status);
        }

        template <typename T>
        void StoreOriginalIfUnset(T& target, PFN_vkVoidFunction fn)
        {
            if (target == nullptr && fn != nullptr)
            {
                target = reinterpret_cast<T>(fn);
            }
        }

        bool HookExport(HMODULE module, const char* exportName, void* detour, void** original)
        {
            if (module == nullptr || exportName == nullptr || detour == nullptr || original == nullptr)
            {
                return false;
            }

            const auto target = reinterpret_cast<void*>(GetProcAddress(module, exportName));
            if (target == nullptr)
            {
                return false;
            }

            if (*original != nullptr)
            {
                return true;
            }

            const auto createStatus = MH_CreateHook(target, detour, original);
            if (createStatus != MH_OK && createStatus != MH_ERROR_ALREADY_CREATED)
            {
                return false;
            }

            const auto enableStatus = MH_EnableHook(target);
            return enableStatus == MH_OK || enableStatus == MH_ERROR_ENABLED;
        }

        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkCreateInstance(
            const VkInstanceCreateInfo* createInfo,
            const VkAllocationCallbacks* allocator,
            VkInstance* instance);

        VKAPI_ATTR void VKAPI_CALL Hook_vkDestroyInstance(VkInstance instance, const VkAllocationCallbacks* allocator);
        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkCreateDevice(
            VkPhysicalDevice physicalDevice,
            const VkDeviceCreateInfo* createInfo,
            const VkAllocationCallbacks* allocator,
            VkDevice* device);
        VKAPI_ATTR void VKAPI_CALL Hook_vkDestroyDevice(VkDevice device, const VkAllocationCallbacks* allocator);
        VKAPI_ATTR void VKAPI_CALL Hook_vkGetDeviceQueue(
            VkDevice device,
            std::uint32_t queueFamilyIndex,
            std::uint32_t queueIndex,
            VkQueue* queue);
        VKAPI_ATTR void VKAPI_CALL Hook_vkGetDeviceQueue2(
            VkDevice device,
            const VkDeviceQueueInfo2* queueInfo,
            VkQueue* queue);
        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkCreateSwapchainKHR(
            VkDevice device,
            const VkSwapchainCreateInfoKHR* createInfo,
            const VkAllocationCallbacks* allocator,
            VkSwapchainKHR* swapchain);
        VKAPI_ATTR void VKAPI_CALL Hook_vkDestroySwapchainKHR(
            VkDevice device,
            VkSwapchainKHR swapchain,
            const VkAllocationCallbacks* allocator);
        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkAcquireNextImageKHR(
            VkDevice device,
            VkSwapchainKHR swapchain,
            std::uint64_t timeout,
            VkSemaphore semaphore,
            VkFence fence,
            std::uint32_t* imageIndex);
        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkAcquireNextImage2KHR(
            VkDevice device,
            const VkAcquireNextImageInfoKHR* acquireInfo,
            std::uint32_t* imageIndex);
        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkQueuePresentKHR(VkQueue queue, const VkPresentInfoKHR* presentInfo);
        VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL Hook_vkGetDeviceProcAddr(VkDevice device, const char* functionName)
        {
            auto& rt = g_rt;
            PFN_vkVoidFunction resolved = nullptr;
            if (rt.originalGetDeviceProcAddr != nullptr)
            {
                resolved = rt.originalGetDeviceProcAddr(device, functionName);
            }

            DebugLog(
                "stage=hook_vulkan event=hit_vkGetDeviceProcAddr device=%p name=%s resolved=%p.",
                device,
                functionName != nullptr ? functionName : "(null)",
                reinterpret_cast<void*>(resolved));

            if (functionName == nullptr)
            {
                return resolved;
            }

            if (std::strcmp(functionName, "vkGetDeviceQueue") == 0)
            {
                StoreOriginalIfUnset(rt.originalGetDeviceQueue, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkGetDeviceQueue);
            }
            if (std::strcmp(functionName, "vkGetDeviceQueue2") == 0)
            {
                StoreOriginalIfUnset(rt.originalGetDeviceQueue2, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkGetDeviceQueue2);
            }
            if (std::strcmp(functionName, "vkCreateSwapchainKHR") == 0)
            {
                StoreOriginalIfUnset(rt.originalCreateSwapchainKHR, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkCreateSwapchainKHR);
            }
            if (std::strcmp(functionName, "vkDestroyDevice") == 0)
            {
                StoreOriginalIfUnset(rt.originalDestroyDevice, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkDestroyDevice);
            }
            if (std::strcmp(functionName, "vkDestroySwapchainKHR") == 0)
            {
                StoreOriginalIfUnset(rt.originalDestroySwapchainKHR, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkDestroySwapchainKHR);
            }
            if (std::strcmp(functionName, "vkAcquireNextImageKHR") == 0)
            {
                StoreOriginalIfUnset(rt.originalAcquireNextImageKHR, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkAcquireNextImageKHR);
            }
            if (std::strcmp(functionName, "vkAcquireNextImage2KHR") == 0)
            {
                StoreOriginalIfUnset(rt.originalAcquireNextImage2KHR, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkAcquireNextImage2KHR);
            }
            if (std::strcmp(functionName, "vkGetSwapchainImagesKHR") == 0)
            {
                StoreOriginalIfUnset(rt.originalGetSwapchainImagesKHR, resolved);
                return resolved;
            }
            if (std::strcmp(functionName, "vkQueuePresentKHR") == 0)
            {
                StoreOriginalIfUnset(rt.originalQueuePresentKHR, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkQueuePresentKHR);
            }

            return resolved;
        }

        VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL Hook_vkGetInstanceProcAddr(VkInstance instance, const char* functionName)
        {
            auto& rt = g_rt;
            PFN_vkVoidFunction resolved = nullptr;
            if (rt.originalGetInstanceProcAddr != nullptr)
            {
                resolved = rt.originalGetInstanceProcAddr(instance, functionName);
            }

            DebugLog(
                "stage=hook_vulkan event=hit_vkGetInstanceProcAddr instance=%p name=%s resolved=%p.",
                instance,
                functionName != nullptr ? functionName : "(null)",
                reinterpret_cast<void*>(resolved));

            if (instance != VK_NULL_HANDLE)
            {
                rt.instance = instance;
            }

            if (functionName == nullptr)
            {
                return resolved;
            }

            if (std::strcmp(functionName, "vkCreateInstance") == 0)
            {
                StoreOriginalIfUnset(rt.originalCreateInstance, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkCreateInstance);
            }
            if (std::strcmp(functionName, "vkDestroyInstance") == 0)
            {
                StoreOriginalIfUnset(rt.originalDestroyInstance, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkDestroyInstance);
            }
            if (std::strcmp(functionName, "vkCreateDevice") == 0)
            {
                StoreOriginalIfUnset(rt.originalCreateDevice, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkCreateDevice);
            }

            return resolved;
        }

        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkCreateInstance(
            const VkInstanceCreateInfo* createInfo,
            const VkAllocationCallbacks* allocator,
            VkInstance* instance)
        {
            auto& rt = g_rt;
            const auto original = rt.originalCreateInstance;
            if (original == nullptr)
            {
                return VK_ERROR_INITIALIZATION_FAILED;
            }

            const auto result = original(createInfo, allocator, instance);
            if (result == VK_SUCCESS && instance != nullptr && *instance != VK_NULL_HANDLE)
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                rt.instance = *instance;
                if (createInfo != nullptr && createInfo->pApplicationInfo != nullptr)
                {
                    const auto requested = createInfo->pApplicationInfo->apiVersion;
                    if (requested != 0)
                    {
                        rt.apiVersion = requested;
                    }
                }
            }
            return result;
        }

        VKAPI_ATTR void VKAPI_CALL Hook_vkDestroyInstance(VkInstance instance, const VkAllocationCallbacks* allocator)
        {
            auto& rt = g_rt;
            const auto original = rt.originalDestroyInstance;
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                if (rt.instance == instance)
                {
                    rt.instance = VK_NULL_HANDLE;
                }
            }

            if (original != nullptr)
            {
                original(instance, allocator);
            }
        }

        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkCreateDevice(
            VkPhysicalDevice physicalDevice,
            const VkDeviceCreateInfo* createInfo,
            const VkAllocationCallbacks* allocator,
            VkDevice* device)
        {
            if (!g_loggedFirstCreateDeviceHit.exchange(true))
            {
                DebugLog(
                    "stage=hook_vulkan event=first_hit_vkCreateDevice physicalDevice=%p createInfo=%p.",
                    physicalDevice,
                    createInfo);
            }

            auto& rt = g_rt;
            const auto original = rt.originalCreateDevice;
            if (original == nullptr)
            {
                return VK_ERROR_INITIALIZATION_FAILED;
            }

            const auto result = original(physicalDevice, createInfo, allocator, device);
            if (result == VK_SUCCESS && device != nullptr && *device != VK_NULL_HANDLE)
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                rt.devices[*device] = DeviceInfo{physicalDevice};
            }
            return result;
        }

        VKAPI_ATTR void VKAPI_CALL Hook_vkDestroyDevice(VkDevice device, const VkAllocationCallbacks* allocator)
        {
            auto& rt = g_rt;
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                RemoveDeviceStateLocked(rt, device);
            }

            const auto original = rt.originalDestroyDevice;
            if (original != nullptr)
            {
                original(device, allocator);
            }
        }

        VKAPI_ATTR void VKAPI_CALL Hook_vkGetDeviceQueue(
            VkDevice device,
            std::uint32_t queueFamilyIndex,
            std::uint32_t queueIndex,
            VkQueue* queue)
        {
            auto& rt = g_rt;
            const auto original = rt.originalGetDeviceQueue;
            if (original == nullptr)
            {
                return;
            }

            original(device, queueFamilyIndex, queueIndex, queue);
            if (queue != nullptr && *queue != VK_NULL_HANDLE)
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                rt.queues[*queue] = QueueInfo{device, queueFamilyIndex, true};
            }
        }

        VKAPI_ATTR void VKAPI_CALL Hook_vkGetDeviceQueue2(
            VkDevice device,
            const VkDeviceQueueInfo2* queueInfo,
            VkQueue* queue)
        {
            auto& rt = g_rt;
            const auto original = rt.originalGetDeviceQueue2;
            if (original == nullptr)
            {
                return;
            }

            original(device, queueInfo, queue);
            if (queueInfo != nullptr && queue != nullptr && *queue != VK_NULL_HANDLE)
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                rt.queues[*queue] = QueueInfo{device, queueInfo->queueFamilyIndex, true};
            }
        }

        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkCreateSwapchainKHR(
            VkDevice device,
            const VkSwapchainCreateInfoKHR* createInfo,
            const VkAllocationCallbacks* allocator,
            VkSwapchainKHR* swapchain)
        {
            if (!g_loggedFirstCreateSwapchainHit.exchange(true))
            {
                const std::uint32_t width = (createInfo != nullptr) ? createInfo->imageExtent.width : 0u;
                const std::uint32_t height = (createInfo != nullptr) ? createInfo->imageExtent.height : 0u;
                const std::uint32_t format = (createInfo != nullptr)
                    ? static_cast<std::uint32_t>(createInfo->imageFormat)
                    : 0u;
                DebugLog(
                    "stage=hook_vulkan event=first_hit_vkCreateSwapchainKHR device=%p format=%u extent=%ux%u createInfo=%p.",
                    device,
                    format,
                    width,
                    height,
                    createInfo);
            }

            auto& rt = g_rt;
            const auto original = rt.originalCreateSwapchainKHR;
            if (original == nullptr)
            {
                return VK_ERROR_INITIALIZATION_FAILED;
            }

            const auto result = original(device, createInfo, allocator, swapchain);
            if (result == VK_SUCCESS && swapchain != nullptr && *swapchain != VK_NULL_HANDLE)
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                UpsertSwapchainFromCreateLocked(rt, device, *swapchain, createInfo);
            }
            return result;
        }

        VKAPI_ATTR void VKAPI_CALL Hook_vkDestroySwapchainKHR(
            VkDevice device,
            VkSwapchainKHR swapchain,
            const VkAllocationCallbacks* allocator)
        {
            auto& rt = g_rt;
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                auto ovlIt = rt.overlaySwapchains.find(swapchain);
                if (ovlIt != rt.overlaySwapchains.end())
                {
                    DestroyOverlaySwapchainState(device, ovlIt->second);
                    rt.overlaySwapchains.erase(ovlIt);
                }
                rt.swapchains.erase(swapchain);
            }

            const auto original = rt.originalDestroySwapchainKHR;
            if (original != nullptr)
            {
                original(device, swapchain, allocator);
            }
        }
        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkAcquireNextImageKHR(
            VkDevice device,
            VkSwapchainKHR swapchain,
            std::uint64_t timeout,
            VkSemaphore semaphore,
            VkFence fence,
            std::uint32_t* imageIndex)
        {
            auto& rt = g_rt;
            const auto original = rt.originalAcquireNextImageKHR;
            if (original == nullptr)
            {
                return VK_ERROR_INITIALIZATION_FAILED;
            }

            const auto result = original(device, swapchain, timeout, semaphore, fence, imageIndex);
            if (result == VK_SUCCESS || result == VK_SUBOPTIMAL_KHR)
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                UpsertSwapchainFromAcquireLocked(rt, device, swapchain);
            }
            return result;
        }

        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkAcquireNextImage2KHR(
            VkDevice device,
            const VkAcquireNextImageInfoKHR* acquireInfo,
            std::uint32_t* imageIndex)
        {
            auto& rt = g_rt;
            const auto original = rt.originalAcquireNextImage2KHR;
            if (original == nullptr)
            {
                return VK_ERROR_INITIALIZATION_FAILED;
            }

            const auto result = original(device, acquireInfo, imageIndex);
            if ((result == VK_SUCCESS || result == VK_SUBOPTIMAL_KHR) && acquireInfo != nullptr)
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                UpsertSwapchainFromAcquireLocked(rt, device, acquireInfo->swapchain);
            }
            return result;
        }

        VKAPI_ATTR VkResult VKAPI_CALL Hook_vkQueuePresentKHR(VkQueue queue, const VkPresentInfoKHR* presentInfo)
        {
            auto& rt = g_rt;
            PFN_vkQueuePresentKHR original = nullptr;
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                rt.presentCount++;
                rt.lastPresentQpc = NowQpc();
                rt.lastPresentKind = 1;
                if (ShouldEmitDiagLog(
                        rt.lastPresentQpc,
                        rt.qpcFreq,
                        rt.lastQueuePresentEnterLogQpc,
                        kDiagSummaryIntervalMs))
                {
                    const auto swapchainCount = (presentInfo != nullptr) ? presentInfo->swapchainCount : 0u;
                    DebugLog(
                        "stage=hook_vulkan event=queue_present_enter pid=%lu presentCount=%llu swapchainCount=%u queue=%p.",
                        static_cast<unsigned long>(GetCurrentProcessId()),
                        static_cast<unsigned long long>(rt.presentCount),
                        swapchainCount,
                        queue);
                }

                (void)EnsureConfigRefreshedLocked(rt);

                if (presentInfo != nullptr &&
                    presentInfo->swapchainCount > 0 &&
                    presentInfo->pSwapchains != nullptr)
                {
                    const std::uint32_t imageIndex =
                        (presentInfo->pImageIndices != nullptr)
                            ? presentInfo->pImageIndices[0]
                            : 0u;
                    const VkSwapchainKHR swapchain = presentInfo->pSwapchains[0];
                    (void)SubmitPresentWorkLocked(rt, queue, swapchain, imageIndex);
                }

                PublishStatusLocked(rt);
                original = rt.originalQueuePresentKHR;
            }

            if (original == nullptr)
            {
                return VK_ERROR_INITIALIZATION_FAILED;
            }

            return original(queue, presentInfo);
        }
    }

    bool InstallPresentHook()
    {
        auto& rt = g_rt;
        std::lock_guard<std::mutex> lock(rt.mutex);
        DebugLogInstall(
            "stage=hook_vulkan event=agent_loaded pid=%lu.",
            static_cast<unsigned long>(GetCurrentProcessId()));
        if (rt.installed.load())
        {
            DebugLogInstall(
                "stage=hook_vulkan event=install_hook_result result=already_installed pid=%lu.",
                static_cast<unsigned long>(GetCurrentProcessId()));
            return true;
        }

        const auto initStatus = MH_Initialize();
        if (initStatus != MH_OK && initStatus != MH_ERROR_ALREADY_INITIALIZED)
        {
            DebugLogInstall(
                "stage=hook_vulkan event=install_hook_result result=fail reason=mh_initialize_failed status=%d pid=%lu.",
                static_cast<int>(initStatus),
                static_cast<unsigned long>(GetCurrentProcessId()));
            return false;
        }

        HMODULE vulkanModule = GetModuleHandleW(L"vulkan-1.dll");
        if (vulkanModule == nullptr)
        {
            vulkanModule = LoadLibraryW(L"vulkan-1.dll");
        }
        if (vulkanModule == nullptr)
        {
            (void)MH_Uninitialize();
            DebugLogInstall(
                "stage=hook_vulkan event=install_hook_result result=fail reason=vulkan_module_not_found pid=%lu.",
                static_cast<unsigned long>(GetCurrentProcessId()));
            return false;
        }

        auto hookAndLog = [&](const char* apiName, void* detour, void** original) -> bool
        {
            const bool ok = HookExport(vulkanModule, apiName, detour, original);
            DebugLogInstall(
                "stage=hook_vulkan event=install_hook_api api=%s result=%s.",
                apiName != nullptr ? apiName : "(null)",
                ok ? "ok" : "fail");
            return ok;
        };

        bool hookedAny = false;
        hookedAny |= hookAndLog(
            "vkGetDeviceProcAddr",
            reinterpret_cast<void*>(&Hook_vkGetDeviceProcAddr),
            reinterpret_cast<void**>(&rt.originalGetDeviceProcAddr));
        hookedAny |= hookAndLog(
            "vkGetInstanceProcAddr",
            reinterpret_cast<void*>(&Hook_vkGetInstanceProcAddr),
            reinterpret_cast<void**>(&rt.originalGetInstanceProcAddr));
        hookedAny |= hookAndLog(
            "vkCreateInstance",
            reinterpret_cast<void*>(&Hook_vkCreateInstance),
            reinterpret_cast<void**>(&rt.originalCreateInstance));
        hookedAny |= hookAndLog(
            "vkDestroyInstance",
            reinterpret_cast<void*>(&Hook_vkDestroyInstance),
            reinterpret_cast<void**>(&rt.originalDestroyInstance));
        if (kEnableDirectDeviceExportHooks)
        {
            hookedAny |= hookAndLog(
                "vkCreateDevice",
                reinterpret_cast<void*>(&Hook_vkCreateDevice),
                reinterpret_cast<void**>(&rt.originalCreateDevice));
            hookedAny |= hookAndLog(
                "vkDestroyDevice",
                reinterpret_cast<void*>(&Hook_vkDestroyDevice),
                reinterpret_cast<void**>(&rt.originalDestroyDevice));
            hookedAny |= hookAndLog(
                "vkGetDeviceQueue",
                reinterpret_cast<void*>(&Hook_vkGetDeviceQueue),
                reinterpret_cast<void**>(&rt.originalGetDeviceQueue));
            hookedAny |= hookAndLog(
                "vkGetDeviceQueue2",
                reinterpret_cast<void*>(&Hook_vkGetDeviceQueue2),
                reinterpret_cast<void**>(&rt.originalGetDeviceQueue2));
            hookedAny |= hookAndLog(
                "vkCreateSwapchainKHR",
                reinterpret_cast<void*>(&Hook_vkCreateSwapchainKHR),
                reinterpret_cast<void**>(&rt.originalCreateSwapchainKHR));
            hookedAny |= hookAndLog(
                "vkDestroySwapchainKHR",
                reinterpret_cast<void*>(&Hook_vkDestroySwapchainKHR),
                reinterpret_cast<void**>(&rt.originalDestroySwapchainKHR));
            hookedAny |= hookAndLog(
                "vkAcquireNextImageKHR",
                reinterpret_cast<void*>(&Hook_vkAcquireNextImageKHR),
                reinterpret_cast<void**>(&rt.originalAcquireNextImageKHR));
            hookedAny |= hookAndLog(
                "vkAcquireNextImage2KHR",
                reinterpret_cast<void*>(&Hook_vkAcquireNextImage2KHR),
                reinterpret_cast<void**>(&rt.originalAcquireNextImage2KHR));
            hookedAny |= hookAndLog(
                "vkQueuePresentKHR",
                reinterpret_cast<void*>(&Hook_vkQueuePresentKHR),
                reinterpret_cast<void**>(&rt.originalQueuePresentKHR));
        }
        else
        {
            // WHY: Some 32-bit loader exports in DXVK-backed titles are unstable to patch directly; procaddr hooks still cover device-level entry points.
            DebugLogInstall(
                "stage=hook_vulkan event=install_hook_plan path=procaddr_only scope=device_level reason=x86_direct_export_unstable pid=%lu.",
                static_cast<unsigned long>(GetCurrentProcessId()));
        }

        if (!hookedAny)
        {
            (void)MH_DisableHook(MH_ALL_HOOKS);
            (void)MH_Uninitialize();
            DebugLogInstall(
                "stage=hook_vulkan event=install_hook_result result=fail reason=no_exports_hooked pid=%lu.",
                static_cast<unsigned long>(GetCurrentProcessId()));
            return false;
        }

        rt.qpcFreq = QueryQpcFreq();
        rt.captureIntervalQpc = (rt.qpcFreq > 0) ? (rt.qpcFreq / kDefaultCaptureFps) : 0;
        rt.configuredFpsLimit = kDefaultCaptureFps;
        rt.overlayEnabled = false;
        rt.hookSuccessIndicatorArmed = true;
        rt.hookSuccessIndicatorDone = false;
        rt.hookSuccessIndicatorStartQpc = 0;
        rt.installed.store(true);
        DebugLogInstall(
            "stage=hook_vulkan event=install_hook_result result=ok pid=%lu qpcFreq=%llu.",
            static_cast<unsigned long>(GetCurrentProcessId()),
            static_cast<unsigned long long>(rt.qpcFreq));
        return true;
    }

    void LogInstallThreadEvent(const char* fmt, ...)
    {
        if (fmt == nullptr)
        {
            return;
        }

        char buffer[768]{};
        va_list args;
        va_start(args, fmt);
        (void)_vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, fmt, args);
        va_end(args);
        DebugLogInstall("%s", buffer);
    }

    int HandleInstallThreadException(unsigned long exceptionCode)
    {
        // WHY: The remote thread exit code alone loses the faulting exception details.
        DebugLogInstall(
            "stage=hook_vulkan event=install_thread_exception pid=%lu tid=%lu code=0x%08lX.",
            static_cast<unsigned long>(GetCurrentProcessId()),
            static_cast<unsigned long>(GetCurrentThreadId()),
            exceptionCode);
        return EXCEPTION_EXECUTE_HANDLER;
    }

    void UninstallPresentHook()
    {
        auto& rt = g_rt;
        std::lock_guard<std::mutex> lock(rt.mutex);
        if (!rt.installed.load())
        {
            return;
        }

        (void)MH_DisableHook(MH_ALL_HOOKS);
        (void)MH_Uninitialize();
        rt.hookSuccessIndicatorArmed = false;
        rt.hookSuccessIndicatorDone = false;
        rt.hookSuccessIndicatorStartQpc = 0;
        ResetRuntimeLocked(rt);
        rt.installed.store(false);
        DebugLog(
            "stage=hook_vulkan event=uninstall_hook result=ok pid=%lu.",
            static_cast<unsigned long>(GetCurrentProcessId()));
        CloseDiagFile();
    }
}

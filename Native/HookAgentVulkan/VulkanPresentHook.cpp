#include "VulkanPresentHook.h"

#include <algorithm>
#include <atomic>
#include <array>
#include <cstdint>
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

            std::uint64_t lastOverlayV2Seq = 0;
            std::uint64_t lastOverlayV2Qpc = 0;
            ipc::OverlayV2Header overlayV2Header{};
            std::vector<ipc::OverlayTextBlockV2> overlayV2Blocks;
            std::vector<std::uint8_t> overlayV2TextBlob;

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
        };

        VulkanRuntime g_rt;

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

            rt.lastOverlayV2Seq = 0;
            rt.lastOverlayV2Qpc = 0;
            rt.overlayV2Header = {};
            rt.overlayV2Blocks.clear();
            rt.overlayV2TextBlob.clear();

            rt.presentCount = 0;
            rt.lastPresentQpc = 0;
            rt.lastPresentKind = 1;
            rt.lastFormat = 0;
            rt.lastWidth = 0;
            rt.lastHeight = 0;

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

        std::uint32_t FindHostVisibleCoherentMemoryType(VkPhysicalDevice physicalDevice, std::uint32_t typeBits)
        {
            VkPhysicalDeviceMemoryProperties memProps{};
            vkGetPhysicalDeviceMemoryProperties(physicalDevice, &memProps);
            for (std::uint32_t i = 0; i < memProps.memoryTypeCount; ++i)
            {
                const bool typeMatches = (typeBits & (1u << i)) != 0;
                const auto flags = memProps.memoryTypes[i].propertyFlags;
                const bool hostVisible = (flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) != 0;
                const bool hostCoherent = (flags & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT) != 0;
                if (typeMatches && hostVisible && hostCoherent)
                {
                    return i;
                }
            }

            return std::numeric_limits<std::uint32_t>::max();
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
                return false;
            }

            std::uint32_t imageCount = 0;
            if (getImages(info.device, swapchain, &imageCount, nullptr) != VK_SUCCESS || imageCount == 0)
            {
                return false;
            }

            std::vector<VkImage> images(imageCount);
            if (getImages(info.device, swapchain, &imageCount, images.data()) != VK_SUCCESS || imageCount == 0)
            {
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
                return false;
            }

            bool rgbaNeedsSwap = false;
            if (!IsCaptureFormatSupported(swapInfo.format, rgbaNeedsSwap))
            {
                (void)rgbaNeedsSwap;
                return false;
            }

            const VkDevice device = queueInfo.device;
            if (device == VK_NULL_HANDLE || deviceInfo.physicalDevice == VK_NULL_HANDLE || !queueInfo.valid)
            {
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
            if (vkCreateCommandPool(device, &poolInfo, nullptr, &state.commandPool) != VK_SUCCESS)
            {
                DestroyQueueGpuState(state);
                return false;
            }

            VkCommandBufferAllocateInfo cmdAlloc{};
            cmdAlloc.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
            cmdAlloc.commandPool = state.commandPool;
            cmdAlloc.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
            cmdAlloc.commandBufferCount = 1;
            if (vkAllocateCommandBuffers(device, &cmdAlloc, &state.commandBuffer) != VK_SUCCESS)
            {
                DestroyQueueGpuState(state);
                return false;
            }

            VkFenceCreateInfo fenceInfo{};
            fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
            if (vkCreateFence(device, &fenceInfo, nullptr, &state.fence) != VK_SUCCESS)
            {
                DestroyQueueGpuState(state);
                return false;
            }

            VkBufferCreateInfo bufferInfo{};
            bufferInfo.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
            bufferInfo.size = requiredBytes;
            bufferInfo.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
            bufferInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
            if (vkCreateBuffer(device, &bufferInfo, nullptr, &state.stagingBuffer) != VK_SUCCESS)
            {
                DestroyQueueGpuState(state);
                return false;
            }

            VkMemoryRequirements memReq{};
            vkGetBufferMemoryRequirements(device, state.stagingBuffer, &memReq);
            const auto memoryType = FindHostVisibleCoherentMemoryType(deviceInfo.physicalDevice, memReq.memoryTypeBits);
            if (memoryType == std::numeric_limits<std::uint32_t>::max())
            {
                DestroyQueueGpuState(state);
                return false;
            }

            VkMemoryAllocateInfo allocInfo{};
            allocInfo.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
            allocInfo.allocationSize = memReq.size;
            allocInfo.memoryTypeIndex = memoryType;
            if (vkAllocateMemory(device, &allocInfo, nullptr, &state.stagingMemory) != VK_SUCCESS)
            {
                DestroyQueueGpuState(state);
                return false;
            }

            if (vkBindBufferMemory(device, state.stagingBuffer, state.stagingMemory, 0) != VK_SUCCESS)
            {
                DestroyQueueGpuState(state);
                return false;
            }

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

            ImGui::Render();
        }
        bool SubmitPresentWorkLocked(
            VulkanRuntime& rt,
            VkQueue queue,
            VkSwapchainKHR swapchain,
            std::uint32_t imageIndex)
        {
            const auto queueIt = rt.queues.find(queue);
            if (queueIt == rt.queues.end() || !queueIt->second.valid)
            {
                return false;
            }

            auto swapIt = rt.swapchains.find(swapchain);
            if (swapIt == rt.swapchains.end())
            {
                return false;
            }

            auto& swapInfo = swapIt->second;
            if (swapInfo.images.empty())
            {
                (void)EnsureSwapchainImagesLocked(rt, swapInfo, swapchain);
            }
            if (swapInfo.images.empty() || imageIndex >= swapInfo.images.size())
            {
                return false;
            }

            const auto deviceIt = rt.devices.find(queueIt->second.device);
            if (deviceIt == rt.devices.end())
            {
                return false;
            }

            const bool shouldCapture = ShouldCaptureNowLocked(rt, rt.lastPresentQpc);
            if (rt.overlayEnabled)
            {
                (void)RefreshOverlayV2Locked(rt);
            }

            const bool hasOverlayBlocks = rt.overlayEnabled && !rt.overlayV2Blocks.empty() && rt.lastOverlayV2Seq != 0;
            if (!shouldCapture && !hasOverlayBlocks)
            {
                return true;
            }

            if (!EnsureQueueGpuStateLocked(rt, queue, queueIt->second, deviceIt->second, swapInfo))
            {
                return false;
            }

            auto queueStateIt = rt.queueGpuStates.find(queue);
            if (queueStateIt == rt.queueGpuStates.end())
            {
                return false;
            }
            auto& gpu = queueStateIt->second;

            OverlaySwapchainState* ovl = nullptr;
            if (hasOverlayBlocks)
            {
                if (!EnsureOverlaySwapchainStateLocked(rt, swapchain, swapInfo))
                {
                    return false;
                }

                auto ovlIt = rt.overlaySwapchains.find(swapchain);
                if (ovlIt == rt.overlaySwapchains.end())
                {
                    return false;
                }
                ovl = &ovlIt->second;
                if (ovl->framebuffers.empty() || imageIndex >= ovl->framebuffers.size())
                {
                    return false;
                }

                if (!EnsureImGuiLocked(rt, queue, swapchain, queueIt->second, deviceIt->second, *ovl))
                {
                    return false;
                }
            }

            if (vkResetCommandPool(gpu.device, gpu.commandPool, 0) != VK_SUCCESS)
            {
                return false;
            }
            if (vkResetFences(gpu.device, 1, &gpu.fence) != VK_SUCCESS)
            {
                return false;
            }

            VkCommandBufferBeginInfo beginInfo{};
            beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
            beginInfo.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
            if (vkBeginCommandBuffer(gpu.commandBuffer, &beginInfo) != VK_SUCCESS)
            {
                return false;
            }

            const VkImage targetImage = swapInfo.images[imageIndex];
            if (targetImage == VK_NULL_HANDLE)
            {
                (void)vkEndCommandBuffer(gpu.commandBuffer);
                return false;
            }

            bool inTransferLayout = false;
            if (shouldCapture)
            {
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
            }

            if (hasOverlayBlocks && ovl != nullptr)
            {
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

            if (vkEndCommandBuffer(gpu.commandBuffer) != VK_SUCCESS)
            {
                return false;
            }

            VkSubmitInfo submitInfo{};
            submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
            submitInfo.commandBufferCount = 1;
            submitInfo.pCommandBuffers = &gpu.commandBuffer;
            if (vkQueueSubmit(queue, 1, &submitInfo, gpu.fence) != VK_SUCCESS)
            {
                return false;
            }

            if (vkWaitForFences(gpu.device, 1, &gpu.fence, VK_TRUE, 1'000'000'000ull) != VK_SUCCESS)
            {
                return false;
            }

            rt.lastFormat = static_cast<std::uint32_t>(gpu.format);
            rt.lastWidth = gpu.width;
            rt.lastHeight = gpu.height;

            if (!shouldCapture)
            {
                return true;
            }

            bool rgbaNeedsSwap = false;
            if (!IsCaptureFormatSupported(gpu.format, rgbaNeedsSwap))
            {
                return false;
            }

            void* mapped = nullptr;
            if (vkMapMemory(gpu.device, gpu.stagingMemory, 0, gpu.stagingBytes, 0, &mapped) != VK_SUCCESS || mapped == nullptr)
            {
                return false;
            }

            const auto* src = static_cast<const std::uint8_t*>(mapped);
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

            vkUnmapMemory(gpu.device, gpu.stagingMemory);

            const auto pid = GetCurrentProcessId();
            const auto ts = NowQpc();
            const std::uint32_t stride = gpu.width * 4u;
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
            if (!wrote)
            {
                return false;
            }

            rt.lastCaptureQpc = ts;
            rt.lastFrameWriteQpc = ts;
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
        if (rt.installed.load())
        {
            return true;
        }

        const auto initStatus = MH_Initialize();
        if (initStatus != MH_OK && initStatus != MH_ERROR_ALREADY_INITIALIZED)
        {
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
            return false;
        }

        bool hookedAny = false;
        hookedAny |= HookExport(
            vulkanModule,
            "vkGetDeviceProcAddr",
            reinterpret_cast<void*>(&Hook_vkGetDeviceProcAddr),
            reinterpret_cast<void**>(&rt.originalGetDeviceProcAddr));
        hookedAny |= HookExport(
            vulkanModule,
            "vkGetInstanceProcAddr",
            reinterpret_cast<void*>(&Hook_vkGetInstanceProcAddr),
            reinterpret_cast<void**>(&rt.originalGetInstanceProcAddr));
        hookedAny |= HookExport(
            vulkanModule,
            "vkCreateInstance",
            reinterpret_cast<void*>(&Hook_vkCreateInstance),
            reinterpret_cast<void**>(&rt.originalCreateInstance));
        hookedAny |= HookExport(
            vulkanModule,
            "vkDestroyInstance",
            reinterpret_cast<void*>(&Hook_vkDestroyInstance),
            reinterpret_cast<void**>(&rt.originalDestroyInstance));
        hookedAny |= HookExport(
            vulkanModule,
            "vkCreateDevice",
            reinterpret_cast<void*>(&Hook_vkCreateDevice),
            reinterpret_cast<void**>(&rt.originalCreateDevice));
        hookedAny |= HookExport(
            vulkanModule,
            "vkDestroyDevice",
            reinterpret_cast<void*>(&Hook_vkDestroyDevice),
            reinterpret_cast<void**>(&rt.originalDestroyDevice));
        hookedAny |= HookExport(
            vulkanModule,
            "vkGetDeviceQueue",
            reinterpret_cast<void*>(&Hook_vkGetDeviceQueue),
            reinterpret_cast<void**>(&rt.originalGetDeviceQueue));
        hookedAny |= HookExport(
            vulkanModule,
            "vkGetDeviceQueue2",
            reinterpret_cast<void*>(&Hook_vkGetDeviceQueue2),
            reinterpret_cast<void**>(&rt.originalGetDeviceQueue2));
        hookedAny |= HookExport(
            vulkanModule,
            "vkCreateSwapchainKHR",
            reinterpret_cast<void*>(&Hook_vkCreateSwapchainKHR),
            reinterpret_cast<void**>(&rt.originalCreateSwapchainKHR));
        hookedAny |= HookExport(
            vulkanModule,
            "vkDestroySwapchainKHR",
            reinterpret_cast<void*>(&Hook_vkDestroySwapchainKHR),
            reinterpret_cast<void**>(&rt.originalDestroySwapchainKHR));
        hookedAny |= HookExport(
            vulkanModule,
            "vkAcquireNextImageKHR",
            reinterpret_cast<void*>(&Hook_vkAcquireNextImageKHR),
            reinterpret_cast<void**>(&rt.originalAcquireNextImageKHR));
        hookedAny |= HookExport(
            vulkanModule,
            "vkAcquireNextImage2KHR",
            reinterpret_cast<void*>(&Hook_vkAcquireNextImage2KHR),
            reinterpret_cast<void**>(&rt.originalAcquireNextImage2KHR));
        hookedAny |= HookExport(
            vulkanModule,
            "vkQueuePresentKHR",
            reinterpret_cast<void*>(&Hook_vkQueuePresentKHR),
            reinterpret_cast<void**>(&rt.originalQueuePresentKHR));

        if (!hookedAny)
        {
            (void)MH_DisableHook(MH_ALL_HOOKS);
            (void)MH_Uninitialize();
            return false;
        }

        rt.qpcFreq = QueryQpcFreq();
        rt.captureIntervalQpc = (rt.qpcFreq > 0) ? (rt.qpcFreq / kDefaultCaptureFps) : 0;
        rt.configuredFpsLimit = kDefaultCaptureFps;
        rt.overlayEnabled = false;
        rt.installed.store(true);
        return true;
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
        ResetRuntimeLocked(rt);
        rt.installed.store(false);
    }
}

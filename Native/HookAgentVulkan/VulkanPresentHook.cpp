#include "VulkanPresentHook.h"

#include <algorithm>
#include <atomic>
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

#include "../HookCommon/SharedFrameWriter.h"
#include "../HookCommon/SharedHookConfig.h"
#include "../HookCommon/SharedHookStatus.h"

namespace ht::hook::vulkan
{
    namespace
    {
        constexpr std::uint32_t kDefaultCaptureFps = 15u;
        constexpr std::uint32_t kMinCaptureFps = 1u;
        constexpr std::uint32_t kMaxCaptureFps = 240u;

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

        struct QueueCaptureState
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

        struct VulkanRuntime
        {
            std::mutex mutex;
            std::atomic_bool installed{false};

            ipc::SharedFrameWriter frameWriter;
            ipc::SharedHookConfigReader configReader;
            ipc::SharedHookStatusWriter statusWriter;

            PFN_vkGetDeviceProcAddr originalGetDeviceProcAddr = nullptr;
            PFN_vkGetInstanceProcAddr originalGetInstanceProcAddr = nullptr;
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

            std::unordered_map<VkDevice, DeviceInfo> devices;
            std::unordered_map<VkQueue, QueueInfo> queues;
            std::unordered_map<VkSwapchainKHR, SwapchainInfo> swapchains;
            std::unordered_map<VkQueue, QueueCaptureState> captures;

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

        void DestroyCaptureState(QueueCaptureState& st)
        {
            if (st.device == VK_NULL_HANDLE)
            {
                st = QueueCaptureState{};
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

            st = QueueCaptureState{};
        }

        void RemoveDeviceStateLocked(VulkanRuntime& rt, VkDevice device)
        {
            for (auto it = rt.captures.begin(); it != rt.captures.end();)
            {
                if (it->second.device == device)
                {
                    DestroyCaptureState(it->second);
                    it = rt.captures.erase(it);
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
            for (auto& entry : rt.captures)
            {
                DestroyCaptureState(entry.second);
            }

            rt.captures.clear();
            rt.queues.clear();
            rt.swapchains.clear();
            rt.devices.clear();
            rt.frameWriter.Reset();
            rt.configReader.Reset();
            rt.statusWriter.Reset();
            rt.frameId = 0;
            rt.lastCaptureQpc = 0;
            rt.lastConfigQpc = 0;
            rt.lastFrameWriteQpc = 0;
            rt.configuredFpsLimit = kDefaultCaptureFps;
            rt.captureIntervalQpc = (rt.qpcFreq > 0) ? (rt.qpcFreq / kDefaultCaptureFps) : 0;
            rt.overlayEnabled = false;
            rt.presentCount = 0;
            rt.lastPresentQpc = 0;
            rt.lastPresentKind = 1;
            rt.lastFormat = 0;
            rt.lastWidth = 0;
            rt.lastHeight = 0;

            rt.originalGetDeviceProcAddr = nullptr;
            rt.originalGetInstanceProcAddr = nullptr;
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

        bool IsSupportedCaptureFormat(VkFormat format, bool& rgbaNeedsSwap)
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
                    if (rt.originalGetSwapchainImagesKHR == nullptr)
                    {
                        rt.originalGetSwapchainImagesKHR = getImages;
                    }
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
        bool EnsureCaptureResourcesLocked(
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
            if (!IsSupportedCaptureFormat(swapInfo.format, rgbaNeedsSwap))
            {
                (void)rgbaNeedsSwap;
                return false;
            }

            const VkDevice device = queueInfo.device;
            if (device == VK_NULL_HANDLE || deviceInfo.physicalDevice == VK_NULL_HANDLE || !queueInfo.valid)
            {
                return false;
            }

            auto& cap = rt.captures[queue];
            const VkDeviceSize requiredBytes =
                static_cast<VkDeviceSize>(swapInfo.extent.width) *
                static_cast<VkDeviceSize>(swapInfo.extent.height) * 4ull;

            const bool mustRecreate =
                cap.device != device ||
                cap.queueFamily != queueInfo.familyIndex ||
                cap.width != swapInfo.extent.width ||
                cap.height != swapInfo.extent.height ||
                cap.format != swapInfo.format ||
                cap.stagingBytes != requiredBytes ||
                cap.commandPool == VK_NULL_HANDLE ||
                cap.commandBuffer == VK_NULL_HANDLE ||
                cap.stagingBuffer == VK_NULL_HANDLE ||
                cap.stagingMemory == VK_NULL_HANDLE ||
                cap.fence == VK_NULL_HANDLE;

            if (!mustRecreate)
            {
                return true;
            }

            DestroyCaptureState(cap);

            cap.device = device;
            cap.queueFamily = queueInfo.familyIndex;
            cap.width = swapInfo.extent.width;
            cap.height = swapInfo.extent.height;
            cap.format = swapInfo.format;
            cap.stagingBytes = requiredBytes;

            VkCommandPoolCreateInfo poolInfo{};
            poolInfo.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
            poolInfo.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
            poolInfo.queueFamilyIndex = queueInfo.familyIndex;
            if (vkCreateCommandPool(device, &poolInfo, nullptr, &cap.commandPool) != VK_SUCCESS)
            {
                DestroyCaptureState(cap);
                return false;
            }

            VkCommandBufferAllocateInfo cmdAlloc{};
            cmdAlloc.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
            cmdAlloc.commandPool = cap.commandPool;
            cmdAlloc.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
            cmdAlloc.commandBufferCount = 1;
            if (vkAllocateCommandBuffers(device, &cmdAlloc, &cap.commandBuffer) != VK_SUCCESS)
            {
                DestroyCaptureState(cap);
                return false;
            }

            VkFenceCreateInfo fenceInfo{};
            fenceInfo.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
            if (vkCreateFence(device, &fenceInfo, nullptr, &cap.fence) != VK_SUCCESS)
            {
                DestroyCaptureState(cap);
                return false;
            }

            VkBufferCreateInfo bufferInfo{};
            bufferInfo.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
            bufferInfo.size = requiredBytes;
            bufferInfo.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
            bufferInfo.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
            if (vkCreateBuffer(device, &bufferInfo, nullptr, &cap.stagingBuffer) != VK_SUCCESS)
            {
                DestroyCaptureState(cap);
                return false;
            }

            VkMemoryRequirements memReq{};
            vkGetBufferMemoryRequirements(device, cap.stagingBuffer, &memReq);
            const auto memoryType = FindHostVisibleCoherentMemoryType(deviceInfo.physicalDevice, memReq.memoryTypeBits);
            if (memoryType == std::numeric_limits<std::uint32_t>::max())
            {
                DestroyCaptureState(cap);
                return false;
            }

            VkMemoryAllocateInfo allocInfo{};
            allocInfo.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
            allocInfo.allocationSize = memReq.size;
            allocInfo.memoryTypeIndex = memoryType;
            if (vkAllocateMemory(device, &allocInfo, nullptr, &cap.stagingMemory) != VK_SUCCESS)
            {
                DestroyCaptureState(cap);
                return false;
            }

            if (vkBindBufferMemory(device, cap.stagingBuffer, cap.stagingMemory, 0) != VK_SUCCESS)
            {
                DestroyCaptureState(cap);
                return false;
            }

            cap.scratch.clear();
            cap.scratch.resize(static_cast<std::size_t>(requiredBytes));
            return true;
        }

        bool CaptureSwapchainImageLocked(
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

            const auto swapIt = rt.swapchains.find(swapchain);
            if (swapIt == rt.swapchains.end())
            {
                return false;
            }

            auto& swapInfo = swapIt->second;
            if (swapInfo.device == VK_NULL_HANDLE)
            {
                return false;
            }

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

            bool rgbaNeedsSwap = false;
            if (!IsSupportedCaptureFormat(swapInfo.format, rgbaNeedsSwap))
            {
                return false;
            }

            if (!EnsureCaptureResourcesLocked(rt, queue, queueIt->second, deviceIt->second, swapInfo))
            {
                return false;
            }

            auto capIt = rt.captures.find(queue);
            if (capIt == rt.captures.end())
            {
                return false;
            }
            auto& cap = capIt->second;

            const VkImage image = swapInfo.images[imageIndex];
            if (image == VK_NULL_HANDLE)
            {
                return false;
            }
            if (vkResetCommandPool(cap.device, cap.commandPool, 0) != VK_SUCCESS)
            {
                return false;
            }

            VkCommandBufferBeginInfo beginInfo{};
            beginInfo.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
            beginInfo.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
            if (vkBeginCommandBuffer(cap.commandBuffer, &beginInfo) != VK_SUCCESS)
            {
                return false;
            }

            VkImageMemoryBarrier toTransfer{};
            toTransfer.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
            toTransfer.srcAccessMask = VK_ACCESS_MEMORY_READ_BIT;
            toTransfer.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
            toTransfer.oldLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
            toTransfer.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
            toTransfer.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
            toTransfer.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
            toTransfer.image = image;
            toTransfer.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
            toTransfer.subresourceRange.baseMipLevel = 0;
            toTransfer.subresourceRange.levelCount = 1;
            toTransfer.subresourceRange.baseArrayLayer = 0;
            toTransfer.subresourceRange.layerCount = 1;
            vkCmdPipelineBarrier(
                cap.commandBuffer,
                VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                VK_PIPELINE_STAGE_TRANSFER_BIT,
                0,
                0,
                nullptr,
                0,
                nullptr,
                1,
                &toTransfer);

            VkBufferImageCopy region{};
            region.bufferOffset = 0;
            region.bufferRowLength = 0;
            region.bufferImageHeight = 0;
            region.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
            region.imageSubresource.mipLevel = 0;
            region.imageSubresource.baseArrayLayer = 0;
            region.imageSubresource.layerCount = 1;
            region.imageOffset = {0, 0, 0};
            region.imageExtent = {cap.width, cap.height, 1};
            vkCmdCopyImageToBuffer(
                cap.commandBuffer,
                image,
                VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                cap.stagingBuffer,
                1,
                &region);

            VkImageMemoryBarrier toPresent{};
            toPresent.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
            toPresent.srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
            toPresent.dstAccessMask = VK_ACCESS_MEMORY_READ_BIT;
            toPresent.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
            toPresent.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
            toPresent.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
            toPresent.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
            toPresent.image = image;
            toPresent.subresourceRange = toTransfer.subresourceRange;
            vkCmdPipelineBarrier(
                cap.commandBuffer,
                VK_PIPELINE_STAGE_TRANSFER_BIT,
                VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                0,
                0,
                nullptr,
                0,
                nullptr,
                1,
                &toPresent);

            if (vkEndCommandBuffer(cap.commandBuffer) != VK_SUCCESS)
            {
                return false;
            }

            if (vkResetFences(cap.device, 1, &cap.fence) != VK_SUCCESS)
            {
                return false;
            }

            VkSubmitInfo submitInfo{};
            submitInfo.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
            submitInfo.commandBufferCount = 1;
            submitInfo.pCommandBuffers = &cap.commandBuffer;
            if (vkQueueSubmit(queue, 1, &submitInfo, cap.fence) != VK_SUCCESS)
            {
                return false;
            }

            if (vkWaitForFences(cap.device, 1, &cap.fence, VK_TRUE, 1'000'000'000ull) != VK_SUCCESS)
            {
                return false;
            }

            void* mapped = nullptr;
            if (vkMapMemory(cap.device, cap.stagingMemory, 0, cap.stagingBytes, 0, &mapped) != VK_SUCCESS || mapped == nullptr)
            {
                return false;
            }

            const auto* srcBytes = static_cast<const std::uint8_t*>(mapped);
            const std::size_t payloadBytes = static_cast<std::size_t>(cap.stagingBytes);
            if (cap.scratch.size() < payloadBytes)
            {
                cap.scratch.resize(payloadBytes);
            }

            if (!rgbaNeedsSwap)
            {
                std::memcpy(cap.scratch.data(), srcBytes, payloadBytes);
            }
            else
            {
                for (std::size_t i = 0; i + 3 < payloadBytes; i += 4)
                {
                    cap.scratch[i + 0] = srcBytes[i + 2];
                    cap.scratch[i + 1] = srcBytes[i + 1];
                    cap.scratch[i + 2] = srcBytes[i + 0];
                    cap.scratch[i + 3] = srcBytes[i + 3];
                }
            }

            vkUnmapMemory(cap.device, cap.stagingMemory);

            const auto pid = GetCurrentProcessId();
            const auto timestamp = NowQpc();
            const std::uint32_t stride = cap.width * 4u;
            const bool wrote = rt.frameWriter.WriteFrame(
                pid,
                ipc::GraphicsApi::Vulkan,
                ++rt.frameId,
                cap.width,
                cap.height,
                stride,
                timestamp,
                cap.scratch.data(),
                payloadBytes);
            if (!wrote)
            {
                return false;
            }

            rt.lastCaptureQpc = timestamp;
            rt.lastFrameWriteQpc = timestamp;
            rt.lastFormat = static_cast<std::uint32_t>(cap.format);
            rt.lastWidth = cap.width;
            rt.lastHeight = cap.height;
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
            status.lastCmdQpc = 0;
            status.lastCmdCount = 0;
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

            if (functionName == nullptr)
            {
                return resolved;
            }

            if (std::strcmp(functionName, "vkCreateDevice") == 0)
            {
                StoreOriginalIfUnset(rt.originalCreateDevice, resolved);
                return reinterpret_cast<PFN_vkVoidFunction>(&Hook_vkCreateDevice);
            }

            return resolved;
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
            {
                std::lock_guard<std::mutex> lock(rt.mutex);
                rt.presentCount++;
                rt.lastPresentQpc = NowQpc();
                rt.lastPresentKind = 1;

                (void)EnsureConfigRefreshedLocked(rt);
                if (ShouldCaptureNowLocked(rt, rt.lastPresentQpc) &&
                    presentInfo != nullptr &&
                    presentInfo->swapchainCount > 0 &&
                    presentInfo->pSwapchains != nullptr)
                {
                    const std::uint32_t imageIndex =
                        (presentInfo->pImageIndices != nullptr)
                            ? presentInfo->pImageIndices[0]
                            : 0u;
                    const VkSwapchainKHR swapchain = presentInfo->pSwapchains[0];
                    (void)CaptureSwapchainImageLocked(rt, queue, swapchain, imageIndex);
                }

                PublishStatusLocked(rt);
            }

            const auto original = rt.originalQueuePresentKHR;
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

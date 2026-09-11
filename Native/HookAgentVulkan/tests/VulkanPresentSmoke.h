// Included in the integration test translation unit after the production hook implementation.
CaptureSlot overlayVerificationSlot;
unsigned overlayVerifiedFrames = 0;

VKAPI_ATTR VkResult VKAPI_CALL VerifyOverlayBeforePresent(VkQueue queue, const VkPresentInfoKHR* present)
{
    auto& rt = g_rt;
    const auto& swap = rt.swapchains.at(present->pSwapchains[0]);
    auto& slot = overlayVerificationSlot;
    if (slot.commandPool == VK_NULL_HANDLE)
        Require(CreateCaptureSlotResources(swap.device, rt.devices.at(swap.device).physicalDevice,
            rt.queues.at(queue).familyIndex, static_cast<VkDeviceSize>(swap.extent.width) * swap.extent.height * 4,
            swap.extent.width, swap.extent.height, swap.format, false, slot), "create overlay pixel verifier");
    Require(vkResetCommandPool(swap.device, slot.commandPool, 0) == VK_SUCCESS, "reset verification commands");
    Require(vkResetFences(swap.device, 1, &slot.fence) == VK_SUCCESS, "reset verification fence");
    VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    Require(vkBeginCommandBuffer(slot.commandBuffer, &begin) == VK_SUCCESS, "begin verification commands");
    const auto image = swap.images[present->pImageIndices[0]];
    CmdTransitionImageLayout(slot.commandBuffer, image, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        VK_ACCESS_MEMORY_READ_BIT, VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkBufferImageCopy region{};
    region.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    region.imageExtent = {swap.extent.width, swap.extent.height, 1};
    vkCmdCopyImageToBuffer(slot.commandBuffer, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, slot.stagingBuffer, 1, &region);
    CmdMakeReadbackVisible(slot.commandBuffer);
    CmdTransitionImageLayout(slot.commandBuffer, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
        VK_ACCESS_TRANSFER_READ_BIT, VK_ACCESS_MEMORY_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);
    Require(vkEndCommandBuffer(slot.commandBuffer) == VK_SUCCESS, "end verification commands");
    // WHY: Read exactly the image about to be presented, after the hook's semaphore signal.
    Require(SubmitSynchronizedPresentWork(queue, slot.commandBuffer, slot.fence, *present) == VK_SUCCESS, "submit pixel verification");
    Require(vkWaitForFences(swap.device, 1, &slot.fence, VK_TRUE, UINT64_MAX) == VK_SUCCESS, "wait for verification readback");
    const auto* pixel = static_cast<const std::uint8_t*>(slot.stagingMapped) + (12 * swap.extent.width + 12) * 4;
    Require(pixel[0] == 0 && pixel[1] == 255 && pixel[2] == 0, "every presented frame retains the green overlay");
    ++overlayVerifiedFrames;
    return vkQueuePresentKHR(queue, present);
}

void RealPresentSmoke(VulkanRuntime& rt, VkInstance instance, VkPhysicalDevice physical,
    VkDevice device, VkQueue queue, std::uint32_t family)
{
    struct Surface
    {
        HWND window = nullptr;
        VkSurfaceKHR surface = VK_NULL_HANDLE;
        VkSwapchainKHR swapchain = VK_NULL_HANDLE;
        VkSemaphore acquired = VK_NULL_HANDLE;
        std::vector<VkSemaphore> rendered;
        std::vector<VkImage> images;
    };
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = L"HTVulkanPresentRegression";
    Require(RegisterClassW(&windowClass) != 0, "register hidden test window");
    VkSemaphoreCreateInfo semInfo{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
    const auto capturedDeviceInfo = rt.devices.at(device);

    for (unsigned generation = 0; generation < 2; ++generation)
    {
        rt.instance = instance;
        rt.originalGetInstanceProcAddr = vkGetInstanceProcAddr;
        rt.originalGetSwapchainImagesKHR = vkGetSwapchainImagesKHR;
        rt.apiVersion = VK_API_VERSION_1_1;
        rt.devices[device] = capturedDeviceInfo;
        const auto getQueue = reinterpret_cast<PFN_vkGetDeviceQueue>(Hook_vkGetInstanceProcAddr(instance, "vkGetDeviceQueue"));
        VkQueue observedQueue = VK_NULL_HANDLE;
        getQueue(device, family, 0, &observedQueue);
        Require(observedQueue == queue, "observe real present queue through hook");
        const auto createSwapchain = reinterpret_cast<PFN_vkCreateSwapchainKHR>(Hook_vkGetInstanceProcAddr(instance, "vkCreateSwapchainKHR"));
        const auto presentQueue = reinterpret_cast<PFN_vkQueuePresentKHR>(Hook_vkGetInstanceProcAddr(instance, "vkQueuePresentKHR"));
        Require(createSwapchain == Hook_vkCreateSwapchainKHR && presentQueue == Hook_vkQueuePresentKHR,
            "instance lookup preserves swapchain and present hooks without direct export patching");
        std::uint32_t physicalCount = 0;
        Require(vkEnumeratePhysicalDevices(instance, &physicalCount, nullptr) == VK_SUCCESS, "count available physical devices");
        const bool exerciseBackfill = generation == 1 && physicalCount == 1;
        if (exerciseBackfill) rt.devices.erase(device);
        rt.captureIntervalQpc = rt.qpcFreq;
        rt.overlayEnabled = true;
        rt.hookSuccessIndicatorArmed = true;
        Require(EnsurePublishWorkerLocked(rt), "start WSI publisher");
        std::array<Surface, 2> surfaces{};
        for (auto& target : surfaces)
        {
            target.window = CreateWindowW(windowClass.lpszClassName, L"Vulkan regression", WS_POPUP,
                0, 0, 160 + generation * 32, 96, nullptr, nullptr, windowClass.hInstance, nullptr);
            Require(target.window != nullptr, "create hidden window");
            VkWin32SurfaceCreateInfoKHR surfaceInfo{VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR};
            surfaceInfo.hinstance = windowClass.hInstance;
            surfaceInfo.hwnd = target.window;
            Require(vkCreateWin32SurfaceKHR(instance, &surfaceInfo, nullptr, &target.surface) == VK_SUCCESS, "create Win32 surface");
            VkBool32 supportsPresent = VK_FALSE;
            Require(vkGetPhysicalDeviceSurfaceSupportKHR(physical, family, target.surface, &supportsPresent) == VK_SUCCESS && supportsPresent,
                "graphics queue supports presentation");
            VkSurfaceCapabilitiesKHR caps{};
            Require(vkGetPhysicalDeviceSurfaceCapabilitiesKHR(physical, target.surface, &caps) == VK_SUCCESS, "surface capabilities");
            std::uint32_t count = 0;
            Require(vkGetPhysicalDeviceSurfaceFormatsKHR(physical, target.surface, &count, nullptr) == VK_SUCCESS, "surface formats count");
            std::vector<VkSurfaceFormatKHR> formats(count);
            Require(vkGetPhysicalDeviceSurfaceFormatsKHR(physical, target.surface, &count, formats.data()) == VK_SUCCESS, "surface formats");
            const auto format = std::find_if(formats.begin(), formats.end(), [](const VkSurfaceFormatKHR& item)
                { return item.format == VK_FORMAT_B8G8R8A8_UNORM; });
            Require(format != formats.end(), "BGRA UNORM surface format");
            VkSwapchainCreateInfoKHR create{VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR};
            create.surface = target.surface;
            create.minImageCount = std::max(2u, caps.minImageCount);
            create.imageFormat = format->format;
            create.imageColorSpace = format->colorSpace;
            create.imageExtent = caps.currentExtent;
            create.imageArrayLayers = 1;
            create.imageUsage = VK_IMAGE_USAGE_TRANSFER_DST_BIT;
            create.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
            create.preTransform = caps.currentTransform;
            create.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
            create.presentMode = VK_PRESENT_MODE_FIFO_KHR;
            create.clipped = VK_TRUE;
            Require((caps.supportedUsageFlags & (VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT)) ==
                (VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT), "surface supports capture/overlay usage");
            Require(createSwapchain(device, &create, nullptr, &target.swapchain) == VK_SUCCESS, "create test swapchain through hook");
            Require(rt.swapchains[target.swapchain].hookUsageSupported && create.imageUsage == VK_IMAGE_USAGE_TRANSFER_DST_BIT,
                "hook augments usage without mutating caller's create info");
            if (exerciseBackfill)
                Require(!rt.devices[device].createInfoKnown && rt.devices[device].physicalDeviceCount == 1,
                    "swapchain creation resolves a unique adapter without fabricating device creation metadata");
            target.images = rt.swapchains[target.swapchain].images;
            Require(!target.images.empty(), "query swapchain images");
            target.rendered.resize(target.images.size());
            for (auto& semaphore : target.rendered)
                Require(vkCreateSemaphore(device, &semInfo, nullptr, &semaphore) == VK_SUCCESS, "create per-image game semaphore");
            Require(vkCreateSemaphore(device, &semInfo, nullptr, &target.acquired) == VK_SUCCESS, "create acquire semaphore");
        }
        rt.originalQueuePresentKHR = VerifyOverlayBeforePresent;
        rt.overlayV2Header.canvasW = rt.swapchains.at(surfaces[0].swapchain).extent.width;
        rt.overlayV2Header.canvasH = rt.swapchains.at(surfaces[0].swapchain).extent.height;
        ht::hook::ipc::OverlayTextBlockV2 block{};
        block.x = block.y = 8;
        block.w = 32;
        block.h = 24;
        block.bgArgb = 0xff00ff00;
        rt.overlayV2Blocks = {block};
        rt.lastOverlayV2Seq = 1;
        VkCommandPool gamePool = VK_NULL_HANDLE;
        VkCommandBuffer gameCommands = VK_NULL_HANDLE;
        VkFence gameFence = VK_NULL_HANDLE;
        Require(CreateQueueSubmitResources(device, family, gamePool, gameCommands, gameFence), "game command resources");
        std::array<VkPipelineStageFlags, 2> stages{VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT};
        for (unsigned frame = 0; frame < 36; ++frame)
        {
            std::array<std::uint32_t, 2> imageIndices{};
            std::array<VkSwapchainKHR, 2> swapchains{};
            std::array<VkSemaphore, 2> acquired{};
            std::array<VkSemaphore, 2> rendered{};
            Require(vkResetCommandPool(device, gamePool, 0) == VK_SUCCESS, "reset game commands");
            Require(vkResetFences(device, 1, &gameFence) == VK_SUCCESS, "reset game fence");
            VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
            Require(vkBeginCommandBuffer(gameCommands, &begin) == VK_SUCCESS, "begin game commands");
            for (std::size_t i = 0; i < surfaces.size(); ++i)
            {
                auto& target = surfaces[i];
                const auto result = vkAcquireNextImageKHR(device, target.swapchain, 5'000'000'000ull,
                    target.acquired, VK_NULL_HANDLE, &imageIndices[i]);
                Require(result == VK_SUCCESS || result == VK_SUBOPTIMAL_KHR, "acquire test image");
                acquired[i] = target.acquired;
                rendered[i] = target.rendered[imageIndices[i]];
                swapchains[i] = target.swapchain;
                const auto image = target.images[imageIndices[i]];
                CmdTransitionImageLayout(gameCommands, image, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                    0, VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
                // NOTE: Endpoints avoid driver-dependent UNORM rounding in the byte comparison.
                VkClearColorValue color{{1.0f, 0.0f, 0.0f, 1.0f}};
                VkImageSubresourceRange range{VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
                vkCmdClearColorImage(gameCommands, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &color, 1, &range);
                CmdTransitionImageLayout(gameCommands, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
                    VK_ACCESS_TRANSFER_WRITE_BIT, 0, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);
            }
            Require(vkEndCommandBuffer(gameCommands) == VK_SUCCESS, "end game commands");
            VkSubmitInfo gameSubmit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
            gameSubmit.commandBufferCount = 1;
            gameSubmit.pCommandBuffers = &gameCommands;
            gameSubmit.waitSemaphoreCount = 2;
            gameSubmit.pWaitSemaphores = acquired.data();
            gameSubmit.pWaitDstStageMask = stages.data();
            gameSubmit.signalSemaphoreCount = 2;
            gameSubmit.pSignalSemaphores = rendered.data();
            Require(vkQueueSubmit(queue, 1, &gameSubmit, gameFence) == VK_SUCCESS, "submit game render");
            if (frame >= 18)
            {
                // WHY: Change the first presented target without detaching to exercise ImGui rebinding.
                std::swap(swapchains[0], swapchains[1]);
                std::swap(imageIndices[0], imageIndices[1]);
            }
            std::array<VkResult, 2> results{};
            VkPresentInfoKHR present{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
            present.waitSemaphoreCount = 2;
            present.pWaitSemaphores = rendered.data();
            present.swapchainCount = 2;
            present.pSwapchains = swapchains.data();
            present.pImageIndices = imageIndices.data();
            present.pResults = results.data();
            const std::array<std::uint32_t, 2> masks{1, 1};
            VkDeviceGroupPresentInfoKHR groupPresent{VK_STRUCTURE_TYPE_DEVICE_GROUP_PRESENT_INFO_KHR};
            groupPresent.swapchainCount = 2;
            groupPresent.pDeviceMasks = masks.data();
            groupPresent.mode = VK_DEVICE_GROUP_PRESENT_MODE_LOCAL_BIT_KHR;
            if (frame & 1) present.pNext = &groupPresent;
            if (frame % 7 == 0 && rt.queueGpuStates.count(queue)) rt.queueGpuStates[queue].lastCaptureIssueQpc = 0;
            const bool captureSlotsForcedBusy = frame == 10;
            if (captureSlotsForcedBusy)
            {
                // WHY: Model a slow OCR publisher holding every capture slot, without holding any overlay resources.
                auto& gpu = rt.queueGpuStates.at(queue);
                Require(WaitForQueueGpuWorkLocked(rt, gpu) && DrainPublishQueueLocked(rt), "retire real owners before simulating full capture ring");
                for (auto& slot : gpu.captureSlots) slot.state = CaptureSlotState::Publishing;
                gpu.lastCaptureIssueQpc = 0;
            }
            rt.lastPresentQpc = NowQpc();
            rt.hookSuccessIndicatorDone = false;
            const auto waitsBefore = waitCalls.load();
            const bool rebinding = rt.imguiInitialized && rt.imguiBoundSwapchain != swapchains[0];
            const auto submitsBefore = rt.overlaySubmitCount;
            const auto reuseWaitsBefore = rt.overlayWaitCount;
            const auto captureBusyBefore = rt.captureBusyCount;
            // WHY: Make the GPU-busy branch deterministic even on a fast test GPU. The real
            // fence wait still executes, and the pixel verifier detects a skipped draw.
            if (!rebinding && !rt.overlayFrames.empty())
            {
                const auto& next = rt.overlayFrames[(rt.overlayFrameIndex + 1) % rt.overlayFrames.size()];
                if (next.pending) forceOverlayBusyFence = next.fence;
            }
            const bool forcedBusy = forceOverlayBusyFence != VK_NULL_HANDLE;
            const auto result = presentQueue(queue, &present);
            forceOverlayBusyFence = VK_NULL_HANDLE;
            if (captureSlotsForcedBusy)
            {
                Require(rt.captureBusyCount == captureBusyBefore + 1, "capture ring saturation was exercised");
                for (auto& slot : rt.queueGpuStates.at(queue).captureSlots) slot.state = CaptureSlotState::Free;
            }
            Require(rt.overlaySubmitCount == submitsBefore + 1, "each frame draws even when capture is throttled or overlay was busy");
            Require(rebinding || (forcedBusy ? rt.overlayWaitCount == reuseWaitsBefore + 1 : waitCalls == waitsBefore),
                "only occupied overlay slots require a reuse wait");
            Require(result == VK_SUCCESS || result == VK_SUBOPTIMAL_KHR, "present after re-signaling game semaphores");
            for (auto perSwapchain : results) Require(perSwapchain == VK_SUCCESS || perSwapchain == VK_SUBOPTIMAL_KHR, "all batch pResults preserved");
            // NOTE: The test game uses this CPU wait to recycle its own command buffer, not the hook's.
            Require(vkWaitForFences(device, 1, &gameFence, VK_TRUE, UINT64_MAX) == VK_SUCCESS, "game command completion");
        }
        Require(vkDeviceWaitIdle(device) == VK_SUCCESS, "test game finishes its submissions");
        Require(PollCompletedCapturesLocked(rt, queue, rt.queueGpuStates[queue]), "final capture poll");
        Require(DrainPublishQueueLocked(rt) && rt.capturePublishCount > 0, "WSI frames were actually published");
        Require(rt.imguiInitialized && rt.imguiBoundSwapchain == surfaces[1].swapchain, "overlay was initialized and rebound");
        RequirePublishedPixel(rt, 0, 0, 255, rt.queueGpuStates[queue].stagingBytes);
        Require(rt.overlayWaitCount > 0, "exercised busy ring reuse without missing overlay pixels");
        DestroyCaptureSlot(device, overlayVerificationSlot);
        // WHY: A rejected submit must report failure without inventing pending GPU ownership.
        // This injection never touches the actual semaphores or the already-presented image.
        VkPresentInfoKHR failedPresent{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
        failedPresent.waitSemaphoreCount = 1;
        failedPresent.pWaitSemaphores = surfaces[1].rendered.data();
        // WHY: A failed overlay submit must not mark an unsignaled fence as pending forever.
        rt.queueGpuStates[queue].lastCaptureIssueQpc = NowQpc();
        rt.lastPresentQpc = NowQpc();
        const auto overlayIssuedBefore = rt.overlaySubmitCount;
        forcedSubmitResult = VK_ERROR_OUT_OF_HOST_MEMORY;
        VkResult overlayFailure = VK_SUCCESS;
        Require(!SubmitPresentWorkLocked(rt, queue, surfaces[1].swapchain, 0, failedPresent, overlayFailure) &&
            overlayFailure == forcedSubmitResult && rt.overlaySubmitCount == overlayIssuedBefore &&
            !rt.overlayFrames[rt.overlayFrameIndex].pending, "failed overlay submit preserves accurate fence ownership");
        // The injected failure never reached the GPU, whose real work was retired above.
        rt.queueGpuStates[queue].failed = false;
        rt.queueGpuStates[queue].lastCaptureIssueQpc = 0;
        rt.lastPresentQpc = NowQpc();
        forcedSubmitResult = VK_ERROR_OUT_OF_HOST_MEMORY;
        VkResult failedResult = VK_SUCCESS;
        const auto issuedBefore = rt.captureIssueCount;
        Require(!SubmitPresentWorkLocked(rt, queue, surfaces[1].swapchain, 0, failedPresent, failedResult) &&
            failedResult == forcedSubmitResult && rt.queueGpuStates[queue].failed && rt.captureIssueCount == issuedBefore,
            "submit error is propagated and stops queue reuse without counting a capture");
        forcedSubmitResult = VK_SUCCESS;
        Require(ResetRuntimeLocked(rt), "retire hook before resize/detach");
        Require(rt.overlayFrames.empty(), "resize/detach releases all overlay frame resources");
        vkDestroyFence(device, gameFence, nullptr);
        vkDestroyCommandPool(device, gamePool, nullptr);
        for (auto& target : surfaces)
        {
            vkDestroySemaphore(device, target.acquired, nullptr);
            for (auto semaphore : target.rendered) vkDestroySemaphore(device, semaphore, nullptr);
            vkDestroySwapchainKHR(device, target.swapchain, nullptr);
            vkDestroySurfaceKHR(instance, target.surface, nullptr);
            DestroyWindow(target.window);
        }
        Require(validationErrors == 0, "WSI synchronization and resource lifetime validation");
    }
    UnregisterClassW(windowClass.lpszClassName, windowClass.hInstance);
    Require(overlayVerifiedFrames == 72, "verified overlay pixels on every test frame");
    std::puts("PASS: 72 multi-swapchain presents with overlay pixels, busy ring reuse, resize and detach");
}

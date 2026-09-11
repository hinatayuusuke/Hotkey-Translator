// Included after the production implementation; no hooks are installed into this test process.
namespace dispatch_tests
{
    const auto instance = (VkInstance)20;
    const auto physical = (VkPhysicalDevice)21;
    const auto swapchain = (VkSwapchainKHR)22;
    std::uint32_t adapterCount = 1;

    VKAPI_ATTR VkResult VKAPI_CALL Enumerate(VkInstance, std::uint32_t* count, VkPhysicalDevice* devices)
    {
        *count = adapterCount;
        if (devices && adapterCount == 1) *devices = physical;
        return VK_SUCCESS;
    }

    VKAPI_ATTR VkResult VKAPI_CALL CreateDevice(VkPhysicalDevice, const VkDeviceCreateInfo*,
        const VkAllocationCallbacks*, VkDevice* device)
    {
        *device = testDevice;
        return VK_SUCCESS;
    }

    VKAPI_ATTR void VKAPI_CALL GetQueue(VkDevice, std::uint32_t, std::uint32_t, VkQueue* queue)
    {
        *queue = testQueue;
    }

    VKAPI_ATTR VkResult VKAPI_CALL GetImages(VkDevice, VkSwapchainKHR, std::uint32_t* count, VkImage* images)
    {
        *count = 1;
        if (images) *images = (VkImage)23;
        return VK_SUCCESS;
    }

    // Used only to compare lookup results, never called through another function's signature.
    VKAPI_ATTR void VKAPI_CALL UnusedCommand() {}

    VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL DeviceProc(VkDevice, const char* name)
    {
        if (!name || std::strcmp(name, "vkUnavailable") == 0) return nullptr;
        if (std::strcmp(name, "vkGetDeviceQueue") == 0) return (PFN_vkVoidFunction)GetQueue;
        if (std::strcmp(name, "vkGetSwapchainImagesKHR") == 0) return (PFN_vkVoidFunction)GetImages;
        return (PFN_vkVoidFunction)UnusedCommand;
    }

    VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL InstanceProc(VkInstance, const char* name)
    {
        if (!name || std::strcmp(name, "vkCreateInstance") == 0) return nullptr;
        if (std::strcmp(name, "vkGetInstanceProcAddr") == 0) return (PFN_vkVoidFunction)InstanceProc;
        if (std::strcmp(name, "vkGetDeviceProcAddr") == 0) return (PFN_vkVoidFunction)DeviceProc;
        if (std::strcmp(name, "vkEnumeratePhysicalDevices") == 0) return (PFN_vkVoidFunction)Enumerate;
        if (std::strcmp(name, "vkCreateDevice") == 0) return (PFN_vkVoidFunction)CreateDevice;
        return DeviceProc(VK_NULL_HANDLE, name);
    }
}

void DispatchAndMetadataRegression()
{
    using namespace dispatch_tests;
    auto& rt = g_rt;
    Require(ResetRuntimeLocked(rt), "reset dispatch test runtime");
    rt.originalGetInstanceProcAddr = InstanceProc;
    Require(Hook_vkGetInstanceProcAddr(instance, "vkGetInstanceProcAddr") == (PFN_vkVoidFunction)Hook_vkGetInstanceProcAddr,
        "instance resolver cannot bypass interception by resolving itself");
    const auto getDeviceProc = reinterpret_cast<PFN_vkGetDeviceProcAddr>(Hook_vkGetInstanceProcAddr(instance, "vkGetDeviceProcAddr"));
    Require(getDeviceProc == Hook_vkGetDeviceProcAddr && rt.originalGetDeviceProcAddr == DeviceProc,
        "instance lookup of device resolver retains x86 interception");
    const struct { const char* name; PFN_vkVoidFunction hook; } commands[] = {
        {"vkGetDeviceQueue", (PFN_vkVoidFunction)Hook_vkGetDeviceQueue},
        {"vkGetDeviceQueue2", (PFN_vkVoidFunction)Hook_vkGetDeviceQueue2},
        {"vkCreateSwapchainKHR", (PFN_vkVoidFunction)Hook_vkCreateSwapchainKHR},
        {"vkDestroyDevice", (PFN_vkVoidFunction)Hook_vkDestroyDevice},
        {"vkDestroySwapchainKHR", (PFN_vkVoidFunction)Hook_vkDestroySwapchainKHR},
        {"vkAcquireNextImageKHR", (PFN_vkVoidFunction)Hook_vkAcquireNextImageKHR},
        {"vkAcquireNextImage2KHR", (PFN_vkVoidFunction)Hook_vkAcquireNextImage2KHR},
        {"vkQueuePresentKHR", (PFN_vkVoidFunction)Hook_vkQueuePresentKHR},
    };
    for (const auto& command : commands)
    {
        Require(Hook_vkGetInstanceProcAddr(instance, command.name) == command.hook, "instance lookup wraps device commands");
        Require(getDeviceProc(testDevice, command.name) == command.hook, "device lookup wraps the same commands");
    }
    Require(Hook_vkGetInstanceProcAddr(instance, "vkCreateInstance") == nullptr, "missing global command stays null");
    Require(Hook_vkGetInstanceProcAddr(instance, "vkUnavailable") == nullptr && getDeviceProc(testDevice, "vkUnavailable") == nullptr,
        "unsupported lookups stay null");
    Require(Hook_vkGetInstanceProcAddr(instance, "vkUnrelated") == (PFN_vkVoidFunction)UnusedCommand,
        "unrelated command is unchanged");
    Require(WrapDeviceProcLocked(rt, "vkQueuePresentKHR", nullptr) == nullptr, "missing known device command stays null");

    const char* scenarios[] = {"captured_single", "additional_family", "single_device_group", "multi_gpu",
        "backfill_single_adapter", "backfill_ambiguous_adapter", "empty_wait"};
    for (unsigned scenario = 0; scenario < std::size(scenarios); ++scenario)
    {
        Require(ResetRuntimeLocked(rt), "reset before metadata scenario");
        rt.originalGetInstanceProcAddr = InstanceProc;
        rt.qpcFreq = QueryQpcFreq();
        adapterCount = scenario == 5 ? 2 : 1;
        const auto createDevice = reinterpret_cast<PFN_vkCreateDevice>(Hook_vkGetInstanceProcAddr(instance, "vkCreateDevice"));
        const auto getQueue = reinterpret_cast<PFN_vkGetDeviceQueue>(Hook_vkGetInstanceProcAddr(instance, "vkGetDeviceQueue"));
        (void)Hook_vkGetInstanceProcAddr(instance, "vkGetSwapchainImagesKHR");
        VkDeviceQueueCreateInfo queues[2]{};
        for (auto& queue : queues) { queue.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO; queue.queueCount = 1; }
        queues[1].queueFamilyIndex = 1;
        VkDeviceCreateInfo create{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
        create.queueCreateInfoCount = scenario == 1 ? 2 : 1;
        create.pQueueCreateInfos = queues;
        const VkPhysicalDevice physicalDevices[] = {physical, (VkPhysicalDevice)24};
        VkDeviceGroupDeviceCreateInfo group{VK_STRUCTURE_TYPE_DEVICE_GROUP_DEVICE_CREATE_INFO};
        group.physicalDeviceCount = scenario == 3 ? 2 : 1;
        group.pPhysicalDevices = physicalDevices;
        if (scenario == 2 || scenario == 3) create.pNext = &group;
        if (scenario != 4 && scenario != 5)
        {
            VkDevice device = VK_NULL_HANDLE;
            Require(createDevice(physical, &create, nullptr, &device) == VK_SUCCESS && device == testDevice,
                "device creation through instance resolver is captured");
        }
        VkQueue queue = VK_NULL_HANDLE;
        getQueue(testDevice, 0, 0, &queue);
        Require(queue == testQueue, "queue through instance resolver is captured");
        if (scenario == 5) Require(rt.devices.empty(), "ambiguous adapter is not guessed");
        else Require(rt.devices[testDevice].createInfoKnown == (scenario != 4), "backfill does not invent creation metadata");

        VkSwapchainCreateInfoKHR swap{VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR};
        swap.imageFormat = VK_FORMAT_B8G8R8A8_UNORM;
        swap.imageExtent = {2, 1};
        swap.imageArrayLayers = 1;
        swap.imageUsage = VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
        swap.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
        swap.presentMode = VK_PRESENT_MODE_FIFO_KHR;
        UpsertSwapchainFromCreateLocked(rt, testDevice, swapchain, &swap);
        auto& gpu = rt.queueGpuStates[testQueue];
        gpu.device = testDevice; gpu.queueFamily = 0; gpu.width = 2; gpu.height = 1;
        gpu.format = swap.imageFormat; gpu.stagingBytes = 8; gpu.captureRingSize = 3;
        gpu.commandPool = (VkCommandPool)25; gpu.commandBuffer = (VkCommandBuffer)26;
        gpu.fence = (VkFence)27; gpu.stagingBuffer = (VkBuffer)28; gpu.stagingMemory = (VkDeviceMemory)29;
        std::uint8_t pixels[8]{};
        gpu.stagingMapped = pixels;
        gpu.captureSlots.resize(3);
        VkPresentInfoKHR present{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
        const VkSemaphore semaphore = (VkSemaphore)30;
        present.waitSemaphoreCount = scenario == 6 ? 0 : 1;
        present.pWaitSemaphores = &semaphore;
        rt.lastPresentQpc = NowQpc();
        capturePreparationCalls = 0;
        probeCapturePreparation = true;
        VkResult result = VK_SUCCESS;
        (void)SubmitPresentWorkLocked(rt, queue, swapchain, 0, present, result);
        probeCapturePreparation = false;
        const bool supported = scenario != 3 && scenario != 5 && scenario != 6;
        if ((capturePreparationCalls == 1) != supported)
        {
            std::fprintf(stderr, "Metadata scenario failed: %s\n", scenarios[scenario]);
            Require(false, "supported metadata reaches capture preparation; unsupported metadata skips");
        }
        // No GPU object was created; discard fake handles before normal runtime teardown.
        rt.queueGpuStates.clear();
    }
    Require(ResetRuntimeLocked(rt), "cleanup dispatch test runtime");
    std::puts("PASS: instance/device lookup, creation metadata and late-device capture regression");
}

#include <windows.h>
#include <vulkan/vulkan.h>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <future>
#include <memory>
#include <thread>

namespace
{
    std::atomic<VkResult> fenceResult{VK_SUCCESS};
    std::atomic<VkResult> waitResult{VK_SUCCESS};
    std::atomic_uint waitCalls{0};
    std::atomic_bool blockWait{false};
    HANDLE waitEntered = nullptr;
    HANDLE waitRelease = nullptr;
    bool realGpu = false;
    std::atomic_uint validationErrors{0};

    VKAPI_ATTR VkResult VKAPI_CALL TestGetFenceStatus(VkDevice device, VkFence fence)
    {
        if (realGpu)
        {
            return vkGetFenceStatus(device, fence);
        }
        return fenceResult.load();
    }

    VKAPI_ATTR VkResult VKAPI_CALL TestWaitForFences(VkDevice device, std::uint32_t count, const VkFence* fences, VkBool32 all, std::uint64_t timeout)
    {
        ++waitCalls;
        if (realGpu)
        {
            return vkWaitForFences(device, count, fences, all, timeout);
        }
        if (blockWait)
        {
            SetEvent(waitEntered);
            WaitForSingleObject(waitRelease, INFINITE);
        }
        return waitResult.load();
    }

    void Require(bool condition, const char* message)
    {
        if (!condition)
        {
            std::fprintf(stderr, "FAIL: %s\n", message);
            std::exit(1);
        }
    }
}

// WHY: Only GPU completion is simulated. Queue ownership, Windows events, the publish thread,
// pixel conversion and the V2 shared-memory writer below are the production implementation.
#define vkGetFenceStatus TestGetFenceStatus
#define vkWaitForFences TestWaitForFences
#include "../VulkanPresentHook.cpp"
#undef vkGetFenceStatus
#undef vkWaitForFences

using namespace ht::hook::vulkan;

namespace
{
    const auto testQueue = reinterpret_cast<VkQueue>(static_cast<std::uintptr_t>(1));
    const auto testDevice = reinterpret_cast<VkDevice>(static_cast<std::uintptr_t>(2));

    void ReadbackAtLowFps()
    {
        auto runtime = std::make_unique<VulkanRuntime>();
        auto& rt = *runtime;
        rt.qpcFreq = QueryQpcFreq();
        rt.perfDiagLogEnabled = true;
        Require(EnsurePublishWorkerLocked(rt), "start worker");
        auto& gpu = rt.queueGpuStates[testQueue];
        gpu.device = testDevice;
        gpu.publishGeneration = 7;
        gpu.queueFamily = 0;
        gpu.width = 2;
        gpu.height = 1;
        gpu.format = VK_FORMAT_B8G8R8A8_UNORM;
        gpu.stagingBytes = 8;
        gpu.commandPool = (VkCommandPool)3;
        gpu.commandBuffer = (VkCommandBuffer)4;
        gpu.fence = (VkFence)5;
        gpu.stagingBuffer = (VkBuffer)6;
        gpu.stagingMemory = (VkDeviceMemory)7;
        gpu.captureRingSize = 3;
        gpu.captureSlots.resize(3);
        rt.queues[testQueue] = QueueInfo{testDevice, 0, true};
        rt.devices[testDevice] = DeviceInfo{(VkPhysicalDevice)8};
        const auto swapchain = (VkSwapchainKHR)9;
        rt.swapchains[swapchain] = SwapchainInfo{testDevice, gpu.format, {2, 1}, {(VkImage)10}};
        auto& slot = gpu.captureSlots[0];
        std::uint8_t pixels[] = {11, 22, 33, 255, 44, 55, 66, 255};
        slot.stagingMapped = pixels;
        gpu.stagingMapped = pixels;
        slot.stagingBytes = sizeof(pixels);
        slot.width = 2;
        slot.height = 1;
        const auto callsBefore = waitCalls.load();
        std::uint64_t expectedFrames = 0;
        for (const auto fps : {1u, 5u, 15u})
        {
            rt.captureIntervalQpc = rt.qpcFreq / fps;
            slot.submitQpc = NowQpc();
            slot.state = CaptureSlotState::Pending;
            slot.rgbaNeedsSwap = fps == 5;
            gpu.lastCaptureIssueQpc = slot.submitQpc;
            const auto issued = gpu.lastCaptureIssueQpc;
            rt.lastPresentQpc = issued + rt.qpcFreq / 60;
            Require(!ShouldCaptureNowLocked(rt, gpu, issued + rt.qpcFreq / 60), "60 FPS tick must not issue another capture");
            fenceResult = VK_NOT_READY;
            Require(SubmitPresentWorkLocked(rt, testQueue, swapchain, 0), "pending GPU is deferred on no-capture present");
            Require(slot.state == CaptureSlotState::Pending && rt.frameId == expectedFrames, "unsignaled frame is not enqueued");
            fenceResult = VK_SUCCESS;
            Require(SubmitPresentWorkLocked(rt, testQueue, swapchain, 0), "ready GPU is polled independently of capture gate");
            ++expectedFrames;
            Require(slot.state == CaptureSlotState::Publishing, "ready slot ownership transfers to worker");
            Require(DrainPublishQueueLocked(rt), "drain ready frame");
            Require(slot.state == CaptureSlotState::Free, "slot released only after worker completion");
            Require(gpu.lastCaptureIssueQpc == issued, "retirement must not change capture schedule");

            const auto map = OpenFileMappingW(FILE_MAP_READ, FALSE, rt.frameWriter.MappingName().c_str());
            Require(map != nullptr, "open real V2 output");
            const auto* data = static_cast<const std::uint8_t*>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, 0));
            Require(data != nullptr, "map real V2 output");
            const auto* pipe = reinterpret_cast<const ht::hook::ipc::FramePipeHeaderV2*>(data);
            const auto* frame = reinterpret_cast<const ht::hook::ipc::FrameSlotHeaderV2*>(
                data + sizeof(*pipe) + ht::hook::ipc::FrameSlotBytes(pipe->payloadCapacity) * pipe->publishedIndex);
            const auto* payload = reinterpret_cast<const std::uint8_t*>(frame + 1);
            Require(frame->slotSeq == pipe->publishedSeq && frame->frameId == rt.frameId, "V2 sequence is committed");
            Require(payload[0] == (slot.rgbaNeedsSwap ? 33 : 11) && payload[1] == 22 && payload[3] == 255, "BGRA payload and RGBA conversion");
            UnmapViewOfFile(data);
            CloseHandle(map);
        }
        Require(waitCalls == callsBefore, "normal retirement must not wait for GPU");
        Require(rt.capturePublishCount == 3, "one publish for each ready frame");
        Require(rt.perfSamples[static_cast<std::size_t>(PerfMetric::CaptureAge)].count == 3, "worker timing collected");

        slot.state = CaptureSlotState::Pending;
        slot.submitQpc = NowQpc();
        fenceResult = VK_ERROR_DEVICE_LOST;
        Require(!PollCompletedCapturesLocked(rt, testQueue, gpu), "fence error is explicit");
        Require(gpu.failed && slot.state == CaptureSlotState::Failed, "failed fence must not free GPU-owned slot");
        Require(FindFreeCaptureSlotLocked(gpu) != &slot, "failed slot cannot be reused");
        Require(StopPublishWorkerLocked(rt), "stop worker");
        gpu.captureSlots.clear();
        gpu.device = VK_NULL_HANDLE;
        std::puts("PASS: low-FPS retirement, pixels, timing and fence errors");
    }

    void WorkerOwnershipAndIdle()
    {
        auto runtime = std::make_unique<VulkanRuntime>();
        auto& rt = *runtime;
        Require(EnsurePublishWorkerLocked(rt), "start ownership worker");
        CaptureSlot slot{};
        std::uint8_t pixel[] = {1, 2, 3, 255};
        slot.stagingMapped = pixel;
        slot.stagingBytes = sizeof(pixel);
        VulkanPublishRequest request{};
        request.slot = &slot;
        request.pid = GetCurrentProcessId();
        request.width = request.height = 1;
        request.stride = 4;
        request.bytes = 4;
        rt.frameWriterMutex.lock();
        Require(EnqueuePublishRequestLocked(rt, request), "enqueue blocked writer");
        auto drain = std::async(std::launch::async, [&] { return DrainPublishQueueLocked(rt); });
        Require(drain.wait_for(std::chrono::milliseconds(30)) == std::future_status::timeout, "drain must retain mapping while worker reads");
        Require(WaitForSingleObject(rt.publishIdleEvent, 0) == WAIT_TIMEOUT, "active queue is not idle");
        rt.frameWriterMutex.unlock();
        Require(drain.wait_for(std::chrono::seconds(2)) == std::future_status::ready && drain.get(), "drain resumes after writer completes");

        for (unsigned round = 0; round < 2000; ++round)
        {
            // WHY: Repeated idle->enqueue transitions cross worker completion and enqueue scheduling.
            request.frameId = round + 1;
            Require(EnqueuePublishRequestLocked(rt, request), "stress enqueue");
            if ((round & 3u) == 0)
            {
                SwitchToThread();
            }
            Require(DrainPublishQueueLocked(rt), "stress drain");
            Require(WaitForSingleObject(rt.publishIdleEvent, 0) == WAIT_OBJECT_0, "empty queue keeps idle signaled");
        }
        Require(StopPublishWorkerLocked(rt), "stress stop");
        std::puts("PASS: active ownership and 2000 idle/enqueue transitions");
    }

    void GpuRetirement()
    {
        auto runtime = std::make_unique<VulkanRuntime>();
        QueueGpuState gpu{};
        gpu.device = testDevice;
        gpu.captureSlots.resize(1);
        gpu.captureSlots[0].state = CaptureSlotState::Pending;
        gpu.captureSlots[0].submitQpc = 1;
        gpu.submitPending = true;
        waitResult = VK_SUCCESS;
        waitEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        waitRelease = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        blockWait = true;
        auto retire = std::async(std::launch::async, [&] { return WaitForQueueGpuWorkLocked(*runtime, gpu); });
        Require(WaitForSingleObject(waitEntered, 2000) == WAIT_OBJECT_0, "teardown reaches GPU completion wait");
        Require(retire.wait_for(std::chrono::milliseconds(20)) == std::future_status::timeout, "teardown cannot overtake GPU");
        SetEvent(waitRelease);
        Require(retire.get(), "GPU retirement completes");
        blockWait = false;
        Require(!gpu.submitPending && gpu.captureSlots[0].state == CaptureSlotState::Free, "both immediate and capture work retired");
        CloseHandle(waitEntered);
        CloseHandle(waitRelease);

        gpu.captureSlots[0].state = CaptureSlotState::Pending;
        gpu.captureSlots[0].submitQpc = 1;
        waitResult = VK_ERROR_OUT_OF_HOST_MEMORY;
        Require(!WaitForQueueGpuWorkLocked(*runtime, gpu), "retirement failure is not success");
        Require(gpu.captureSlots[0].state == CaptureSlotState::Pending, "failed retirement retains resources");
        waitResult = VK_ERROR_DEVICE_LOST;
        Require(WaitForQueueGpuWorkLocked(*runtime, gpu), "lost-device resources can be retired");
        std::puts("PASS: GPU teardown ordering and retirement errors");
    }

    void PublishFailuresAndGeneration()
    {
        auto runtime = std::make_unique<VulkanRuntime>();
        auto& rt = *runtime;
        Require(EnsurePublishWorkerLocked(rt), "start failure test worker");
        auto& gpu = rt.queueGpuStates[testQueue];
        gpu.publishGeneration = 2;
        gpu.captureSlots.resize(1);
        auto& slot = gpu.captureSlots[0];
        slot.state = CaptureSlotState::Publishing;
        VulkanPublishRequest invalid{};
        invalid.slot = &slot;
        invalid.queue = testQueue;
        invalid.generation = 2;
        Require(EnqueuePublishRequestLocked(rt, invalid), "enqueue invalid payload");
        Require(DrainPublishQueueLocked(rt), "failed payload still completes ownership transfer");
        Require(slot.state == CaptureSlotState::Free && rt.capturePublishCount == 0, "failed publish releases slot without reporting success");

        slot.state = CaptureSlotState::Publishing;
        VulkanPublishCompletion stale{};
        stale.slot = &slot;
        stale.queue = testQueue;
        stale.generation = 1;
        stale.writeSucceeded = true;
        EnterCriticalSection(&rt.publishQueueLock);
        rt.publishCompleted.push_back(stale);
        LeaveCriticalSection(&rt.publishQueueLock);
        DrainCompletedPublishesLocked(rt);
        Require(slot.state == CaptureSlotState::Publishing && rt.capturePublishCount == 0, "old generation cannot release a current slot or count as published");
        slot.state = CaptureSlotState::Free;
        Require(StopPublishWorkerLocked(rt), "stop failure test worker");
        Require(!EnqueuePublishRequestLocked(rt, invalid), "stopped worker rejects new work");
        std::puts("PASS: publish failures and stale-generation ownership");
    }

    void DeadWorkerAndPerfOrdering()
    {
        auto runtime = std::make_unique<VulkanRuntime>();
        auto& rt = *runtime;
        Require(EnsurePublishWorkerLocked(rt), "start dead-worker test");
        SetEvent(rt.publishStopEvent);
        Require(WaitForSingleObject(rt.publishThreadHandle, 2000) == WAIT_OBJECT_0, "worker exited");
        EnterCriticalSection(&rt.publishQueueLock);
        rt.publishQueue.push_back(VulkanPublishRequest{});
        ResetEvent(rt.publishIdleEvent);
        LeaveCriticalSection(&rt.publishQueueLock);
        Require(!DrainPublishQueueLocked(rt), "dead worker cannot report pending requests as drained");
        EnterCriticalSection(&rt.publishQueueLock);
        rt.publishQueue.clear();
        SetEvent(rt.publishIdleEvent);
        LeaveCriticalSection(&rt.publishQueueLock);
        Require(StopPublishWorkerLocked(rt), "close exited worker after test cleanup");

        rt.perfWindowQpc = 10000;
        RecordPerf(rt, PerfMetric::HookTotal, 42);
        EmitPerfWindow(rt, 9999, 1000);
        Require(rt.perfWindowQpc == 10000 && rt.perfSamples[static_cast<std::size_t>(PerfMetric::HookTotal)].count == 1,
            "out-of-order presents must not underflow the reporting interval");
        std::puts("PASS: dead-worker detection and concurrent metric ordering");
    }

    VKAPI_ATTR VkBool32 VKAPI_CALL ValidationMessage(VkDebugUtilsMessageSeverityFlagBitsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT, const VkDebugUtilsMessengerCallbackDataEXT* data, void*)
    {
        if (severity & VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT)
        {
            ++validationErrors;
            std::fprintf(stderr, "VALIDATION: %s\n", data->pMessage);
        }
        return VK_FALSE;
    }

    void RealGpuReadback()
    {
        realGpu = true;
        auto runtime = std::make_unique<VulkanRuntime>();
        auto& rt = *runtime;
        rt.qpcFreq = QueryQpcFreq();
        rt.perfDiagLogEnabled = true;
        VkDebugUtilsMessengerCreateInfoEXT debug{VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT};
        debug.messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT;
        debug.messageType = VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT;
        debug.pfnUserCallback = ValidationMessage;
        const VkValidationFeatureEnableEXT sync = VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT;
        VkValidationFeaturesEXT validation{VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT};
        validation.pNext = &debug;
        validation.enabledValidationFeatureCount = 1;
        validation.pEnabledValidationFeatures = &sync;
        const char* layers[] = {"VK_LAYER_KHRONOS_validation"};
        const char* extensions[] = {VK_EXT_DEBUG_UTILS_EXTENSION_NAME, VK_EXT_VALIDATION_FEATURES_EXTENSION_NAME};
        VkInstanceCreateInfo info{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
        info.pNext = &validation;
        info.enabledLayerCount = 1;
        info.ppEnabledLayerNames = layers;
        info.enabledExtensionCount = 2;
        info.ppEnabledExtensionNames = extensions;
        VkInstance instance = VK_NULL_HANDLE;
        Require(vkCreateInstance(&info, nullptr, &instance) == VK_SUCCESS, "create validation-enabled instance");
        const auto createDebug = reinterpret_cast<PFN_vkCreateDebugUtilsMessengerEXT>(vkGetInstanceProcAddr(instance, "vkCreateDebugUtilsMessengerEXT"));
        const auto destroyDebug = reinterpret_cast<PFN_vkDestroyDebugUtilsMessengerEXT>(vkGetInstanceProcAddr(instance, "vkDestroyDebugUtilsMessengerEXT"));
        VkDebugUtilsMessengerEXT messenger = VK_NULL_HANDLE;
        Require(createDebug(instance, &debug, nullptr, &messenger) == VK_SUCCESS, "create validation messenger");
        std::uint32_t count = 0;
        Require(vkEnumeratePhysicalDevices(instance, &count, nullptr) == VK_SUCCESS && count > 0, "GPU available");
        std::vector<VkPhysicalDevice> devices(count);
        Require(vkEnumeratePhysicalDevices(instance, &count, devices.data()) == VK_SUCCESS, "enumerate GPUs");
        const auto physical = devices[0];
        vkGetPhysicalDeviceQueueFamilyProperties(physical, &count, nullptr);
        std::vector<VkQueueFamilyProperties> families(count);
        vkGetPhysicalDeviceQueueFamilyProperties(physical, &count, families.data());
        std::uint32_t family = 0;
        while (family < count && !(families[family].queueFlags & VK_QUEUE_GRAPHICS_BIT)) ++family;
        Require(family < count, "graphics queue available");
        float priority = 1;
        VkDeviceQueueCreateInfo queueInfo{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
        queueInfo.queueFamilyIndex = family;
        queueInfo.queueCount = 1;
        queueInfo.pQueuePriorities = &priority;
        VkDeviceCreateInfo deviceInfo{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
        deviceInfo.queueCreateInfoCount = 1;
        deviceInfo.pQueueCreateInfos = &queueInfo;
        VkDevice device = VK_NULL_HANDLE;
        Require(vkCreateDevice(physical, &deviceInfo, nullptr, &device) == VK_SUCCESS, "create real device");
        VkQueue queue = VK_NULL_HANDLE;
        vkGetDeviceQueue(device, family, 0, &queue);
        auto& gpu = rt.queueGpuStates[queue];
        gpu.device = device;
        gpu.publishGeneration = 1;
        gpu.captureSlots.resize(1);
        auto& slot = gpu.captureSlots[0];
        // WHY: An 8-byte image exposes memory-requirement padding that used to be treated as payload.
        Require(CreateCaptureSlotResources(device, physical, family, 8, 2, 1, VK_FORMAT_B8G8R8A8_UNORM, false, slot), "create real staging resources");
        Require(slot.stagingBytes == 8, "allocator padding is excluded from frame payload");
        Require(EnsurePublishWorkerLocked(rt), "start real GPU publisher");
        for (unsigned iteration = 0; iteration < 12; ++iteration)
        {
            Require(vkResetCommandPool(device, slot.commandPool, 0) == VK_SUCCESS, "reset retired command pool");
            Require(vkResetFences(device, 1, &slot.fence) == VK_SUCCESS, "reset retired fence");
            VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
            Require(vkBeginCommandBuffer(slot.commandBuffer, &begin) == VK_SUCCESS, "begin GPU fill");
            vkCmdFillBuffer(slot.commandBuffer, slot.stagingBuffer, 0, 8, 0xff332211);
            CmdMakeReadbackVisible(slot.commandBuffer);
            Require(vkEndCommandBuffer(slot.commandBuffer) == VK_SUCCESS, "end GPU fill");
            VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
            submit.commandBufferCount = 1;
            submit.pCommandBuffers = &slot.commandBuffer;
            Require(vkQueueSubmit(queue, 1, &submit, slot.fence) == VK_SUCCESS, "submit real GPU write");
            slot.submitQpc = NowQpc();
            slot.state = CaptureSlotState::Pending;
            const auto deadline = NowQpc() + rt.qpcFreq * 5;
            while (slot.state == CaptureSlotState::Pending && NowQpc() < deadline)
            {
                Require(PollCompletedCapturesLocked(rt, queue, gpu), "poll real GPU fence");
                SwitchToThread();
            }
            Require(slot.state == CaptureSlotState::Publishing, "real GPU completion reached publisher");
            Require(DrainPublishQueueLocked(rt), "real GPU publish drain");
            Require(rt.publishScratch.size() == 8 && rt.publishScratch[0] == 0x11 && rt.publishScratch[3] == 0xff, "real GPU pixels published without padding");
        }
        Require(WaitForQueueGpuWorkLocked(rt, gpu), "retire real GPU work");
        Require(StopPublishWorkerLocked(rt), "stop real GPU publisher before unmap");
        DestroyQueueGpuState(gpu);
        vkDestroyDevice(device, nullptr);
        destroyDebug(instance, messenger, nullptr);
        vkDestroyInstance(instance, nullptr);
        Require(validationErrors == 0, "validation and synchronization validation report no errors");
        std::puts("PASS: real GPU staging, 12 publishes and teardown with synchronization validation");
    }
}

int main(int argc, char** argv)
{
    SetEnvironmentVariableW(L"HT_HOOK_VK_CAPTURE_RING_SIZE", L"3");
    SetEnvironmentVariableW(L"HT_HOOK_VK_DISABLE_DELAYED_READBACK", L"0");
    if (argc == 2 && std::strcmp(argv[1], "--gpu") == 0)
    {
        RealGpuReadback();
        return 0;
    }
    ReadbackAtLowFps();
    WorkerOwnershipAndIdle();
    GpuRetirement();
    PublishFailuresAndGeneration();
    DeadWorkerAndPerfOrdering();
    return 0;
}

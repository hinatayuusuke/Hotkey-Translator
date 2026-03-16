#include "VulkanPresentHook.h"

#include <windows.h>

extern "C" __declspec(dllexport) BOOL __stdcall InstallVulkanHook()
{
    return ht::hook::vulkan::InstallPresentHook() ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) void __stdcall UninstallVulkanHook()
{
    ht::hook::vulkan::UninstallPresentHook();
}

extern "C" __declspec(dllexport) DWORD __stdcall InstallVulkanHookThread(void* /*unused*/)
{
    ht::hook::vulkan::LogInstallThreadEvent(
        "stage=hook_vulkan event=install_thread_enter pid=%lu tid=%lu.",
        static_cast<unsigned long>(GetCurrentProcessId()),
        static_cast<unsigned long>(GetCurrentThreadId()));
    __try
    {
        const DWORD exitCode = InstallVulkanHook() ? 1u : 0u;
        ht::hook::vulkan::LogInstallThreadEvent(
            "stage=hook_vulkan event=install_thread_exit pid=%lu tid=%lu exit=%lu.",
            static_cast<unsigned long>(GetCurrentProcessId()),
            static_cast<unsigned long>(GetCurrentThreadId()),
            static_cast<unsigned long>(exitCode));
        return exitCode;
    }
    __except (ht::hook::vulkan::HandleInstallThreadException(GetExceptionCode()))
    {
        return 0u;
    }
}

extern "C" __declspec(dllexport) DWORD __stdcall UninstallVulkanHookThread(void* /*unused*/)
{
    UninstallVulkanHook();
    return 1u;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
        case DLL_PROCESS_ATTACH:
            DisableThreadLibraryCalls(module);
            break;
        case DLL_PROCESS_DETACH:
            if (reserved == nullptr)
            {
                UninstallVulkanHook();
            }
            // WHY: During process exit (`reserved != nullptr`), avoid graceful worker shutdown from DllMain.
            // The OS tears the process down anyway, and waiting here risks loader-lock issues.
            break;
        default:
            break;
    }

    return TRUE;
}

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
    return InstallVulkanHook() ? 1u : 0u;
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
            break;
        default:
            break;
    }

    return TRUE;
}

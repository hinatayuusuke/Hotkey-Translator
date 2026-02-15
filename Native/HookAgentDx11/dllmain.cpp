#include "Dx11PresentHook.h"

#include <windows.h>

extern "C" __declspec(dllexport) BOOL __stdcall InstallDx11Hook()
{
    return ht::hook::dx11::InstallPresentHook() ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) void __stdcall UninstallDx11Hook()
{
    ht::hook::dx11::UninstallPresentHook();
}

extern "C" __declspec(dllexport) DWORD __stdcall InstallDx11HookThread(void* /*unused*/)
{
    return InstallDx11Hook() ? 1u : 0u;
}

extern "C" __declspec(dllexport) DWORD __stdcall UninstallDx11HookThread(void* /*unused*/)
{
    UninstallDx11Hook();
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
                UninstallDx11Hook();
            }
            break;
        default:
            break;
    }

    return TRUE;
}

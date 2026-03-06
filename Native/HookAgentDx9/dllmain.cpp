#include "Dx9PresentHook.h"

#include <windows.h>

extern "C" __declspec(dllexport) BOOL __stdcall InstallDx9Hook()
{
    return ht::hook::dx9::InstallPresentHook() ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) void __stdcall UninstallDx9Hook()
{
    ht::hook::dx9::UninstallPresentHook();
}

extern "C" __declspec(dllexport) DWORD __stdcall InstallDx9HookThread(void* /*unused*/)
{
    return InstallDx9Hook() ? 1u : 0u;
}

extern "C" __declspec(dllexport) DWORD __stdcall UninstallDx9HookThread(void* /*unused*/)
{
    UninstallDx9Hook();
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
                UninstallDx9Hook();
            }
            break;
        default:
            break;
    }

    return TRUE;
}


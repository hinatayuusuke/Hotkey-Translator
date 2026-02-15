#include "Dx11PresentHook.h"

#include <atomic>

namespace ht::hook::dx11
{
    namespace
    {
        std::atomic_bool g_installed{false};
    }

    bool InstallPresentHook()
    {
        // WHY: This repository step introduces the integration boundary first; MinHook + vtable interception follows next step.
        g_installed.store(true, std::memory_order_release);
        return true;
    }

    void UninstallPresentHook()
    {
        g_installed.store(false, std::memory_order_release);
    }
}

#pragma once

#include <windows.h>

namespace ht::hook::dx11
{
    bool InstallPresentHook();
    void UninstallPresentHook();
}

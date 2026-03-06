#pragma once

#include <windows.h>

namespace ht::hook::dx9
{
    bool InstallPresentHook();
    void UninstallPresentHook();
}


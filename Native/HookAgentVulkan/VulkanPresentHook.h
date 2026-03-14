#pragma once

namespace ht::hook::vulkan
{
    bool InstallPresentHook();
    void UninstallPresentHook();
    void LogInstallThreadEvent(const char* fmt, ...);
    int HandleInstallThreadException(unsigned long exceptionCode);
}

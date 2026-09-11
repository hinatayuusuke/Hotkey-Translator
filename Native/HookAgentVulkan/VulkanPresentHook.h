#pragma once

namespace ht::hook::vulkan
{
    // NOTE: The Host must publish the shared config before installation; missing config fails
    // installation so startup diagnostics cannot bypass the user's file-output policy.
    bool InstallPresentHook();
    void UninstallPresentHook();
    void LogInstallThreadEvent(const char* fmt, ...);
    int HandleInstallThreadException(unsigned long exceptionCode);
}

cmake_minimum_required(VERSION 3.20)
project(HotkeyTranslatorHookNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

option(HT_VULKAN_BUILD_TESTS "Build Vulkan hook lifecycle and readback regression tests" OFF)
if (HT_VULKAN_BUILD_TESTS)
    enable_testing()
endif()

add_subdirectory(ThirdParty/MinHook)
add_subdirectory(HookHost)
add_subdirectory(HookAgentDx11)
add_subdirectory(HookAgentDx9)
add_subdirectory(HookAgentVulkan)

# WHY: OneOCR helper is currently packaged and consumed only as an x64 sidecar.
# Building the same target in the Win32 tree would collide with the shared output path
# and provides no runtime value because the WPF app itself is distributed as x64.
if (CMAKE_SIZEOF_VOID_P EQUAL 8)
    add_subdirectory(OneOcrHelper)
endif()

set(HT_VULKAN_SDK_ROOT_OVERRIDE "" CACHE PATH "Optional explicit Vulkan SDK root for HookAgentVulkan")
set(HT_VULKAN_INCLUDE_DIR_OVERRIDE "" CACHE PATH "Optional explicit Vulkan include directory for HookAgentVulkan")
set(HT_VULKAN_LIBRARY_OVERRIDE "" CACHE FILEPATH "Optional explicit vulkan-1 import library for HookAgentVulkan")

set(HT_VULKAN_INCLUDE_DIR "")
set(HT_VULKAN_LIBRARY "")
set(HT_VULKAN_SOURCE "")

if (HT_VULKAN_SDK_ROOT_OVERRIDE)
    set(HT_VULKAN_INCLUDE_DIR "${HT_VULKAN_SDK_ROOT_OVERRIDE}/Include")
    if (CMAKE_SIZEOF_VOID_P EQUAL 4)
        set(HT_VULKAN_LIBRARY "${HT_VULKAN_SDK_ROOT_OVERRIDE}/Lib32/vulkan-1.lib")
    else()
        set(HT_VULKAN_LIBRARY "${HT_VULKAN_SDK_ROOT_OVERRIDE}/Lib/vulkan-1.lib")
    endif()
    set(HT_VULKAN_SOURCE "sdk_root_override")
elseif (HT_VULKAN_INCLUDE_DIR_OVERRIDE AND HT_VULKAN_LIBRARY_OVERRIDE)
    set(HT_VULKAN_INCLUDE_DIR "${HT_VULKAN_INCLUDE_DIR_OVERRIDE}")
    set(HT_VULKAN_LIBRARY "${HT_VULKAN_LIBRARY_OVERRIDE}")
    set(HT_VULKAN_SOURCE "pair_override")
elseif (DEFINED ENV{VULKAN_SDK} AND NOT "$ENV{VULKAN_SDK}" STREQUAL "")
    set(HT_VULKAN_INCLUDE_DIR "$ENV{VULKAN_SDK}/Include")
    if (CMAKE_SIZEOF_VOID_P EQUAL 4)
        set(HT_VULKAN_LIBRARY "$ENV{VULKAN_SDK}/Lib32/vulkan-1.lib")
    else()
        set(HT_VULKAN_LIBRARY "$ENV{VULKAN_SDK}/Lib/vulkan-1.lib")
    endif()
    set(HT_VULKAN_SOURCE "env_vulkan_sdk")
endif()

if (HT_VULKAN_SOURCE STREQUAL "" AND (NOT EXISTS "${HT_VULKAN_INCLUDE_DIR}/vulkan/vulkan.h" OR NOT EXISTS "${HT_VULKAN_LIBRARY}"))
    find_package(Vulkan QUIET)
    if (NOT Vulkan_FOUND)
        message(WARNING "Vulkan SDK not found. HookAgentVulkan target will be skipped.")
        return()
    endif()

    set(HT_VULKAN_INCLUDE_DIR "${Vulkan_INCLUDE_DIR}")
    set(HT_VULKAN_LIBRARY "${Vulkan_LIBRARY}")

    if (CMAKE_SIZEOF_VOID_P EQUAL 4)
        get_filename_component(HT_VULKAN_LIBRARY_DIR "${HT_VULKAN_LIBRARY}" DIRECTORY)
        get_filename_component(HT_VULKAN_SDK_ROOT "${HT_VULKAN_LIBRARY_DIR}" DIRECTORY)
        set(HT_VULKAN_LIBRARY_LIB32 "${HT_VULKAN_SDK_ROOT}/Lib32/vulkan-1.lib")
        if (EXISTS "${HT_VULKAN_LIBRARY_LIB32}")
            # WHY: LunarG's Windows SDK ships the 32-bit import library under Lib32 even when CMake initially finds the x64 root.
            set(HT_VULKAN_LIBRARY "${HT_VULKAN_LIBRARY_LIB32}")
        endif()
    endif()
endif()

if (NOT EXISTS "${HT_VULKAN_INCLUDE_DIR}/vulkan/vulkan.h" OR NOT EXISTS "${HT_VULKAN_LIBRARY}")
    message(WARNING "Resolved Vulkan include/lib pair is incomplete for HookAgentVulkan (include='${HT_VULKAN_INCLUDE_DIR}', lib='${HT_VULKAN_LIBRARY}'). Target will be skipped.")
    return()
endif()

message(STATUS "HookAgentVulkan include: ${HT_VULKAN_INCLUDE_DIR}")
message(STATUS "HookAgentVulkan library: ${HT_VULKAN_LIBRARY}")

add_library(HookAgentVulkan SHARED
    HookAgentVulkan.def
    dllmain.cpp
    VulkanPresentHook.cpp
    ../HookCommon/SharedFrameWriter.cpp
    ../HookCommon/SharedHookConfig.cpp
    ../HookCommon/SharedHookStatus.cpp
    ../HookCommon/SharedOverlayV2.cpp
    ../ThirdParty/imgui/imgui.cpp
    ../ThirdParty/imgui/imgui_draw.cpp
    ../ThirdParty/imgui/imgui_tables.cpp
    ../ThirdParty/imgui/imgui_widgets.cpp
    ../ThirdParty/imgui/backends/imgui_impl_vulkan.cpp
)

target_include_directories(HookAgentVulkan PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/../HookCommon
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/MinHook/include
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/imgui
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/imgui/backends
    ${HT_VULKAN_INCLUDE_DIR}
)

target_compile_definitions(HookAgentVulkan PRIVATE _UNICODE UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(HookAgentVulkan PRIVATE minhook "${HT_VULKAN_LIBRARY}")

set(HT_VULKAN_AGENT_BIN_DIR "${CMAKE_SOURCE_DIR}/HookHost/bin")
if (CMAKE_SIZEOF_VOID_P EQUAL 4)
    # WHY: The injector currently requires same-bitness host/agent pairs, so x86 builds must live beside the x86 HookHost.
    set(HT_VULKAN_AGENT_BIN_DIR "${CMAKE_SOURCE_DIR}/HookHost/bin/x86")
endif()

set_target_properties(HookAgentVulkan PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${HT_VULKAN_AGENT_BIN_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${HT_VULKAN_AGENT_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG   "${HT_VULKAN_AGENT_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${HT_VULKAN_AGENT_BIN_DIR}"
)

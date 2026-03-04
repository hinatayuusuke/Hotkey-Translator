find_package(Vulkan QUIET)
if (NOT Vulkan_FOUND)
    message(WARNING "Vulkan SDK not found. HookAgentVulkan target will be skipped.")
    return()
endif()

add_library(HookAgentVulkan SHARED
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
    ${Vulkan_INCLUDE_DIRS}
)

target_compile_definitions(HookAgentVulkan PRIVATE _UNICODE UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(HookAgentVulkan PRIVATE minhook Vulkan::Vulkan)

set_target_properties(HookAgentVulkan PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${CMAKE_SOURCE_DIR}/HookHost/bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/HookHost/bin"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG   "${CMAKE_SOURCE_DIR}/HookHost/bin"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/HookHost/bin"
)

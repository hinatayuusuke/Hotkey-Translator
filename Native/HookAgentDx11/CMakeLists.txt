add_library(HookAgentDx11 SHARED
    dllmain.cpp
    Dx11PresentHook.cpp
    ../HookCommon/SharedFrameWriter.cpp
    ../HookCommon/SharedHookConfig.cpp
    ../HookCommon/SharedOverlayV2.cpp
    ../HookCommon/SharedHookStatus.cpp
    ../ThirdParty/imgui/imgui.cpp
    ../ThirdParty/imgui/imgui_draw.cpp
    ../ThirdParty/imgui/imgui_tables.cpp
    ../ThirdParty/imgui/imgui_widgets.cpp
    ../ThirdParty/imgui/backends/imgui_impl_dx11.cpp
)

target_include_directories(HookAgentDx11 PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/../HookCommon
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/MinHook/include
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/imgui
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/imgui/backends
)

target_compile_definitions(HookAgentDx11 PRIVATE _UNICODE UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(HookAgentDx11 PRIVATE d3d11 dxgi minhook)

set(HT_DX11_AGENT_BIN_DIR "${CMAKE_SOURCE_DIR}/HookHost/bin")
if (CMAKE_SIZEOF_VOID_P EQUAL 4)
    # WHY: The injector currently requires same-bitness host/agent pairs, so x86 builds must live beside the x86 HookHost.
    set(HT_DX11_AGENT_BIN_DIR "${CMAKE_SOURCE_DIR}/HookHost/bin/x86")
endif()

set_target_properties(HookAgentDx11 PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${HT_DX11_AGENT_BIN_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${HT_DX11_AGENT_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG   "${HT_DX11_AGENT_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${HT_DX11_AGENT_BIN_DIR}"
)

add_library(HookAgentDx9 SHARED
    HookAgentDx9.def
    dllmain.cpp
    Dx9PresentHook.cpp
    ../HookCommon/SharedFrameWriter.cpp
    ../HookCommon/SharedHookConfig.cpp
    ../HookCommon/SharedHookStatus.cpp
    ../HookCommon/SharedOverlayV2.cpp
    ../ThirdParty/imgui/imgui.cpp
    ../ThirdParty/imgui/imgui_draw.cpp
    ../ThirdParty/imgui/imgui_tables.cpp
    ../ThirdParty/imgui/imgui_widgets.cpp
    ../ThirdParty/imgui/backends/imgui_impl_dx9.cpp
)

target_include_directories(HookAgentDx9 PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/../HookCommon
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/MinHook/include
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/imgui
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/imgui/backends
)

target_compile_definitions(HookAgentDx9 PRIVATE _UNICODE UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(HookAgentDx9 PRIVATE d3d9 minhook)

set(HT_DX9_AGENT_BIN_DIR "${CMAKE_SOURCE_DIR}/HookHost/bin")
if (CMAKE_SIZEOF_VOID_P EQUAL 4)
    set(HT_DX9_AGENT_BIN_DIR "${CMAKE_SOURCE_DIR}/HookHost/bin/x86")
endif()

set_target_properties(HookAgentDx9 PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${HT_DX9_AGENT_BIN_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${HT_DX9_AGENT_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG   "${HT_DX9_AGENT_BIN_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${HT_DX9_AGENT_BIN_DIR}"
)

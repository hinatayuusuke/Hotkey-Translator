add_library(HookAgentDx11 SHARED
    dllmain.cpp
    Dx11PresentHook.cpp
    ../HookCommon/SharedFrameWriter.cpp
    ../HookCommon/SharedHookConfig.cpp
    ../HookCommon/SharedOverlayCommands.cpp
    ../HookCommon/SharedOverlayV2.cpp
    ../HookCommon/SharedHookStatus.cpp
)

target_include_directories(HookAgentDx11 PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/../HookCommon
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/MinHook/include
)

target_compile_definitions(HookAgentDx11 PRIVATE _UNICODE UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(HookAgentDx11 PRIVATE d3d11 dxgi minhook)

set_target_properties(HookAgentDx11 PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${CMAKE_SOURCE_DIR}/HookHost/bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/HookHost/bin"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG   "${CMAKE_SOURCE_DIR}/HookHost/bin"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/HookHost/bin"
)

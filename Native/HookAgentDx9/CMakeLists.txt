add_library(HookAgentDx9 SHARED
    dllmain.cpp
    Dx9PresentHook.cpp
    ../HookCommon/SharedFrameWriter.cpp
    ../HookCommon/SharedHookConfig.cpp
    ../HookCommon/SharedHookStatus.cpp
)

target_include_directories(HookAgentDx9 PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/../HookCommon
    ${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/MinHook/include
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

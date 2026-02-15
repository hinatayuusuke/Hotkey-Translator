add_library(HookAgentDx11 SHARED
    dllmain.cpp
    Dx11PresentHook.cpp
)

target_include_directories(HookAgentDx11 PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/../HookCommon
)

target_compile_definitions(HookAgentDx11 PRIVATE _UNICODE UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_link_libraries(HookAgentDx11 PRIVATE d3d11 dxgi)

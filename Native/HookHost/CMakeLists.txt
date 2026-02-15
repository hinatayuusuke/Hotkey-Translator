add_executable(HookHost
    main.cpp
    ../HookCommon/SharedHookConfig.cpp
)

target_compile_definitions(HookHost PRIVATE _UNICODE UNICODE)

target_include_directories(HookHost PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../HookCommon
)

set_target_properties(HookHost PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${CMAKE_SOURCE_DIR}/HookHost/bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/HookHost/bin"
)

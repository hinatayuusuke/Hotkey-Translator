add_executable(HookHost
    main.cpp
    ../HookCommon/SharedHookConfig.cpp
)

target_compile_definitions(HookHost PRIVATE _UNICODE UNICODE)

target_include_directories(HookHost PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../HookCommon
)

set(HT_HOOKHOST_BIN_DIR "${CMAKE_SOURCE_DIR}/HookHost/bin")
if (CMAKE_SIZEOF_VOID_P EQUAL 4)
    set(HT_HOOKHOST_BIN_DIR "${CMAKE_SOURCE_DIR}/HookHost/bin/x86")
endif()

set_target_properties(HookHost PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${HT_HOOKHOST_BIN_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${HT_HOOKHOST_BIN_DIR}"
)

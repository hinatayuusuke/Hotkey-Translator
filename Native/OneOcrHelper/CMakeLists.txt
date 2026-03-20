add_executable(OneOcrHelper
    main.cpp
)

target_compile_definitions(OneOcrHelper PRIVATE _UNICODE UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)

target_link_libraries(OneOcrHelper PRIVATE
    crypt32
    ole32
    windowscodecs
)

set(HT_ONEOCRHELPER_BIN_DIR "${CMAKE_SOURCE_DIR}/OneOcrHelper/bin")

set_target_properties(OneOcrHelper PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   "${HT_ONEOCRHELPER_BIN_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${HT_ONEOCRHELPER_BIN_DIR}"
)

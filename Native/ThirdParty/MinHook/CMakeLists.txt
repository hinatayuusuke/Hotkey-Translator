add_library(minhook STATIC
    src/buffer.c
    src/hook.c
    src/trampoline.c
    src/hde/hde32.c
    src/hde/hde64.c
)

# WHY: Toolchains in this repo may not have a standalone C compiler registered with CMake.
# Compile MinHook as C++ to keep the Native build self-contained (MSVC 'cl' is used for CXX).
set_source_files_properties(
    src/buffer.c
    src/hook.c
    src/trampoline.c
    src/hde/hde32.c
    src/hde/hde64.c
    PROPERTIES LANGUAGE CXX
)

target_include_directories(minhook PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_include_directories(minhook PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_compile_definitions(minhook PRIVATE _UNICODE UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)

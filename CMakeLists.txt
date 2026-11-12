cmake_minimum_required(VERSION 3.25)
project(Bosun LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the tools run from a plain install folder with no redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_options(/W4 /permissive- /utf-8)
# Control-flow guard needs both halves: the compiler emits the checks and the linker sets the image flag. /CETCOMPAT
# marks each image as safe under hardware shadow stacks. scripts/native-pe-check.py reads both back from the files.
add_compile_options(/guard:cf)
add_link_options(/guard:cf /CETCOMPAT)
add_compile_definitions(UNICODE _UNICODE)

set(BOSUN_LIBS pdh advapi32)

# app.cpp is nearly all of both tools, so it is compiled once and linked into each.
add_library(bosun_app OBJECT src/app.cpp)

add_executable(bosun src/main.cpp $<TARGET_OBJECTS:bosun_app>)
target_link_libraries(bosun PRIVATE ${BOSUN_LIBS})

# Same logger with no console window, for starting at login.
add_executable(bosun-logger WIN32 src/logger_main.cpp $<TARGET_OBJECTS:bosun_app>)
target_link_libraries(bosun-logger PRIVATE ${BOSUN_LIBS})

# bin/ holds only the shipped tools. Test binaries, PDBs and everything else stay in build/.
# Release only, so a Debug build can never replace what scripts/install.ps1 installs.
foreach(tool bosun bosun-logger)
  add_custom_command(TARGET ${tool} POST_BUILD
    COMMAND "$<$<CONFIG:Release>:${CMAKE_COMMAND};-E;copy_if_different;$<TARGET_FILE:${tool}>;${CMAKE_SOURCE_DIR}/bin/$<TARGET_FILE_NAME:${tool}>>"
    COMMAND_EXPAND_LISTS
    VERBATIM)
endforeach()

enable_testing()
add_executable(test_core tests/test_core.cpp)
add_test(NAME core COMMAND test_core)
add_executable(test_sleeper tests/test_sleeper.cpp)

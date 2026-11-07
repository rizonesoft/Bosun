cmake_minimum_required(VERSION 3.25)
project(DevMode LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the tools run from a plain install folder with no redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_options(/W4 /permissive- /utf-8)
add_compile_definitions(UNICODE _UNICODE)

set(DEVMODE_LIBS pdh advapi32)

add_executable(devmode src/main.cpp src/app.cpp)
target_link_libraries(devmode PRIVATE ${DEVMODE_LIBS})

# Same logger with no console window, for starting at login.
add_executable(devmode-logger WIN32 src/logger_main.cpp src/app.cpp)
target_link_libraries(devmode-logger PRIVATE ${DEVMODE_LIBS})

enable_testing()
add_executable(test_core tests/test_core.cpp)
add_test(NAME core COMMAND test_core)
add_executable(test_sleeper tests/test_sleeper.cpp)

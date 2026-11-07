// Windowless entry point for starting the logger at login.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "app.hpp"

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    return bosun_main({L"log"});
}

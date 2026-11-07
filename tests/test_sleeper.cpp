// Test helper: appends start and end lines with a millisecond clock, sleeps between them, then exits with a chosen code.
// usage: test_sleeper <file> <sleep_ms> [exit_code]
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <cstdio>
#include <cstdlib>

static void mark(const char* path, const char* what) {
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER u{ft.dwLowDateTime, ft.dwHighDateTime};
    for (int i = 0; i < 200; ++i) {
        FILE* f = _fsopen(path, "a", 0x20 /* _SH_DENYWR */);
        if (f) {
            std::fprintf(f, "%s %llu\n", what, u.QuadPart / 10000);
            std::fclose(f);
            return;
        }
        Sleep(10);
    }
}

int main(int argc, char** argv) {
    if (argc < 3) return 64;
    mark(argv[1], "start");
    Sleep(std::atoi(argv[2]));
    mark(argv[1], "end");
    return argc > 3 ? std::atoi(argv[3]) : 0;
}

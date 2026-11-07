#include "app.hpp"

int wmain(int argc, wchar_t** argv) {
    std::vector<std::wstring> args(argv + 1, argv + argc);
    return bosun_main(args);
}

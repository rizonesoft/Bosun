#pragma once
#include <string>
#include <vector>

// Runs a bosun subcommand. args excludes the program name.
int bosun_main(const std::vector<std::wstring>& args);

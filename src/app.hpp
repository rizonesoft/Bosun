#pragma once
#include <string>
#include <vector>

// Runs a devmode subcommand. args excludes the program name.
int devmode_main(const std::vector<std::wstring>& args);

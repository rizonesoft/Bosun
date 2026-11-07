// Platform-free logic, kept apart from the Windows calls so it can be unit tested.
#pragma once
#include <algorithm>
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <sstream>
#include <string>
#include <vector>

namespace dm {

// Slot 0 always admits, so a lone job can never be blocked by the memory gate.
inline bool slot_admits(int slot, uint64_t avail_mb, uint64_t min_avail_mb) {
    return slot == 0 || avail_mb >= min_avail_mb;
}

// Quotes one argument by the rules CommandLineToArgvW parses.
inline std::wstring quote_arg(const std::wstring& a) {
    if (!a.empty() && a.find_first_of(L" \t\n\v\"") == std::wstring::npos) return a;
    std::wstring out = L"\"";
    size_t backslashes = 0;
    for (wchar_t c : a) {
        if (c == L'\\') {
            ++backslashes;
        } else if (c == L'"') {
            out.append(backslashes * 2 + 1, L'\\');
            out.push_back(L'"');
            backslashes = 0;
        } else {
            out.append(backslashes, L'\\');
            out.push_back(c);
            backslashes = 0;
        }
    }
    out.append(backslashes * 2, L'\\');
    out.push_back(L'"');
    return out;
}

// Quotes one word for cmd.exe. Inside double quotes cmd treats & | < > ( ) ^ as text.
// % still expands there, so each one becomes %%cd:~,% : a literal % followed by a
// substring expansion that yields nothing, which leaves no intact %NAME% to expand.
inline std::wstring batch_quote(const std::wstring& s) {
    std::wstring out = L"\"";
    size_t backslashes = 0;
    for (wchar_t c : s) {
        if (c == L'\\') {
            ++backslashes;
            out.push_back(c);
            continue;
        }
        if (c == L'"') {
            out.append(backslashes, L'\\');
            out += L"\"\"";
        } else if (c == L'%') {
            out += L"%%cd:~,%";
        } else {
            out.push_back(c);
        }
        backslashes = 0;
    }
    out.append(backslashes, L'\\');  // A batch often forwards %* to a program that reads \" as an escape.
    out.push_back(L'"');
    return out;
}

// Line breaks end a cmd.exe command and no quoting can contain them, so those arguments are refused.
inline bool batch_safe(const std::wstring& s) {
    return std::all_of(s.begin(), s.end(), [](wchar_t c) { return c >= 0x20 || c == L'\t'; });
}

// Builds the command line for a program plus arguments, or nothing if a batch file
// cannot safely receive them. Batch files must go through cmd.exe.
inline std::optional<std::wstring> build_command_line(const std::wstring& program, const std::vector<std::wstring>& args, bool is_batch) {
    std::wstring line;
    if (!is_batch) {
        line = quote_arg(program);
        for (const auto& a : args) line += L" " + quote_arg(a);
        return line;
    }
    if (!batch_safe(program)) return std::nullopt;
    line = L"cmd.exe /e:ON /v:OFF /d /s /c \"" + batch_quote(program);
    for (const auto& a : args) {
        if (!batch_safe(a)) return std::nullopt;
        line += L" " + batch_quote(a);
    }
    return line + L"\"";
}

// Walks up from dir to the nearest folder holding .git and returns that folder's path,
// so two repos that share a folder name stay separate.
inline std::optional<std::string> repo_from_path(std::string dir, const std::function<bool(const std::string&)>& has_git) {
    std::replace(dir.begin(), dir.end(), '\\', '/');
    while (!dir.empty() && dir.back() == '/') dir.pop_back();
    while (!dir.empty()) {
        if (has_git(dir)) return dir;
        size_t p = dir.rfind('/');
        if (p == std::string::npos) break;
        dir.resize(p);
    }
    return std::nullopt;
}

// Percent-encodes the characters that would break a CSV row, so distinct names stay distinct.
inline std::string csv_name(const std::string& s) {
    std::string out;
    for (char c : s) {
        switch (c) {
            case '%': out += "%25"; break;
            case ',': out += "%2C"; break;
            case '"': out += "%22"; break;
            case '\n': out += "%0A"; break;
            case '\r': out += "%0D"; break;
            default: out.push_back(c);
        }
    }
    return out;
}

struct Row {
    std::string ts, side, kind, name;
    int64_t count = 0, private_mb = 0, rss_mb = 0;
};

inline std::optional<Row> parse_row(const std::string& line) {
    std::vector<std::string> f;
    std::stringstream ss(line);
    std::string item;
    while (std::getline(ss, item, ',')) f.push_back(item);
    if (f.size() != 7 || f[0] == "ts") return std::nullopt;
    try {
        return Row{f[0], f[1], f[2], f[3], std::stoll(f[4]), std::stoll(f[5]), std::stoll(f[6])};
    } catch (...) {
        return std::nullopt;  // A partial line from a sampler killed mid-write.
    }
}

struct Report {
    int64_t win_samples = 0, low_samples = 0;
    std::optional<int64_t> min_avail_mb;
    std::string worst_minute;
    std::map<std::string, int64_t> worst_repos;
    int64_t max_commit_mb = 0, commit_limit_mb = 0, max_pages_in = 0;
    std::optional<int64_t> wsl_min_avail_mb;
    int64_t wsl_max_swap_mb = 0, wsl_max_psi_full_x100 = 0, wsl_max_oom_kills = 0;
    std::map<std::string, int64_t> repo_peak_mb;
    int64_t busiest_total_mb = 0, busiest_repo_count = 0;
    std::string busiest_minute;
};

// low_mb marks a sample as memory pressure. Samples from both sides join on the minute.
inline Report aggregate(const std::vector<Row>& rows, int64_t low_mb = 2048) {
    Report r;
    std::map<std::string, std::map<std::string, int64_t>> minute_repos;
    std::map<std::string, int64_t> minute_avail;
    for (const auto& row : rows) {
        std::string minute = row.ts.substr(0, 16);
        if (row.kind == "repo") {
            std::string key = row.side + ":" + row.name;
            auto& m = minute_repos[minute][key];
            m = std::max(m, row.private_mb);
            auto& peak = r.repo_peak_mb[key];
            peak = std::max(peak, row.private_mb);
        } else if (row.kind == "sys" && row.side == "win") {
            if (row.name == "available_mb") {
                ++r.win_samples;
                if (row.private_mb < low_mb) ++r.low_samples;
                auto it = minute_avail.find(minute);
                if (it == minute_avail.end() || row.private_mb < it->second) minute_avail[minute] = row.private_mb;
            } else if (row.name == "commit_mb") {
                r.max_commit_mb = std::max(r.max_commit_mb, row.private_mb);
            } else if (row.name == "commit_limit_mb") {
                r.commit_limit_mb = std::max(r.commit_limit_mb, row.private_mb);
            } else if (row.name == "pages_in_per_sec") {
                r.max_pages_in = std::max(r.max_pages_in, row.private_mb);
            }
        } else if (row.kind == "sys" && row.side == "wsl") {
            if (row.name == "mem_available_mb") {
                if (!r.wsl_min_avail_mb || row.private_mb < *r.wsl_min_avail_mb) r.wsl_min_avail_mb = row.private_mb;
            } else if (row.name == "swap_used_mb") {
                r.wsl_max_swap_mb = std::max(r.wsl_max_swap_mb, row.private_mb);
            } else if (row.name == "psi_full_avg10_x100") {
                r.wsl_max_psi_full_x100 = std::max(r.wsl_max_psi_full_x100, row.private_mb);
            } else if (row.name == "oom_kills") {
                r.wsl_max_oom_kills = std::max(r.wsl_max_oom_kills, row.private_mb);
            }
        }
    }
    for (const auto& [minute, avail] : minute_avail) {
        if (!r.min_avail_mb || avail < *r.min_avail_mb) {
            r.min_avail_mb = avail;
            r.worst_minute = minute;
            auto it = minute_repos.find(minute);
            r.worst_repos = it == minute_repos.end() ? std::map<std::string, int64_t>{} : it->second;
        }
    }
    for (const auto& [minute, repos] : minute_repos) {
        int64_t total = 0;
        for (const auto& [name, mb] : repos) total += mb;
        if (total > r.busiest_total_mb) {
            r.busiest_total_mb = total;
            r.busiest_repo_count = static_cast<int64_t>(repos.size());
            r.busiest_minute = minute;
        }
    }
    return r;
}

inline std::string render(const Report& r) {
    std::ostringstream o;
    if (!r.min_avail_mb) return "No samples yet. Is the logger running? Check with: devmode status\n";
    double pct = r.win_samples ? 100.0 * static_cast<double>(r.low_samples) / static_cast<double>(r.win_samples) : 0;
    o << "Times are UTC.\nWindows\n";
    o << "  samples: " << r.win_samples << ", under 2 GB available: " << r.low_samples << " (" << static_cast<int>(pct + 0.5) << "%)\n";
    o << "  lowest available: " << *r.min_avail_mb << " MB at " << r.worst_minute << "\n";
    o << "  peak commit: " << r.max_commit_mb << " of " << r.commit_limit_mb << " MB\n";
    o << "  peak page-ins: " << r.max_pages_in << " per second\n";
    o << "WSL\n";
    if (r.wsl_min_avail_mb) {
        o << "  lowest available: " << *r.wsl_min_avail_mb << " MB, peak swap: " << r.wsl_max_swap_mb << " MB\n";
        o << "  peak stall (PSI full avg10): " << r.wsl_max_psi_full_x100 / 100 << "%, OOM kills counter: " << r.wsl_max_oom_kills << "\n";
    } else {
        o << "  no samples\n";
    }
    o << "Busiest minute: " << r.busiest_total_mb << " MB across " << r.busiest_repo_count << " repos at " << r.busiest_minute << "\n";
    o << "Repos at the lowest-memory minute:\n";
    std::vector<std::pair<std::string, int64_t>> worst(r.worst_repos.begin(), r.worst_repos.end());
    std::sort(worst.begin(), worst.end(), [](const auto& a, const auto& b) { return a.second > b.second; });
    for (const auto& [name, mb] : worst) o << "  " << mb << " MB  " << name << "\n";
    o << "Peak per repo:\n";
    std::vector<std::pair<std::string, int64_t>> peaks(r.repo_peak_mb.begin(), r.repo_peak_mb.end());
    std::sort(peaks.begin(), peaks.end(), [](const auto& a, const auto& b) { return a.second > b.second; });
    for (const auto& [name, mb] : peaks) o << "  " << mb << " MB  " << name << "\n";
    return o.str();
}

}  // namespace dm

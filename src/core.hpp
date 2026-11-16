// Platform-free logic, kept apart from the Windows calls so it can be unit tested.
#pragma once
#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <sstream>
#include <string>
#include <vector>

namespace dm {

// Slot 0 always admits, so a lone job can never be blocked by the memory gate.
inline bool slot_admits(int slot, uint64_t avail_mb, uint64_t min_avail_mb) {
    return slot == 0 || avail_mb >= min_avail_mb;
}

// A flag's or variable's whole number: ASCII digits only (no sign, space, prefix or trailing text) within [lo, hi], or
// nothing. std::stoull took "12x" as 12 and "-1" as the largest value, so a typo became a limit nobody asked for.
inline std::optional<uint64_t> parse_count(const std::string& s, uint64_t lo, uint64_t hi) {
    if (s.empty() || s.size() > 20) return std::nullopt;
    uint64_t v = 0;
    for (char c : s) {
        if (c < '0' || c > '9') return std::nullopt;
        uint64_t d = static_cast<uint64_t>(c - '0');
        if (v > (UINT64_MAX - d) / 10) return std::nullopt;
        v = v * 10 + d;
    }
    if (v < lo || v > hi) return std::nullopt;
    return v;
}

// The bound on MB, second and GB values: far past any machine, short of any overflow when they are multiplied out.
constexpr uint64_t kMaxQuantity = uint64_t{1} << 40;

// A job and everything it started may write at most budget_gb in all; 0 means no budget. A command that writes without
// end (a test whose fake child floods a log, a loop that never stops) would otherwise fill the drive, and a full system
// drive takes Windows and the WSL disk image on it down with it.
// The largest budget whose byte count fits 64 bits; a flag above it is refused (D01 T01 §3, round 1).
constexpr uint64_t kMaxWriteGb = UINT64_MAX >> 30;

inline bool over_write_budget(uint64_t written_bytes, uint64_t budget_gb) {
    if (budget_gb == 0 || budget_gb > kMaxWriteGb) return false;  // off, or past anything 64 bits can count
    return written_bytes > (budget_gb << 30);
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

// YYYY-MM-DDTHH:MM:SSZ as seconds since 1970, or nothing for any other text.
inline std::optional<int64_t> utc_seconds(const std::string& t) {
    if (t.size() != 20 || t[4] != '-' || t[7] != '-' || t[10] != 'T' || t[13] != ':' || t[16] != ':' || t[19] != 'Z') return std::nullopt;
    auto num = [&](size_t at, size_t len, int& out) {
        out = 0;
        for (size_t k = at; k < at + len; ++k) {
            if (t[k] < '0' || t[k] > '9') return false;
            out = out * 10 + (t[k] - '0');
        }
        return true;
    };
    int y, mo, d, h, mi, s;
    if (!num(0, 4, y) || !num(5, 2, mo) || !num(8, 2, d) || !num(11, 2, h) || !num(14, 2, mi) || !num(17, 2, s)) return std::nullopt;
    if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 60) return std::nullopt;
    // Days from the civil date (Howard Hinnant's algorithm).
    int yy = y - (mo <= 2);
    int era = (yy >= 0 ? yy : yy - 399) / 400;
    int yoe = yy - era * 400;
    int doy = (153 * (mo + (mo > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    int64_t days = int64_t{era} * 146097 + doe - 719468;
    return days * 86400 + h * 3600 + mi * 60 + s;
}

// The inverse of csv_name (and of wsl/bosun_log.py's csv_name): each %XX back to its byte, anything else as it is, so
// a name read from history matches the same name in a live snapshot (D01 T01 §4).
inline std::string csv_unname(const std::string& s) {
    auto hex = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return -1;
    };
    std::string out;
    for (size_t i = 0; i < s.size(); ++i) {
        int hi = 0, lo = 0;
        if (s[i] == '%' && i + 2 < s.size() && (hi = hex(s[i + 1])) >= 0 && (lo = hex(s[i + 2])) >= 0) {
            out.push_back(static_cast<char>(hi * 16 + lo));
            i += 2;
        } else {
            out.push_back(s[i]);
        }
    }
    return out;
}

// Whether `s` is well-formed UTF-8 (no overlong forms, surrogates or values past U+10FFFF), so text read from a file
// can go into JSON a strict reader accepts (D01 T01 §4, round 1: a hand-made "%FF" name had decoded to a raw byte).
inline bool valid_utf8(const std::string& s) {
    size_t i = 0;
    while (i < s.size()) {
        unsigned char c = static_cast<unsigned char>(s[i]);
        size_t n = c < 0x80 ? 0 : (c >> 5) == 0x6 ? 1 : (c >> 4) == 0xE ? 2 : (c >> 3) == 0x1E ? 3 : 9;
        if (n == 9 || (n > 0 && i + n >= s.size())) return false;  // a bad lead byte, or a sequence cut short
        uint32_t cp = n == 0 ? c : n == 1 ? (c & 0x1F) : n == 2 ? (c & 0x0F) : (c & 0x07);
        for (size_t k = 1; k <= n; ++k) {
            unsigned char cc = static_cast<unsigned char>(s[i + k]);
            if ((cc >> 6) != 0x2) return false;
            cp = (cp << 6) | (cc & 0x3F);
        }
        if ((n == 1 && cp < 0x80) || (n == 2 && cp < 0x800) || (n == 3 && cp < 0x10000) || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return false;
        i += n + 1;
    }
    return true;
}

struct Row {
    std::string ts, side, kind, name;
    int64_t count = 0, private_mb = 0, rss_mb = 0;
};

// A whole decimal integer, sign allowed, with nothing else in the field; std::stoll took "12x" as 12.
inline std::optional<int64_t> csv_integer(const std::string& f) {
    if (f.empty() || f.size() > 20) return std::nullopt;
    size_t i = f[0] == '-' ? 1 : 0;
    if (i == f.size()) return std::nullopt;
    int64_t v = 0;
    for (; i < f.size(); ++i) {
        if (f[i] < '0' || f[i] > '9') return std::nullopt;
        if (v > (INT64_MAX - (f[i] - '0')) / 10) return std::nullopt;
        v = v * 10 + (f[i] - '0');
    }
    return f[0] == '-' ? -v : v;
}

inline std::optional<Row> parse_row(const std::string& line) {
    std::vector<std::string> f;
    std::stringstream ss(line);
    std::string item;
    while (std::getline(ss, item, ',')) f.push_back(item);
    if (f.size() != 7 || f[0] == "ts") return std::nullopt;
    auto count = csv_integer(f[4]), priv = csv_integer(f[5]), rss = csv_integer(f[6]);
    if (!count || !priv || !rss) return std::nullopt;  // a partial line from a sampler killed mid-write
    // A real time (round 3: a 19-byte stamp with a stray byte had gone into the JSON), and quantities no sampler can
    // write past: never negative, at most 2^40 MB, so no sum of them can overflow.
    if (!utc_seconds(f[0] + "Z")) return std::nullopt;
    for (int64_t v : {*count, *priv, *rss})
        if (v < 0 || v > static_cast<int64_t>(kMaxQuantity)) return std::nullopt;
    std::string name = csv_unname(f[3]);
    if (!valid_utf8(name) || !valid_utf8(f[1]) || !valid_utf8(f[2])) return std::nullopt;  // no writer makes it: refused and counted
    return Row{f[0], f[1], f[2], std::move(name), *count, *priv, *rss};
}

// One UTC minute of history: each side's lowest available memory that minute (null without a sample), and how many
// repos had processes with their summed private memory.
struct MinutePoint {
    std::optional<int64_t> win_available_mb, wsl_available_mb;
    int64_t repos = 0, repo_private_mb = 0;
};

struct RepoPeak {
    int64_t private_mb = 0, rss_mb = 0, processes = 0;
};

struct Report {
    int64_t win_samples = 0, low_samples = 0, wsl_samples = 0;
    std::optional<int64_t> min_avail_mb;
    std::string worst_minute;
    std::map<std::string, int64_t> worst_repos;  // "side:repo" at the lowest-memory minute
    // Null when no sample of it was taken (D01 T01 §4): a maximum of nothing is not 0.
    std::optional<int64_t> max_commit_mb, commit_limit_mb, max_pages_in;
    std::optional<int64_t> wsl_min_avail_mb;
    std::optional<int64_t> wsl_max_swap_mb, wsl_max_psi_full_x100, wsl_max_oom_kills;
    std::map<std::string, int64_t> repo_peak_mb;                      // "side:repo"
    std::map<std::pair<std::string, std::string>, RepoPeak> repo_peaks;  // (side, repo)
    int64_t busiest_total_mb = 0, busiest_repo_count = 0;  // the minute with the most private memory in repos
    std::string busiest_minute;
    // The minute with the most repos active at once (ties to the one with more memory, then the earlier): not the
    // busiest minute, whose count is only that minute's (round 2 of D01 T01 §4).
    int64_t active_max = 0, active_total_mb = 0;
    std::string active_minute;
    std::map<std::string, MinutePoint> series;  // by minute, YYYY-MM-DDTHH:MM
    std::set<std::string> dates;               // UTC dates with a sample
    std::string first_sample, last_sample;
};

inline void keep_max(std::optional<int64_t>& m, int64_t v) {
    if (!m || v > *m) m = v;
}

// low_mb marks a sample as memory pressure. Samples from both sides join on the minute. Only samples at or after
// `since` (YYYY-MM-DDTHH:MM:SS, UTC, as the rows write it) count, for every figure alike; empty takes them all.
inline Report aggregate(const std::vector<Row>& rows, int64_t low_mb = 2048, const std::string& since = {}) {
    Report r;
    std::map<std::string, std::map<std::string, int64_t>> minute_repos;
    std::map<std::string, int64_t> minute_avail;
    for (const auto& row : rows) {
        if (!since.empty() && row.ts < since) continue;
        std::string minute = row.ts.substr(0, 16);
        r.dates.insert(row.ts.substr(0, 10));
        if (r.first_sample.empty() || row.ts < r.first_sample) r.first_sample = row.ts;
        if (row.ts > r.last_sample) r.last_sample = row.ts;
        if (row.kind == "repo") {
            std::string key = row.side + ":" + row.name;
            auto& m = minute_repos[minute][key];
            m = std::max(m, row.private_mb);
            auto& peak = r.repo_peak_mb[key];
            peak = std::max(peak, row.private_mb);
            auto& p = r.repo_peaks[{row.side, row.name}];
            p.private_mb = std::max(p.private_mb, row.private_mb);
            p.rss_mb = std::max(p.rss_mb, row.rss_mb);
            p.processes = std::max(p.processes, row.count);
        } else if (row.kind == "sys" && row.side == "win") {
            if (row.name == "available_mb") {
                ++r.win_samples;
                if (row.private_mb < low_mb) ++r.low_samples;
                auto it = minute_avail.find(minute);
                if (it == minute_avail.end() || row.private_mb < it->second) minute_avail[minute] = row.private_mb;
                auto& point = r.series[minute].win_available_mb;
                if (!point || row.private_mb < *point) point = row.private_mb;
            } else if (row.name == "commit_mb") {
                keep_max(r.max_commit_mb, row.private_mb);
            } else if (row.name == "commit_limit_mb") {
                keep_max(r.commit_limit_mb, row.private_mb);
            } else if (row.name == "pages_in_per_sec") {
                keep_max(r.max_pages_in, row.private_mb);
            }
        } else if (row.kind == "sys" && row.side == "wsl") {
            if (row.name == "mem_available_mb") {
                ++r.wsl_samples;
                if (!r.wsl_min_avail_mb || row.private_mb < *r.wsl_min_avail_mb) r.wsl_min_avail_mb = row.private_mb;
                auto& point = r.series[minute].wsl_available_mb;
                if (!point || row.private_mb < *point) point = row.private_mb;
            } else if (row.name == "swap_used_mb") {
                keep_max(r.wsl_max_swap_mb, row.private_mb);
            } else if (row.name == "psi_full_avg10_x100") {
                keep_max(r.wsl_max_psi_full_x100, row.private_mb);
            } else if (row.name == "oom_kills") {
                keep_max(r.wsl_max_oom_kills, row.private_mb);
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
        for (const auto& [name, mb] : repos) total = std::min<int64_t>(total + mb, INT64_MAX / 2);  // saturates, never wraps
        auto& point = r.series[minute];
        point.repos = static_cast<int64_t>(repos.size());
        point.repo_private_mb = total;
        int64_t count = static_cast<int64_t>(repos.size());
        if (count > r.active_max || (count == r.active_max && total > r.active_total_mb)) {
            r.active_max = count;
            r.active_total_mb = total;
            r.active_minute = minute;
        }
        if (total > r.busiest_total_mb) {
            r.busiest_total_mb = total;
            r.busiest_repo_count = static_cast<int64_t>(repos.size());
            r.busiest_minute = minute;
        }
    }
    return r;
}

// Seconds since 1970 as YYYY-MM-DDTHH:MM:SSZ (the inverse of utc_seconds; Howard Hinnant's civil_from_days).
inline std::string utc_stamp(int64_t t) {
    int64_t days = t >= 0 ? t / 86400 : (t - 86399) / 86400;
    int64_t secs = t - days * 86400;
    int64_t z = days + 719468;
    int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    int64_t doe = z - era * 146097;
    int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    int64_t y = yoe + era * 400;
    int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    int64_t mp = (5 * doy + 2) / 153;
    int64_t d = doy - (153 * mp + 2) / 5 + 1;
    int64_t m = mp < 10 ? mp + 3 : mp - 9;
    if (m <= 2) ++y;
    char buf[32];
    std::snprintf(buf, sizeof(buf), "%04lld-%02lld-%02lldT%02lld:%02lld:%02lldZ", static_cast<long long>(y), static_cast<long long>(m),
                  static_cast<long long>(d), static_cast<long long>(secs / 3600), static_cast<long long>(secs / 60 % 60),
                  static_cast<long long>(secs % 60));
    return buf;
}

inline std::string mb_or(const std::optional<int64_t>& v, const char* none = "not measured") {
    return v ? std::to_string(*v) : std::string(none);
}

inline std::string render(const Report& r) {
    std::ostringstream o;
    if (!r.min_avail_mb) return "No samples yet. Is the logger running? Check with: bosun status\n";
    double pct = r.win_samples ? 100.0 * static_cast<double>(r.low_samples) / static_cast<double>(r.win_samples) : 0;
    o << "Times are UTC.\nWindows\n";
    o << "  samples: " << r.win_samples << ", under 2 GB available: " << r.low_samples << " (" << static_cast<int>(pct + 0.5) << "%)\n";
    o << "  lowest available: " << *r.min_avail_mb << " MB at " << r.worst_minute << "\n";
    o << "  peak commit: " << mb_or(r.max_commit_mb) << " of " << mb_or(r.commit_limit_mb) << " MB\n";
    o << "  peak page-ins: " << mb_or(r.max_pages_in) << " per second\n";
    o << "WSL\n";
    if (r.wsl_min_avail_mb) {
        o << "  lowest available: " << *r.wsl_min_avail_mb << " MB, peak swap: " << mb_or(r.wsl_max_swap_mb) << " MB\n";
        o << "  peak stall (PSI full avg10): "
          << (r.wsl_max_psi_full_x100 ? std::to_string(*r.wsl_max_psi_full_x100 / 100) + "%" : std::string("not measured"))
          << ", OOM kills counter: " << mb_or(r.wsl_max_oom_kills) << "\n";
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

// What report could read: a folder or file it couldn't open makes it incomplete, and rows it refused are counted.
struct ReadStatus {
    bool complete = true;
    int64_t files = 0, unreadable_files = 0, rejected_rows = 0;
};

// --- Slot holder and waiter records (D01 T01 §1) ------------------------------------------------------------------
// A mutex can't name its owner, so each holder and waiter publishes a small JSON record beside it. The mutex stays the
// only authority: a record only says who, and one whose process is gone is ignored.

// JSON string contents for UTF-8 text: quotes and backslashes escaped, control characters as \u00XX. The one escaper
// every JSON the tools write goes through (status --json reuses it).
inline std::string json_escape(const std::string& s) {
    static const char hex[] = "0123456789abcdef";
    std::string out;
    out.reserve(s.size() + 8);
    for (unsigned char c : s) {
        if (c == '"') out += "\\\"";
        else if (c == '\\') out += "\\\\";
        else if (c < 0x20) {
            out += "\\u00";
            out.push_back(hex[c >> 4]);
            out.push_back(hex[c & 0xF]);
        } else out.push_back(static_cast<char>(c));
    }
    return out;
}

struct SlotRecord {
    std::string kind;                   // "holder" or "waiter"
    int64_t pid = 0;
    uint64_t process_start = 0;         // the process's creation time, a FILETIME as GetProcessTimes gives it
    std::string started_at;             // UTC, YYYY-MM-DDTHH:MM:SSZ
    std::string command;                // the command line as run
    std::optional<std::string> repo;    // null outside a Git checkout
    std::string side;                   // "windows" or "wsl"
    std::optional<int> slot;            // holders only
    uint32_t session = 0;
    std::string ns;
    // Added by D01 T01 §3, for the Queue page's waited, running and why. Optional, so a record an older bosun.exe wrote
    // still reads, with them null.
    std::optional<std::string> acquired_at;         // holders: UTC when the slot was taken
    std::optional<std::string> command_started_at;  // holders: UTC when the command started, null until it has
    std::optional<uint64_t> min_guest_mb;           // WSL holders past slot 0: the guest memory floor the shim waits on
    std::optional<std::string> waiting_for;         // waiters: "slot" (every slot held) or "memory" (a slot's floor unmet)
    std::optional<uint64_t> min_avail_mb, max_wait_s;  // waiters: the limits the job waits under
    // Added by D03 T02 §1: a WSL job's own Linux process (the shim's), so the instance scanners can join a holder to the
    // job tree WSL sees. Optional, so a record without it still reads; written only when known.
    std::optional<int64_t> guest_pid;
    // And its distro (round 1 of D03 T02 §1): a Linux pid is unique only within one distro, so a join needs both.
    std::optional<std::string> guest_distro;
};

// A WSL distro's name as wsl.exe takes it: letters, digits, '.', '_' and '-', at most 64, so it can name a file too.
inline bool distro_name_ok(const std::string& name) {
    if (name.empty() || name.size() > 64) return false;
    for (unsigned char c : name)
        if (!std::isalnum(c) && c != '.' && c != '_' && c != '-') return false;
    return true;
}


// A record's fields, without the braces, so status --json can extend the object it embeds.
inline std::string record_fields(const SlotRecord& r) {
    std::ostringstream o;
    auto text = [&](const std::optional<std::string>& v) {
        if (v) o << "\"" << json_escape(*v) << "\"";
        else o << "null";
    };
    auto count = [&](const std::optional<uint64_t>& v) {
        if (v) o << *v;
        else o << "null";
    };
    o << "\"schema_version\":1,\"kind\":\"" << json_escape(r.kind) << "\",\"pid\":" << r.pid << ",\"process_start\":" << r.process_start
      << ",\"started_at\":\"" << json_escape(r.started_at) << "\",\"command\":\"" << json_escape(r.command) << "\",\"repo\":";
    text(r.repo);
    o << ",\"side\":\"" << json_escape(r.side) << "\"";
    if (r.slot) o << ",\"slot\":" << *r.slot;
    o << ",\"session\":" << r.session << ",\"ns\":\"" << json_escape(r.ns) << "\"";
    if (r.kind == "holder") {
        o << ",\"acquired_at\":";
        text(r.acquired_at);
        o << ",\"command_started_at\":";
        text(r.command_started_at);
        o << ",\"min_guest_mb\":";
        count(r.min_guest_mb);
        if (r.guest_pid) o << ",\"guest_pid\":" << *r.guest_pid;  // D03 T02 §1: absent, never null, while unknown
        if (r.guest_distro) o << ",\"guest_distro\":\"" << json_escape(*r.guest_distro) << "\"";
    } else {
        o << ",\"waiting_for\":";
        text(r.waiting_for);
        o << ",\"min_avail_mb\":";
        count(r.min_avail_mb);
        o << ",\"max_wait_s\":";
        count(r.max_wait_s);
    }
    return o.str();
}

inline std::string record_json(const SlotRecord& r) { return "{" + record_fields(r) + "}"; }

// A flat JSON object whose values are strings, integers or null, as the records are; anything else is refused.
struct FlatValue {
    enum Kind { String, Integer, Decimal, Bool, Null, Nested } kind = Null;  // Nested: an object or list, skipped (`allow_nested`)
    std::string text;  // a String's contents; a number's own text, exactly as read (round 2 of D01 T01 §5)
    int64_t number = 0;
    bool negative = false;
    uint64_t magnitude = 0;
    double decimal = 0;  // a Decimal's value, and an Integer's too
    bool flag = false;
    bool overflow = false;  // an integer past 64 bits, kept only when the caller asked (D01 T01 §5, round 1)
};

// `allow_overflow` keeps an integer too large for 64 bits as an Integer marked `overflow`, so a caller that refuses
// one key (config.json) can do that; every other caller refuses the whole object, as it always has. `allow_nested`
// reads a value that is an object or a list as Nested, its text checked for balance and skipped, so a document another
// tool writes (Claude Code's session records, D03 T02 §1) can be read for its flat fields; every other caller refuses it.
inline std::optional<std::map<std::string, FlatValue>> parse_flat_object(const std::string& s, bool allow_overflow = false, bool allow_nested = false) {
    size_t i = 0;
    auto ws = [&] { while (i < s.size() && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) ++i; };
    auto hex4 = [&](uint32_t& v) -> bool {
        if (i + 4 > s.size()) return false;
        v = 0;
        for (int k = 0; k < 4; ++k) {
            char c = s[i++];
            v <<= 4;
            if (c >= '0' && c <= '9') v |= static_cast<uint32_t>(c - '0');
            else if (c >= 'a' && c <= 'f') v |= static_cast<uint32_t>(c - 'a' + 10);
            else if (c >= 'A' && c <= 'F') v |= static_cast<uint32_t>(c - 'A' + 10);
            else return false;
        }
        return true;
    };
    auto put_utf8 = [](std::string& out, uint32_t cp) {
        if (cp < 0x80) out.push_back(static_cast<char>(cp));
        else if (cp < 0x800) { out.push_back(static_cast<char>(0xC0 | (cp >> 6))); out.push_back(static_cast<char>(0x80 | (cp & 0x3F))); }
        else if (cp < 0x10000) { out.push_back(static_cast<char>(0xE0 | (cp >> 12))); out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F))); out.push_back(static_cast<char>(0x80 | (cp & 0x3F))); }
        else { out.push_back(static_cast<char>(0xF0 | (cp >> 18))); out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F))); out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F))); out.push_back(static_cast<char>(0x80 | (cp & 0x3F))); }
    };
    auto str = [&](std::string& out) -> bool {
        if (i >= s.size() || s[i] != '"') return false;
        ++i;
        while (i < s.size()) {
            char c = s[i++];
            if (c == '"') return true;
            if (static_cast<unsigned char>(c) < 0x20) return false;
            if (c != '\\') { out.push_back(c); continue; }
            if (i >= s.size()) return false;
            char e = s[i++];
            switch (e) {
                case '"': out.push_back('"'); break;
                case '\\': out.push_back('\\'); break;
                case '/': out.push_back('/'); break;
                case 'b': out.push_back('\b'); break;
                case 'f': out.push_back('\f'); break;
                case 'n': out.push_back('\n'); break;
                case 'r': out.push_back('\r'); break;
                case 't': out.push_back('\t'); break;
                case 'u': {
                    uint32_t cp = 0;
                    if (!hex4(cp)) return false;
                    if (cp >= 0xD800 && cp <= 0xDBFF) {
                        uint32_t low = 0;
                        if (i + 2 > s.size() || s[i] != '\\' || s[i + 1] != 'u') return false;
                        i += 2;
                        if (!hex4(low) || low < 0xDC00 || low > 0xDFFF) return false;
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    } else if (cp >= 0xDC00 && cp <= 0xDFFF) return false;
                    put_utf8(out, cp);
                    break;
                }
                default: return false;
            }
        }
        return false;
    };
    std::map<std::string, FlatValue> out;
    ws();
    if (i >= s.size() || s[i] != '{') return std::nullopt;
    ++i;
    ws();
    if (i < s.size() && s[i] == '}') { ++i; ws(); return i == s.size() ? std::optional(out) : std::nullopt; }
    for (;;) {
        ws();
        std::string key;
        if (!str(key)) return std::nullopt;
        ws();
        if (i >= s.size() || s[i] != ':') return std::nullopt;
        ++i;
        ws();
        FlatValue v;
        size_t number_from = i;  // where a number's own text starts
        if (i < s.size() && s[i] == '"') {
            v.kind = FlatValue::String;
            if (!str(v.text)) return std::nullopt;
        } else if (i < s.size() && (s[i] == '{' || s[i] == '[')) {
            if (!allow_nested) return std::nullopt;
            // Skipped by balance: brackets inside strings don't count, and the depth is bounded so no input recurses.
            std::vector<char> open;
            while (i < s.size()) {
                char c = s[i];
                if (c == '"') {
                    std::string ignored;
                    if (!str(ignored)) return std::nullopt;
                    continue;
                }
                ++i;
                if (c == '{' || c == '[') {
                    if (open.size() >= 256) return std::nullopt;
                    open.push_back(c == '{' ? '}' : ']');
                } else if (c == '}' || c == ']') {
                    if (open.empty() || open.back() != c) return std::nullopt;
                    open.pop_back();
                    if (open.empty()) break;
                }
            }
            if (!open.empty()) return std::nullopt;
            v.kind = FlatValue::Nested;
            v.text = s.substr(number_from, i - number_from);  // its own text, so a writer can put it back unchanged
        } else if (s.compare(i, 4, "null") == 0) {
            v.kind = FlatValue::Null;
            i += 4;
        } else if (s.compare(i, 4, "true") == 0 || s.compare(i, 5, "false") == 0) {
            v.kind = FlatValue::Bool;
            v.flag = s[i] == 't';
            i += v.flag ? 4 : 5;
        } else {
            v.kind = FlatValue::Integer;
            if (i < s.size() && s[i] == '-') { v.negative = true; ++i; }
            // JSON allows no leading zero: "01" is no number any writer makes (completion review of D01 T01 §5).
            if (i + 1 < s.size() && s[i] == '0' && s[i + 1] >= '0' && s[i + 1] <= '9') return std::nullopt;
            size_t digits = 0;
            while (i < s.size() && s[i] >= '0' && s[i] <= '9') {
                uint64_t d = static_cast<uint64_t>(s[i] - '0');
                if (v.overflow || v.magnitude > (UINT64_MAX - d) / 10) {
                    if (!allow_overflow) return std::nullopt;
                    v.overflow = true;
                    v.magnitude = UINT64_MAX;
                } else {
                    v.magnitude = v.magnitude * 10 + d;
                }
                ++i;
                ++digits;
            }
            if (!digits) return std::nullopt;
            if (v.negative && v.magnitude > uint64_t{1} << 63) {
                if (!allow_overflow) return std::nullopt;
                v.overflow = true;
            }
            v.number = v.negative ? static_cast<int64_t>(0 - v.magnitude) : static_cast<int64_t>(v.magnitude);
            v.decimal = static_cast<double>(v.number);
            if (i < s.size() && s[i] == '.') {  // a decimal fraction; no exponent is ever written
                ++i;
                double scale = 0.1, fraction = 0;
                size_t places = 0;
                while (i < s.size() && s[i] >= '0' && s[i] <= '9') {
                    fraction += (s[i] - '0') * scale;
                    scale /= 10;
                    ++i;
                    ++places;
                }
                if (!places) return std::nullopt;
                v.kind = FlatValue::Decimal;
                v.decimal = v.negative ? v.decimal - fraction : v.decimal + fraction;
            }
            // An exponent, as JSON allows (round 1: config.json's writer keeps an unknown decimal as %.17g, which
            // can write one). Every digit is read (round 4), and the scale is worked out only up to 400, past any
            // double: beyond that a zero or a negative exponent makes 0, and a positive one a value past range.
            if (i < s.size() && (s[i] == 'e' || s[i] == 'E')) {
                ++i;
                bool down = false;
                if (i < s.size() && (s[i] == '+' || s[i] == '-')) down = s[i++] == '-';
                int e = 0, places = 0;
                while (i < s.size() && s[i] >= '0' && s[i] <= '9') {
                    if (e < 400) e = e * 10 + (s[i] - '0');
                    ++i;
                    ++places;
                }
                if (!places) return std::nullopt;
                if (e > 400) e = 400;
                double scale = 1;
                for (int k = 0; k < e; ++k) scale *= 10;  // 1e400 is inf, so a positive exponent past range stays past it
                if (v.decimal != 0) v.decimal = down ? v.decimal / scale : v.decimal * scale;  // 0e309 is 0, not 0 * inf
                v.kind = FlatValue::Decimal;
                // A value past a double's range (1e309) is no number any reader can use: refused, or, where the caller
                // keeps overflow (config.json's unknown keys), kept by its text alone (round 3 of D01 T01 §5).
                if (!std::isfinite(v.decimal)) {
                    if (!allow_overflow) return std::nullopt;
                    v.overflow = true;
                }
            }
        }
        if (v.kind == FlatValue::Integer || v.kind == FlatValue::Decimal) v.text = s.substr(number_from, i - number_from);
        if (out.count(key)) return std::nullopt;  // a repeated key is no record anyone wrote
        out[key] = v;
        ws();
        if (i < s.size() && s[i] == ',') { ++i; continue; }
        if (i < s.size() && s[i] == '}') { ++i; break; }
        return std::nullopt;
    }
    ws();
    if (i != s.size()) return std::nullopt;
    return out;
}

// The record in `text`, or nothing when it is truncated, isn't one object, has another schema version or lacks a field.
inline std::optional<SlotRecord> parse_record(const std::string& text) {
    auto obj = parse_flat_object(text);
    if (!obj) return std::nullopt;
    auto get = [&](const char* k, FlatValue::Kind kind) -> const FlatValue* {
        auto it = obj->find(k);
        return it != obj->end() && it->second.kind == kind ? &it->second : nullptr;
    };
    const FlatValue* version = get("schema_version", FlatValue::Integer);
    if (!version || version->number != 1) return std::nullopt;
    SlotRecord r;
    const FlatValue *kind = get("kind", FlatValue::String), *pid = get("pid", FlatValue::Integer),
                    *start = get("process_start", FlatValue::Integer), *at = get("started_at", FlatValue::String),
                    *command = get("command", FlatValue::String), *side = get("side", FlatValue::String),
                    *session = get("session", FlatValue::Integer), *ns = get("ns", FlatValue::String);
    if (!kind || !pid || !start || !at || !command || !side || !session || !ns) return std::nullopt;
    if ((kind->text != "holder" && kind->text != "waiter") || (side->text != "windows" && side->text != "wsl")) return std::nullopt;
    if (pid->negative || pid->magnitude == 0 || start->negative || session->negative || session->magnitude > UINT32_MAX) return std::nullopt;
    r.kind = kind->text;
    r.pid = pid->number;
    r.process_start = start->magnitude;
    r.started_at = at->text;
    r.command = command->text;
    r.side = side->text;
    r.session = static_cast<uint32_t>(session->magnitude);
    r.ns = ns->text;
    auto repo = obj->find("repo");
    if (repo == obj->end()) return std::nullopt;
    if (repo->second.kind == FlatValue::String) r.repo = repo->second.text;
    else if (repo->second.kind != FlatValue::Null) return std::nullopt;
    const FlatValue* slot = get("slot", FlatValue::Integer);
    if (r.kind == "holder") {
        if (!slot || slot->negative || slot->magnitude > 4096) return std::nullopt;
        r.slot = static_cast<int>(slot->magnitude);
    } else if (obj->count("slot")) {
        return std::nullopt;
    }
    // The §3 fields: absent or null reads as null, any other wrong shape refuses the record, as for the fields above.
    auto opt_stamp = [&](const char* k, std::optional<std::string>& out) {
        auto it = obj->find(k);
        if (it == obj->end() || it->second.kind == FlatValue::Null) return true;
        if (it->second.kind != FlatValue::String || !utc_seconds(it->second.text)) return false;
        out = it->second.text;
        return true;
    };
    auto opt_count = [&](const char* k, std::optional<uint64_t>& out) {
        auto it = obj->find(k);
        if (it == obj->end() || it->second.kind == FlatValue::Null) return true;
        if (it->second.kind != FlatValue::Integer || it->second.negative || it->second.magnitude > kMaxQuantity) return false;
        out = it->second.magnitude;
        return true;
    };
    auto guest = obj->find("guest_pid");
    if (guest != obj->end() && guest->second.kind != FlatValue::Null) {
        if (guest->second.kind != FlatValue::Integer || guest->second.negative || guest->second.magnitude == 0 || guest->second.magnitude > INT32_MAX)
            return std::nullopt;
        r.guest_pid = guest->second.number;
    }
    auto guest_distro = obj->find("guest_distro");
    if (guest_distro != obj->end() && guest_distro->second.kind != FlatValue::Null) {
        if (guest_distro->second.kind != FlatValue::String || !distro_name_ok(guest_distro->second.text)) return std::nullopt;
        r.guest_distro = guest_distro->second.text;
    }
    if (!opt_stamp("acquired_at", r.acquired_at) || !opt_stamp("command_started_at", r.command_started_at) ||
        !opt_count("min_guest_mb", r.min_guest_mb) || !opt_count("min_avail_mb", r.min_avail_mb) || !opt_count("max_wait_s", r.max_wait_s))
        return std::nullopt;
    auto why = obj->find("waiting_for");
    if (why != obj->end() && why->second.kind != FlatValue::Null) {
        if (why->second.kind != FlatValue::String || (why->second.text != "slot" && why->second.text != "memory")) return std::nullopt;
        r.waiting_for = why->second.text;
    }
    return r;
}

// A queue namespace as a folder name: an injective mapping onto text Windows accepts as one, and injective however the
// file system folds case, since the mutex names are case-sensitive and the folder names are not. Lower-case letters,
// digits, '_' and '-' stay; every other UTF-8 byte becomes %XX in upper-case hex, an upper-case letter included (so
// "ReviewCase" and "reviewcase" stay apart) and '.' too (Windows drops a trailing dot); and the "ns-" prefix keeps an
// empty name and the reserved device names (CON, NUL, COM1...) valid.
inline std::string ns_dir(const std::string& ns) {
    static const char hex[] = "0123456789ABCDEF";
    std::string out = "ns-";
    for (unsigned char c : ns) {
        if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-') out.push_back(static_cast<char>(c));
        else {
            out.push_back('%');
            out.push_back(hex[c >> 4]);
            out.push_back(hex[c & 0xF]);
        }
    }
    return out;
}

inline std::string slot_file(int slot) { return "slot-" + std::to_string(slot) + ".json"; }

// Named by the process's id and start, so cleaning up a dead waiter can never remove a successor's record.
inline std::string waiter_file(int64_t pid, uint64_t process_start) {
    return "wait-" + std::to_string(pid) + "-" + std::to_string(process_start) + ".json";
}

// What a probe of a process says: running (with its start time), gone, or that it can't be told (access denied).
struct ProbeAnswer {
    enum What { Running, Gone, CantTell } what = CantTell;
    uint64_t start = 0;
};

enum class RecordState { Live, Stale, Unknown };

inline RecordState record_state(const SlotRecord& r, const std::function<ProbeAnswer(int64_t)>& probe) {
    ProbeAnswer a = probe(r.pid);
    if (a.what == ProbeAnswer::CantTell) return RecordState::Unknown;  // not knowing is not the same as dead
    if (a.what == ProbeAnswer::Gone || a.start != r.process_start) return RecordState::Stale;
    return RecordState::Live;
}

// Only a waiter's record is ever removed by a reader: a stale one, or one superseded by a holder record of the same
// process (same pid and start), which a job leaves when a reader had the waiter file open as it took its slot. A slot
// record is only ever written or removed by the holder of that slot's mutex, so a reader never touches one: a killed
// holder's is replaced by the next holder.
inline bool reader_removes(const SlotRecord& r, RecordState state, const std::vector<SlotRecord>& all = {}) {
    if (r.kind != "waiter") return false;
    if (state == RecordState::Stale) return true;
    return std::any_of(all.begin(), all.end(), [&](const SlotRecord& o) {
        return o.kind == "holder" && o.pid == r.pid && o.process_start == r.process_start;
    });
}

// --- status --json (D01 T01 §3) ------------------------------------------------------------------------------------
// The panel's main read: each slot and who holds it, who waits and why, memory on both sides and the loggers, as one
// versioned object. Anything that couldn't be measured is null and a records folder that couldn't be read says so,
// so nothing unknown ever reads as zero, free or an empty queue.

// The Windows logger's state from opening its singleton mutex: opened, or refused with access denied (it exists, in
// a security context this caller can't open), is running; not found is not running; any other error can't be told
// (completion review: access denied had read as "not running").
inline const char* logger_state(bool opened, uint32_t error) {
    constexpr uint32_t kFileNotFound = 2, kAccessDenied = 5;  // ERROR_FILE_NOT_FOUND, ERROR_ACCESS_DENIED
    if (opened || error == kAccessDenied) return "running";
    if (error == kFileNotFound) return "not running";
    return "unknown";
}

struct SeenRecord {
    SlotRecord record;
    RecordState state = RecordState::Unknown;
};

struct GuestFigures {
    uint64_t available_mb = 0, total_mb = 0, min_guest_mb = 0;
};

struct StatusInput {
    std::string ns, at;                     // the queue namespace; now, UTC
    std::vector<std::optional<bool>> busy;  // per slot: its mutex held, free, or unknown when it couldn't be probed
    std::vector<SeenRecord> records;        // every record the reader pass parsed, with its state
    bool records_readable = true;
    int records_skipped = 0;                // .json files it couldn't read or parse
    std::string records_dir;
    std::optional<uint64_t> available_mb, total_mb;
    uint64_t min_avail_mb = 0;
    std::optional<GuestFigures> guest;      // the WSL shim's figures; none from a Windows call
    std::string windows_logger = "not running";  // dm::logger_state
    std::optional<std::string> wsl_logger;  // "running", "not running" or "unknown"; none from a Windows call
};

struct StatusView {
    std::vector<std::optional<SeenRecord>> holders;  // per slot
    std::vector<SeenRecord> waiters;                 // oldest first
};

// Slot N's holder is its live or unknown slot-N record, and only while slot N is busy (or can't be told), so a killed
// holder's leftover never names a holder of a free slot. Waiters are the live and unknown waiter records not
// superseded by their own holder record. Stale records are left out.
inline StatusView status_view(const StatusInput& in) {
    StatusView v;
    std::vector<SlotRecord> all;
    for (const auto& s : in.records) all.push_back(s.record);
    v.holders.resize(in.busy.size());
    for (const auto& s : in.records) {
        if (s.state == RecordState::Stale) continue;
        const SlotRecord& r = s.record;
        if (r.kind == "holder") {
            if (!r.slot || *r.slot < 0 || static_cast<size_t>(*r.slot) >= in.busy.size()) continue;
            if (in.busy[*r.slot] == false) continue;
            v.holders[*r.slot] = s;
        } else if (!reader_removes(r, s.state, all)) {
            v.waiters.push_back(s);
        }
    }
    std::sort(v.waiters.begin(), v.waiters.end(), [](const SeenRecord& a, const SeenRecord& b) {
        if (a.record.started_at != b.record.started_at) return a.record.started_at < b.record.started_at;
        return a.record.pid < b.record.pid;
    });
    return v;
}

// Whole seconds from one stamp to another, never below zero; nothing when either is missing or malformed.
inline std::optional<int64_t> seconds_between(const std::optional<std::string>& from, const std::optional<std::string>& to) {
    if (!from || !to) return std::nullopt;
    auto a = utc_seconds(*from), b = utc_seconds(*to);
    if (!a || !b) return std::nullopt;
    return *b > *a ? *b - *a : 0;
}

inline std::string status_json(const StatusInput& in) {
    StatusView v = status_view(in);
    std::ostringstream o;
    auto count = [&](const std::optional<int64_t>& n) {
        if (n) o << *n;
        else o << "null";
    };
    auto state = [](RecordState s) { return s == RecordState::Live ? "live" : "unknown"; };
    o << "{\"schema_version\":1,\"ns\":\"" << json_escape(in.ns) << "\",\"at\":\"" << json_escape(in.at) << "\",\"slots\":[";
    for (size_t i = 0; i < in.busy.size(); ++i) {
        if (i) o << ",";
        o << "{\"index\":" << i << ",\"busy\":" << (!in.busy[i] ? "null" : *in.busy[i] ? "true" : "false") << ",\"holder\":";
        if (!v.holders[i]) {
            o << "null}";
            continue;
        }
        const SlotRecord& r = v.holders[i]->record;
        // A record from before §3 has no acquired_at: how long it waited and what it is doing can't be told.
        const char* phase = nullptr;
        if (r.command_started_at) phase = "running";
        else if (r.acquired_at && r.side == "wsl" && r.min_guest_mb && *r.min_guest_mb > 0) phase = "waiting for guest memory";
        else if (r.acquired_at) phase = "starting";
        o << "{" << record_fields(r) << ",\"state\":\"" << state(v.holders[i]->state) << "\",\"waited_s\":";
        count(seconds_between(r.started_at, r.acquired_at));
        o << ",\"running_s\":";
        count(seconds_between(r.command_started_at, in.at));
        o << ",\"phase\":";
        if (phase) o << "\"" << phase << "\"";
        else o << "null";
        o << "}}";
    }
    o << "],\"waiters\":[";
    for (size_t i = 0; i < v.waiters.size(); ++i) {
        if (i) o << ",";
        const SlotRecord& r = v.waiters[i].record;
        o << "{" << record_fields(r) << ",\"state\":\"" << state(v.waiters[i].state) << "\",\"waited_s\":";
        count(seconds_between(r.started_at, in.at));
        o << "}";
    }
    auto mb = [&](const std::optional<uint64_t>& n) {
        if (n) o << *n;
        else o << "null";
    };
    o << "],\"memory\":{\"available_mb\":";
    mb(in.available_mb);
    o << ",\"total_mb\":";
    mb(in.total_mb);
    o << ",\"min_avail_mb\":" << in.min_avail_mb << "},\"guest\":";
    if (in.guest)
        o << "{\"available_mb\":" << in.guest->available_mb << ",\"total_mb\":" << in.guest->total_mb << ",\"min_guest_mb\":" << in.guest->min_guest_mb << "}";
    else o << "null";
    o << ",\"logger\":{\"windows\":\"" << json_escape(in.windows_logger) << "\",\"wsl\":";
    if (in.wsl_logger) o << "\"" << json_escape(*in.wsl_logger) << "\"";
    else o << "null";
    o << "},\"records\":{\"readable\":" << (in.records_readable ? "true" : "false") << ",\"skipped\":" << in.records_skipped << ",\"dir\":\""
      << json_escape(in.records_dir) << "\"}}";
    return o.str();
}

// --- The job history log (D01 T01 §2) ---------------------------------------------------------------------------
// One JSON line per finished job, appended to jobs-YYYY-MM-DD.jsonl: what ran when, how long it waited and ran, and how
// it ended, for the Queue page and report --json.

struct JobLine {
    std::string started_at, ended_at;     // UTC, YYYY-MM-DDTHH:MM:SSZ: when bosun took the job, when its command ended
    int64_t pid = 0;                      // the bosun process
    std::string command;
    std::optional<std::string> repo;
    std::string side;                     // "windows" or "wsl"
    std::string ns;
    std::optional<int> slot;              // null for a job that ran without one
    double waited_s = 0, ran_s = 0;       // until the command started, and from then to its end
    std::optional<int64_t> exit_code;     // null when the WSL shim went away without one
    bool timed_out_into_queue = false;    // the slot wait ran out and the job ran without a slot
    // The run's end and length in milliseconds (D01 T01 §4 completion review): ended_at keeps whole seconds and ran_s
    // tenths, so two jobs run one after the other within a second looked like they overlapped. Optional, so a line
    // from before reads with them null and falls back to the coarse times.
    std::optional<int64_t> ended_ms, ran_ms;
};

inline std::string seconds_text(double s) {
    char buf[32];
    std::snprintf(buf, sizeof(buf), "%.1f", s < 0 ? 0.0 : s);
    return buf;
}

inline std::string job_line_json(const JobLine& j) {
    std::ostringstream o;
    o << "{\"schema_version\":1,\"started_at\":\"" << json_escape(j.started_at) << "\",\"ended_at\":\"" << json_escape(j.ended_at)
      << "\",\"pid\":" << j.pid << ",\"command\":\"" << json_escape(j.command) << "\",\"repo\":";
    if (j.repo) o << "\"" << json_escape(*j.repo) << "\"";
    else o << "null";
    o << ",\"side\":\"" << json_escape(j.side) << "\",\"ns\":\"" << json_escape(j.ns) << "\",\"slot\":";
    if (j.slot) o << *j.slot;
    else o << "null";
    o << ",\"waited_s\":" << seconds_text(j.waited_s) << ",\"ran_s\":" << seconds_text(j.ran_s) << ",\"exit_code\":";
    if (j.exit_code) o << *j.exit_code;
    else o << "null";
    o << ",\"timed_out_into_queue\":" << (j.timed_out_into_queue ? "true" : "false") << ",\"ended_ms\":";
    if (j.ended_ms) o << *j.ended_ms;
    else o << "null";
    o << ",\"ran_ms\":";
    if (j.ran_ms) o << *j.ran_ms;
    else o << "null";
    o << "}";
    return o.str();
}

inline std::optional<JobLine> parse_job_line(const std::string& text) {
    auto obj = parse_flat_object(text);
    if (!obj) return std::nullopt;
    auto get = [&](const char* k) -> const FlatValue* {
        auto it = obj->find(k);
        return it == obj->end() ? nullptr : &it->second;
    };
    auto is = [](const FlatValue* v, FlatValue::Kind k) { return v && v->kind == k; };
    auto number = [](const FlatValue* v) { return v && (v->kind == FlatValue::Integer || v->kind == FlatValue::Decimal); };
    const FlatValue *version = get("schema_version"), *started = get("started_at"), *ended = get("ended_at"), *pid = get("pid"),
                    *command = get("command"), *repo = get("repo"), *side = get("side"), *ns = get("ns"), *slot = get("slot"),
                    *waited = get("waited_s"), *ran = get("ran_s"), *code = get("exit_code"), *timed = get("timed_out_into_queue");
    if (!is(version, FlatValue::Integer) || version->number != 1) return std::nullopt;
    if (!is(started, FlatValue::String) || !is(ended, FlatValue::String) || !is(pid, FlatValue::Integer) || !is(command, FlatValue::String)
        || !is(side, FlatValue::String) || !is(ns, FlatValue::String) || !number(waited) || !number(ran) || !is(timed, FlatValue::Bool)
        || !repo || !slot || !code)
        return std::nullopt;
    if (side->text != "windows" && side->text != "wsl") return std::nullopt;
    // Real times and durations no job can have the other way round, so a damaged line is refused and counted rather
    // than read and then dropped where its time is used (round 4 of D01 T01 §4).
    if (!utc_seconds(started->text) || !utc_seconds(ended->text) || !std::isfinite(waited->decimal) || !std::isfinite(ran->decimal) ||
        waited->decimal < 0 || ran->decimal < 0 || ran->decimal > 1e9 || waited->decimal > 1e9)
        return std::nullopt;
    JobLine j;
    j.started_at = started->text;
    j.ended_at = ended->text;
    j.pid = pid->number;
    j.command = command->text;
    j.side = side->text;
    j.ns = ns->text;
    j.waited_s = waited->decimal;
    j.ran_s = ran->decimal;
    j.timed_out_into_queue = timed->flag;
    if (repo->kind == FlatValue::String) j.repo = repo->text;
    else if (repo->kind != FlatValue::Null) return std::nullopt;
    if (slot->kind == FlatValue::Integer && !slot->negative) j.slot = static_cast<int>(slot->magnitude);
    else if (slot->kind != FlatValue::Null) return std::nullopt;
    for (auto [key, out] : {std::pair{"ended_ms", &j.ended_ms}, std::pair{"ran_ms", &j.ran_ms}}) {
        const FlatValue* v = get(key);
        if (!v || v->kind == FlatValue::Null) continue;
        if (v->kind != FlatValue::Integer || v->negative) return std::nullopt;
        *out = v->number;
    }
    if (code->kind == FlatValue::Integer) j.exit_code = code->number;
    else if (code->kind != FlatValue::Null) return std::nullopt;
    return j;
}

// Overlapping builds from the job log (D01 T01 §2): each job's run is the interval from ended_at minus ran_s to
// ended_at, and the overlap is the most runs open at one instant. A job that only waited (ran_s 0) never counts, and
// two runs where one ends as the next starts don't overlap. Only jobs that ended at or after `since_s` count.
struct BuildOverlap {
    int64_t jobs = 0, max_overlap = 0;
    std::optional<int64_t> at;  // seconds since 1970, when the most runs were open
};

inline BuildOverlap build_overlap(const std::vector<JobLine>& jobs, int64_t since_s) {
    BuildOverlap b;
    // In milliseconds: from ended_ms and ran_ms where the line has them, else from the second and tenth it always has.
    // Each run is clipped to the window, so a job that started before it counts only from `since`, and one that ended
    // as the window began counts for nothing (completion review).
    std::vector<std::pair<int64_t, int>> events;  // (ms, +1 start or -1 end); an end sorts before a start at one time
    const int64_t since_ms = since_s * 1000;
    for (const auto& j : jobs) {
        auto end = utc_seconds(j.ended_at);
        if (!end || *end < since_s) continue;
        ++b.jobs;
        int64_t stop = j.ended_ms ? *j.ended_ms : *end * 1000;
        int64_t ran = j.ran_ms ? *j.ran_ms : static_cast<int64_t>(j.ran_s * 1000 + 0.5);
        if (ran <= 0) continue;
        int64_t start = std::max(stop - ran, since_ms);
        if (stop <= start) continue;
        events.push_back({start, +1});
        events.push_back({stop, -1});
    }
    std::sort(events.begin(), events.end());
    int64_t open = 0;
    for (const auto& [t, delta] : events) {
        open += delta;
        if (open > b.max_overlap) {
            b.max_overlap = open;
            b.at = t / 1000;
        }
    }
    return b;
}

// --- report --json and the logger snapshots (D01 T01 §4) -----------------------------------------------------------

inline void json_count(std::ostringstream& o, const std::optional<int64_t>& v) {
    if (v) o << *v;
    else o << "null";
}

inline void json_text(std::ostringstream& o, const std::string& v) {
    if (v.empty()) o << "null";
    else o << "\"" << json_escape(v) << "\"";
}

// A "side:repo" key back into its two parts; the side never holds a colon.
inline std::pair<std::string, std::string> side_and_repo(const std::string& key) {
    size_t colon = key.find(':');
    return colon == std::string::npos ? std::pair<std::string, std::string>{"", key} : std::pair{key.substr(0, colon), key.substr(colon + 1)};
}

inline std::string report_json(const Report& r, const BuildOverlap& b, const ReadStatus& read, const std::string& generated_at,
                               const std::string& since, int days_requested, int64_t low_mb = 2048) {
    std::ostringstream o;
    o << "{\"schema_version\":1,\"generated_at\":\"" << json_escape(generated_at) << "\",\"since\":\"" << json_escape(since)
      << "\",\"days_requested\":" << days_requested << ",\"days_covered\":" << r.dates.size() << ",\"first_sample\":";
    json_text(o, r.first_sample);
    o << ",\"last_sample\":";
    json_text(o, r.last_sample);
    o << ",\"low_mb\":" << low_mb << ",\"read\":{\"complete\":" << (read.complete ? "true" : "false") << ",\"files\":" << read.files
      << ",\"unreadable_files\":" << read.unreadable_files << ",\"rejected_rows\":" << read.rejected_rows << "}";
    o << ",\"windows\":{\"samples\":" << r.win_samples << ",\"low_samples\":" << r.low_samples << ",\"min_available_mb\":";
    json_count(o, r.min_avail_mb);
    o << ",\"worst_minute\":";
    json_text(o, r.worst_minute);
    o << ",\"worst_repos\":[";
    std::vector<std::pair<std::string, int64_t>> worst(r.worst_repos.begin(), r.worst_repos.end());
    std::sort(worst.begin(), worst.end(), [](const auto& a, const auto& c) { return a.second != c.second ? a.second > c.second : a.first < c.first; });
    for (size_t i = 0; i < worst.size(); ++i) {
        auto [side, repo] = side_and_repo(worst[i].first);
        o << (i ? "," : "") << "{\"repo\":\"" << json_escape(repo) << "\",\"side\":\"" << json_escape(side) << "\",\"private_mb\":" << worst[i].second << "}";
    }
    o << "],\"max_commit_mb\":";
    json_count(o, r.max_commit_mb);
    o << ",\"commit_limit_mb\":";
    json_count(o, r.commit_limit_mb);
    o << ",\"max_pages_in_per_sec\":";
    json_count(o, r.max_pages_in);
    o << "},\"wsl\":{\"samples\":" << r.wsl_samples << ",\"min_available_mb\":";
    json_count(o, r.wsl_min_avail_mb);
    o << ",\"max_swap_mb\":";
    json_count(o, r.wsl_max_swap_mb);
    o << ",\"max_psi_full_avg10_x100\":";
    json_count(o, r.wsl_max_psi_full_x100);
    o << ",\"max_oom_kills\":";
    json_count(o, r.wsl_max_oom_kills);
    o << "},\"builds\":{\"jobs\":" << b.jobs << ",\"max_overlap\":" << b.max_overlap << ",\"at\":";
    if (b.at) o << "\"" << utc_stamp(*b.at) << "\"";
    else o << "null";
    o << "},\"active_repos\":{\"max\":" << r.active_max << ",\"minute\":";
    json_text(o, r.active_minute);
    o << ",\"total_private_mb\":" << r.active_total_mb << "},\"repos\":[";
    std::vector<std::pair<std::pair<std::string, std::string>, RepoPeak>> peaks(r.repo_peaks.begin(), r.repo_peaks.end());
    std::sort(peaks.begin(), peaks.end(), [](const auto& a, const auto& c) {
        return a.second.private_mb != c.second.private_mb ? a.second.private_mb > c.second.private_mb : a.first < c.first;
    });
    for (size_t i = 0; i < peaks.size(); ++i) {
        const auto& [key, p] = peaks[i];
        o << (i ? "," : "") << "{\"repo\":\"" << json_escape(key.second) << "\",\"side\":\"" << json_escape(key.first) << "\",\"peak_private_mb\":"
          << p.private_mb << ",\"peak_rss_mb\":" << p.rss_mb << ",\"peak_processes\":" << p.processes << "}";
    }
    o << "],\"series\":[";
    bool first = true;
    for (const auto& [minute, point] : r.series) {
        o << (first ? "" : ",") << "{\"minute\":\"" << json_escape(minute) << "\",\"win_available_mb\":";
        json_count(o, point.win_available_mb);
        o << ",\"wsl_available_mb\":";
        json_count(o, point.wsl_available_mb);
        o << ",\"repos\":" << point.repos << ",\"repo_private_mb\":" << point.repo_private_mb << "}";
        first = false;
    }
    o << "]}";
    return o.str();
}

// One live sample, as the loggers write it to current-<side>.json beside the CSV files: the same figures as the sample's
// CSV rows, so the panel's live cards need no CSV read.
struct SnapshotGroup {
    std::string name;
    int64_t processes = 0, private_mb = 0, rss_mb = 0;
};

struct WslVm {
    bool running = false;
    int64_t processes = 0, private_mb = 0, rss_mb = 0;
};

struct Snapshot {
    std::string side, at;  // "windows" or "wsl"; UTC, YYYY-MM-DDTHH:MM:SSZ
    int64_t interval_s = 0;
    std::vector<std::pair<std::string, std::optional<int64_t>>> memory;  // in the order written; null when not measured
    std::optional<WslVm> wsl_vm;                                         // the Windows side's view of the WSL VM
    std::vector<SnapshotGroup> repos, processes;
    bool history_written = true;  // whether this sample's CSV rows were written: false while history is being lost
};

inline std::string snapshot_json(const Snapshot& s) {
    std::ostringstream o;
    o << "{\"schema_version\":1,\"side\":\"" << json_escape(s.side) << "\",\"at\":\"" << json_escape(s.at) << "\",\"interval_s\":" << s.interval_s
      << ",\"memory\":{";
    for (size_t i = 0; i < s.memory.size(); ++i) {
        o << (i ? "," : "") << "\"" << json_escape(s.memory[i].first) << "\":";
        json_count(o, s.memory[i].second);
    }
    o << "},\"wsl_vm\":";
    if (s.wsl_vm)
        o << "{\"running\":" << (s.wsl_vm->running ? "true" : "false") << ",\"processes\":" << s.wsl_vm->processes << ",\"private_mb\":" << s.wsl_vm->private_mb
          << ",\"rss_mb\":" << s.wsl_vm->rss_mb << "}";
    else o << "null";
    auto groups = [&](const char* key, const char* label, const std::vector<SnapshotGroup>& list) {
        o << ",\"" << key << "\":[";
        for (size_t i = 0; i < list.size(); ++i)
            o << (i ? "," : "") << "{\"" << label << "\":\"" << json_escape(list[i].name) << "\",\"processes\":" << list[i].processes
              << ",\"private_mb\":" << list[i].private_mb << ",\"rss_mb\":" << list[i].rss_mb << "}";
        o << "]";
    };
    groups("repos", "repo", s.repos);
    groups("processes", "name", s.processes);
    o << ",\"history_written\":" << (s.history_written ? "true" : "false") << "}";
    return o.str();
}

// --- Instances (D03 T02 §1) ------------------------------------------------------------------------------------------
// Every agent and editor on a side, recognised by its image, never by a window title, and each job holding a slot. The
// Windows tool and the WSL scanner (wsl/bosun_log.py, which mirrors these rules) write the same document.

// One process as a scanner sees it. `start` is the process's start as its side counts it (a FILETIME on Windows,
// /proc/<pid>/stat field 22 on Linux), to tell a reused process id from the process a record named.
struct ProcInfo {
    int64_t pid = 0, ppid = 0;
    std::string name;
    std::optional<int64_t> private_kb;  // in KB, so a sum of many small processes isn't lost to rounding each one
    std::optional<uint64_t> start;
    std::optional<std::string> started_at, cwd, repo;
};

// The kind of agent or editor an image is, or "" when it is neither. Case and a trailing .exe don't count.
inline std::string instance_kind(std::string name) {
    std::transform(name.begin(), name.end(), name.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    if (name.size() > 4 && name.compare(name.size() - 4, 4, ".exe") == 0) name.resize(name.size() - 4);
    if (name == "claude" || name == "codex" || name == "agy") return name;
    if (name == "code") return "code";
    if (name == "antigravity") return "antigravity";
    return "";
}

// Each process's parent, or 0 when it has none here: a parent that started after its child is a reused id, not the
// parent (both starts known), so a loop of reused ids can't form.
inline std::map<int64_t, int64_t> parents_of(const std::vector<ProcInfo>& procs) {
    std::map<int64_t, const ProcInfo*> by_pid;
    for (const auto& p : procs) by_pid[p.pid] = &p;
    std::map<int64_t, int64_t> parent;
    for (const auto& p : procs) {
        auto it = by_pid.find(p.ppid);
        bool real = it != by_pid.end() && p.ppid != p.pid &&
                    !(p.start && it->second->start && *it->second->start > *p.start);
        parent[p.pid] = real ? p.ppid : 0;
    }
    return parent;
}

// The first of `pid` and its ancestors that is in `roots`, or 0. Bounded by the number of processes, so even a loop
// that slipped past parents_of ends.
inline int64_t first_root(int64_t pid, const std::map<int64_t, int64_t>& parent, const std::set<int64_t>& roots) {
    for (size_t steps = 0; pid && steps <= parent.size(); ++steps) {
        if (roots.count(pid)) return pid;
        auto it = parent.find(pid);
        pid = it == parent.end() ? 0 : it->second;
    }
    return 0;
}

// The instances among `procs`: each process of a kind with no ancestor of the same kind, so a VS Code window's helpers
// are one editor, and so are the sidecars Antigravity's language server starts through cmd.exe (found on this machine,
// 2026-11-16: their parent is cmd.exe, never Antigravity). The walk is bounded, as in first_root.
inline std::set<int64_t> instance_roots(const std::vector<ProcInfo>& procs, const std::map<int64_t, int64_t>& parent) {
    std::map<int64_t, std::string> kind;
    for (const auto& p : procs) kind[p.pid] = instance_kind(p.name);
    std::set<int64_t> roots;
    for (const auto& p : procs) {
        const std::string& k = kind[p.pid];
        if (k.empty()) continue;
        bool nested = false;
        int64_t up = p.pid;
        for (size_t steps = 0; !nested && steps <= parent.size(); ++steps) {
            auto it = parent.find(up);
            up = it == parent.end() ? 0 : it->second;
            if (!up || up == p.pid) break;  // a loop back to itself is no ancestor
            nested = kind[up] == k;
        }
        if (!nested) roots.insert(p.pid);
    }
    return roots;
}

// Each root's memory in MB: its own private memory and that of every descendant whose nearest root it is, so a job under
// an agent counts in the job and never in the agent. Summed in KB and rounded down once (completion review: twelve
// helpers of 0.75 MB each had read 0 MB). Null when nothing it owns was measured.
inline std::map<int64_t, std::optional<int64_t>> owned_memory(const std::vector<ProcInfo>& procs, const std::map<int64_t, int64_t>& parent,
                                                              const std::set<int64_t>& roots) {
    std::map<int64_t, std::optional<int64_t>> kb;
    for (int64_t r : roots) kb[r] = std::nullopt;
    for (const auto& p : procs) {
        int64_t owner = first_root(p.pid, parent, roots);
        if (!owner || !p.private_kb) continue;
        auto& total = kb[owner];
        total = std::min<int64_t>(total.value_or(0) + *p.private_kb, INT64_MAX / 2);
    }
    std::map<int64_t, std::optional<int64_t>> mb;
    for (const auto& [root, total] : kb) mb[root] = total ? std::optional<int64_t>(*total / 1024) : std::nullopt;
    return mb;
}

// Claude Code's own record of a session (~/.claude/sessions/<pid>.json): its process and start, and what it says.
struct SessionRecord {
    int64_t pid = 0;
    std::string proc_start;  // as the record writes it: a FILETIME on Windows, the /proc start tick on Linux
    std::optional<std::string> status;
};

// A record's fields, or nothing when it isn't one: a document Claude Code writes, read for its flat fields only.
inline std::optional<SessionRecord> parse_session_record(const std::string& text) {
    auto obj = parse_flat_object(text, true, true);
    if (!obj) return std::nullopt;
    auto pid = obj->find("pid");
    auto start = obj->find("procStart");
    if (pid == obj->end() || pid->second.kind != FlatValue::Integer || pid->second.negative || pid->second.magnitude == 0 || pid->second.overflow)
        return std::nullopt;
    SessionRecord r;
    r.pid = pid->second.number;
    if (start == obj->end()) return std::nullopt;
    if (start->second.kind == FlatValue::String) r.proc_start = start->second.text;
    else if (start->second.kind == FlatValue::Integer && !start->second.overflow) r.proc_start = start->second.text;
    else return std::nullopt;
    auto status = obj->find("status");
    if (status != obj->end() && status->second.kind == FlatValue::String) r.status = status->second.text;
    return r;
}

// A review in flight (scripts/review-dispatch.py's mark in a repo's build/review-in-flight/): its dispatcher's process,
// and whether it still counts (that process alive, its deadline not passed), which the scanner decides.
struct ReviewMark {
    int64_t pid = 0;
    std::string reviewer;
    bool live = false;
};

struct Instance {
    std::string side, kind, name;
    int64_t pid = 0;
    std::optional<std::string> cwd, repo;
    std::optional<int64_t> private_mb;
    std::optional<std::string> started_at;
    std::string state = "unknown";
    std::optional<std::string> evidence;
    std::optional<int> slot;
    std::optional<std::string> command;
    std::optional<int64_t> guest_pid;
    std::optional<std::string> guest_distro;
};

// An instance's state, from the evidence in this order: the slot its job holds, a live review its process runs under,
// Claude Code's record of the session; with none, unknown. Never guessed from CPU use.
inline void instance_state(Instance& i, const SlotRecord* holder, const ReviewMark* mark, const SessionRecord* session,
                           const std::optional<uint64_t>& own_start) {
    if (holder) {
        std::string slot = "slot " + std::to_string(holder->slot.value_or(0));
        if (holder->command_started_at) {
            i.state = "working";
            i.evidence = "holds " + slot + ", its command running since " + *holder->command_started_at;
        } else {
            i.state = "idle";
            i.evidence = "holds " + slot + "; its command has not started (waiting for guest memory)";
        }
        return;
    }
    if (mark && mark->live) {
        i.state = "reviewing";
        i.evidence = "runs under " + mark->reviewer + "'s review (dispatcher pid " + std::to_string(mark->pid) + ")";
        return;
    }
    if (session) {
        if (!own_start || session->proc_start != std::to_string(*own_start)) {
            i.state = "unknown";
            i.evidence = "the session record for pid " + std::to_string(session->pid) + " names a process that started at another time";
            return;
        }
        if (session->status == std::optional<std::string>("busy")) i.state = "working";
        else if (session->status == std::optional<std::string>("idle")) i.state = "idle";
        else i.state = "unknown";
        i.evidence = "Claude Code says " + session->status.value_or("nothing") + " (session record)";
        return;
    }
    i.state = "unknown";
    i.evidence = std::nullopt;
}

struct InstancesDoc {
    std::string side, at;
    std::optional<std::string> distro;  // the WSL distro; null on Windows
    std::vector<Instance> instances;
    int64_t unreadable = 0;                                    // processes whose folder or memory couldn't be read
    std::vector<std::pair<std::string, std::string>> problems;  // (source, why) for each source that couldn't be read
};

inline std::string instances_json(const InstancesDoc& d) {
    std::ostringstream o;
    auto text = [&](const std::optional<std::string>& v) {
        if (v) o << "\"" << json_escape(*v) << "\"";
        else o << "null";
    };
    auto count = [&](const std::optional<int64_t>& v) {
        if (v) o << *v;
        else o << "null";
    };
    o << "{\"schema_version\":1,\"side\":\"" << json_escape(d.side) << "\",\"distro\":";
    text(d.distro);
    o << ",\"at\":\"" << json_escape(d.at) << "\",\"instances\":[";
    for (size_t k = 0; k < d.instances.size(); ++k) {
        const Instance& i = d.instances[k];
        o << (k ? "," : "") << "{\"side\":\"" << json_escape(i.side) << "\",\"kind\":\"" << json_escape(i.kind) << "\",\"name\":\""
          << json_escape(i.name) << "\",\"pid\":" << i.pid << ",\"cwd\":";
        text(i.cwd);
        o << ",\"repo\":";
        text(i.repo);
        o << ",\"private_mb\":";
        count(i.private_mb);
        o << ",\"started_at\":";
        text(i.started_at);
        o << ",\"state\":\"" << json_escape(i.state) << "\",\"evidence\":";
        text(i.evidence);
        o << ",\"slot\":";
        count(i.slot ? std::optional<int64_t>(*i.slot) : std::nullopt);
        o << ",\"command\":";
        text(i.command);
        o << ",\"guest_pid\":";
        count(i.guest_pid);
        o << ",\"guest_distro\":";
        text(i.guest_distro);
        o << "}";
    }
    o << "],\"unreadable\":" << d.unreadable << ",\"problems\":[";
    for (size_t k = 0; k < d.problems.size(); ++k)
        o << (k ? "," : "") << "{\"source\":\"" << json_escape(d.problems[k].first) << "\",\"why\":\"" << json_escape(d.problems[k].second) << "\"}";
    o << "]}";
    return o.str();
}

// The text form: one line per instance.
inline std::string instances_text(const InstancesDoc& d) {
    std::ostringstream o;
    if (d.instances.empty()) o << "no agents, editors or jobs running\n";
    for (const auto& i : d.instances) {
        o << i.kind << " " << i.pid << " " << (i.repo ? *i.repo : "(no repo)") << " " << i.state;
        if (i.private_mb) o << " " << *i.private_mb << " MB";
        if (i.slot) o << " slot " << *i.slot;
        o << "\n";
    }
    for (const auto& [source, why] : d.problems) o << "can't read " << source << ": " << why << "\n";
    if (d.unreadable) o << d.unreadable << " processes couldn't be read\n";
    return o.str();
}

// --- One config file for queue limits (D01 T01 §5) -------------------------------------------------------------------
// %LOCALAPPDATA%\Bosun\config.json holds the limits the panel's Limits form saves. A flag beats a variable, a variable
// the file, and the file the default, so scripts that set flags or variables behave as they always have.

// The most config.json may hold, read or written: the four limits take a few hundred bytes (round 3: a save that wrote
// more than the reader takes had made every saved limit unreadable).
constexpr size_t kMaxConfigBytes = 64 * 1024;

struct LimitSpec {
    const char* key;
    uint64_t min, max, fallback;
    const char* env;  // the variable that sets it
};

inline const std::vector<LimitSpec>& limit_specs() {
    static const std::vector<LimitSpec> specs{
        {"slots", 1, 64, 2, "BOSUN_SLOTS"},
        {"min_avail_mb", 0, kMaxQuantity, 6144, "BOSUN_MIN_AVAIL_MB"},
        {"max_wait_s", 0, 604800, 900, "BOSUN_MAX_WAIT"},  // 0 is no deadline: a job waits as long as it takes
        {"min_guest_mb", 0, kMaxQuantity, 3072, "BOSUN_MIN_GUEST_MB"},
    };
    return specs;
}

inline const LimitSpec* find_limit(const std::string& key) {
    for (const auto& s : limit_specs())
        if (key == s.key) return &s;
    return nullptr;
}

inline std::string limit_range(const LimitSpec& s) {
    return "a whole number from " + std::to_string(s.min) + " to " + std::to_string(s.max);
}

struct ConfigFile {
    bool valid = true;                                        // false: the whole file was refused, every key falls back
    std::string error;                                        // why, when it was
    std::map<std::string, uint64_t> values;                   // the known keys it sets validly
    std::vector<std::pair<std::string, std::string>> refused;  // (key, why) for each known key with a bad value
    std::vector<std::pair<std::string, FlatValue>> unknown;   // keys this version doesn't know, kept to write back
};

inline ConfigFile parse_config(const std::string& text) {
    ConfigFile f;
    auto obj = parse_flat_object(text, true);
    if (!obj) {
        f.valid = false;
        f.error = "not one flat JSON object (cut short, a repeated key, or not JSON)";
        return f;
    }
    auto version = obj->find("schema_version");
    if (version == obj->end() || version->second.kind != FlatValue::Integer || version->second.overflow || version->second.number != 1) {
        f.valid = false;
        f.error = version == obj->end() ? "no schema_version" : "schema_version is not 1";
        return f;
    }
    for (const auto& [key, v] : *obj) {
        if (key == "schema_version") continue;
        const LimitSpec* spec = find_limit(key);
        if (!spec) {
            f.unknown.push_back({key, v});
            continue;
        }
        if (v.kind != FlatValue::Integer || v.overflow || v.negative || v.magnitude < spec->min || v.magnitude > spec->max) {
            f.refused.push_back({key, "must be " + limit_range(*spec)});
            continue;
        }
        f.values[key] = v.magnitude;
    }
    return f;
}

inline std::string config_json(const ConfigFile& f) {
    std::ostringstream o;
    o << "{\n  \"schema_version\": 1";
    for (const auto& s : limit_specs()) {
        auto it = f.values.find(s.key);
        if (it != f.values.end()) o << ",\n  \"" << s.key << "\": " << it->second;
    }
    for (const auto& [key, v] : f.unknown) {
        o << ",\n  \"" << json_escape(key) << "\": ";
        switch (v.kind) {
            case FlatValue::String: o << "\"" << json_escape(v.text) << "\""; break;
            // A number goes back exactly as it was read, so no value this version doesn't know is ever rewritten: not an
            // unsigned one as signed, nor one past a double's range as "inf" (round 2).
            case FlatValue::Integer:
            case FlatValue::Decimal: o << v.text; break;
            case FlatValue::Bool: o << (v.flag ? "true" : "false"); break;
            case FlatValue::Null: o << "null"; break;
            case FlatValue::Nested: o << v.text; break;  // only a reader asked for nested values keeps one
        }
    }
    o << "\n}\n";
    return o.str();
}

struct ResolvedLimit {
    uint64_t value = 0;
    std::string source;     // "flag", "environment", "file" or "default"
    std::string env_error;  // why the variable was skipped, when it was set but bad
};

// The flag if given, else a valid variable, else a valid saved value, else the default.
inline ResolvedLimit resolve_limit(const LimitSpec& s, const std::optional<uint64_t>& flag, const std::optional<std::string>& env,
                                   const std::optional<uint64_t>& saved) {
    ResolvedLimit r;
    std::optional<uint64_t> from_env;
    if (env && !env->empty()) {
        from_env = parse_count(*env, s.min, s.max);
        if (!from_env) r.env_error = std::string(s.env) + "=" + *env + " is not " + limit_range(s);
    }
    if (flag) r = {*flag, "flag", r.env_error};
    else if (from_env) r = {*from_env, "environment", ""};
    else if (saved) r = {*saved, "file", r.env_error};
    else r = {s.fallback, "default", r.env_error};
    return r;
}

}  // namespace dm

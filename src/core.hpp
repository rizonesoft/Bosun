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

// A job and everything it started may write at most budget_gb in all; 0 means no budget. A command that writes without
// end (a test whose fake child floods a log, a loop that never stops) would otherwise fill the drive, and a full system
// drive takes Windows and the WSL disk image on it down with it.
inline bool over_write_budget(uint64_t written_bytes, uint64_t budget_gb) {
    return budget_gb != 0 && written_bytes > budget_gb * (uint64_t{1} << 30);
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
    if (!r.min_avail_mb) return "No samples yet. Is the logger running? Check with: bosun status\n";
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
};

inline std::string record_json(const SlotRecord& r) {
    std::ostringstream o;
    o << "{\"schema_version\":1,\"kind\":\"" << json_escape(r.kind) << "\",\"pid\":" << r.pid << ",\"process_start\":" << r.process_start
      << ",\"started_at\":\"" << json_escape(r.started_at) << "\",\"command\":\"" << json_escape(r.command) << "\",\"repo\":";
    if (r.repo) o << "\"" << json_escape(*r.repo) << "\"";
    else o << "null";
    o << ",\"side\":\"" << json_escape(r.side) << "\"";
    if (r.slot) o << ",\"slot\":" << *r.slot;
    o << ",\"session\":" << r.session << ",\"ns\":\"" << json_escape(r.ns) << "\"}";
    return o.str();
}

// A flat JSON object whose values are strings, integers or null, as the records are; anything else is refused.
struct FlatValue {
    enum Kind { String, Integer, Null } kind = Null;
    std::string text;
    int64_t number = 0;
    bool negative = false;
    uint64_t magnitude = 0;
};

inline std::optional<std::map<std::string, FlatValue>> parse_flat_object(const std::string& s) {
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
        if (i < s.size() && s[i] == '"') {
            v.kind = FlatValue::String;
            if (!str(v.text)) return std::nullopt;
        } else if (s.compare(i, 4, "null") == 0) {
            v.kind = FlatValue::Null;
            i += 4;
        } else {
            v.kind = FlatValue::Integer;
            if (i < s.size() && s[i] == '-') { v.negative = true; ++i; }
            size_t digits = 0;
            while (i < s.size() && s[i] >= '0' && s[i] <= '9') {
                uint64_t d = static_cast<uint64_t>(s[i] - '0');
                if (v.magnitude > (UINT64_MAX - d) / 10) return std::nullopt;
                v.magnitude = v.magnitude * 10 + d;
                ++i;
                ++digits;
            }
            if (!digits || (i < s.size() && (s[i] == '.' || s[i] == 'e' || s[i] == 'E'))) return std::nullopt;
            if (v.negative ? v.magnitude > uint64_t{1} << 63 : false) return std::nullopt;
            v.number = v.negative ? static_cast<int64_t>(0 - v.magnitude) : static_cast<int64_t>(v.magnitude);
        }
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
    return r;
}

// A queue namespace as a folder name: an injective mapping onto text Windows accepts as one. Letters, digits, '_' and
// '-' stay; every other UTF-8 byte, '.' included (Windows drops a trailing dot), becomes %XX; and the "ns-" prefix keeps
// an empty name and the reserved device names (CON, NUL, COM1...) valid.
inline std::string ns_dir(const std::string& ns) {
    static const char hex[] = "0123456789ABCDEF";
    std::string out = "ns-";
    for (unsigned char c : ns) {
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-') out.push_back(static_cast<char>(c));
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

// Only a stale waiter's record is removed by a reader. A slot record is only ever written or removed by the holder of
// that slot's mutex, so a reader never touches one: a killed holder's is replaced by the next holder.
inline bool reader_removes(const SlotRecord& r, RecordState state) {
    return r.kind == "waiter" && state == RecordState::Stale;
}

}  // namespace dm

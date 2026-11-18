#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <winternl.h>
#include <pdh.h>
#include <sddl.h>

#include <atomic>
#include <chrono>
#include <cmath>
#include <climits>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <condition_variable>
#include <map>
#include <mutex>
#include <thread>
#include <tuple>
#include <type_traits>
#include <unordered_map>

#include "app.hpp"
#include "core.hpp"

namespace fs = std::filesystem;

namespace {

std::string narrow(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

std::wstring env(const wchar_t* name) {
    wchar_t buf[32768];
    DWORD n = GetEnvironmentVariableW(name, buf, 32768);
    return n ? std::wstring(buf, n) : std::wstring{};
}

// A limit from the environment, parsed as strictly as its flag; a value that isn't one is said and the default kept.
uint64_t env_num(const wchar_t* name, uint64_t fallback, uint64_t hi = dm::kMaxQuantity) {
    std::wstring v = env(name);
    if (v.empty()) return fallback;
    auto n = dm::parse_count(narrow(v), 0, hi);
    if (n) return *n;
    std::wcerr << L"[bosun] ignoring " << name << L"=" << v << L": not a whole number up to " << hi << L", so " << fallback << L" applies\n";
    return fallback;
}

uint64_t available_mb() {
    MEMORYSTATUSEX m{};
    m.dwLength = sizeof(m);
    GlobalMemoryStatusEx(&m);
    return m.ullAvailPhys / (1024 * 1024);
}

struct Options {
    // The four limits are resolved in apply_limits (D01 T01 §5): flag, then variable, then config.json, then default.
    int slots = 2;
    uint64_t min_avail_mb = 6144;
    uint64_t max_wait_s = 900;
    std::map<std::string, uint64_t> limit_flags;  // the limits given as flags, by config key
    uint64_t max_write_gb = env_num(L"BOSUN_MAX_WRITE_GB", 100, dm::kMaxWriteGb);
    std::wstring ns = L"Bosun";
    int days = 7;
    int report_low_mb = 2048;  // bosun report --low-mb: the floor its pressure counts are against (D03 T01 §6)
    std::string bad;           // why an option was refused, when one says more than "bad options"
    int interval_s = 30;
    bool once = false;
    bool json = false;
    // The WSL shim's own figures for status --json (D01 T01 §3), and its guest floor for a job's record.
    std::optional<uint64_t> guest_avail_mb, guest_total_mb, min_guest_mb;
    std::wstring wsl_logger;
    // When the WSL shim began, in ms since 1970: acquire starts through interop up to a second later, and that much of
    // the job's wait would otherwise be missing from its line.
    std::optional<uint64_t> queued_at_ms;
    // The WSL shim's own Linux process, kept in the holder's record so the instance scanners can join the job's two
    // halves (D03 T02 §1).
    std::optional<int64_t> guest_pid;
    std::wstring guest_distro;  // and its distro: a Linux pid is unique only within one
    std::vector<std::wstring> command;
    // What a record says of the job (acquire takes them from the WSL shim; run works them out for itself).
    std::wstring repo, side, label;
};

// Returns false on a bad flag. Everything after -- is the command.
bool parse_flags(const std::vector<std::wstring>& args, Options& o) {
    for (size_t i = 0; i < args.size(); ++i) {
        const std::wstring& a = args[i];
        if (a == L"--") {
            o.command.assign(args.begin() + i + 1, args.end());
            return true;
        }
        if (a == L"--once") {
            o.once = true;
            continue;
        }
        if (a == L"--json") {
            o.json = true;
            continue;
        }
        if (i + 1 >= args.size()) return false;
        const std::wstring& v = args[++i];
        auto count = [&](uint64_t lo, uint64_t hi, auto& out) {
            auto n = dm::parse_count(narrow(v), lo, hi);
            if (n) out = static_cast<std::remove_reference_t<decltype(out)>>(*n);
            return n.has_value();
        };
        uint64_t guest = 0;
        if (a == L"--slots") { if (!count(0, 64, o.slots)) return false; o.limit_flags["slots"] = static_cast<uint64_t>(o.slots); }
        else if (a == L"--min-avail-mb") { if (!count(0, dm::kMaxQuantity, o.min_avail_mb)) return false; o.limit_flags["min_avail_mb"] = o.min_avail_mb; }
        else if (a == L"--max-wait") { if (!count(0, dm::kMaxQuantity, o.max_wait_s)) return false; o.limit_flags["max_wait_s"] = o.max_wait_s; }
        else if (a == L"--max-write-gb") { if (!count(0, dm::kMaxWriteGb, o.max_write_gb)) return false; }
        else if (a == L"--days") { if (!count(1, 3650, o.days)) return false; }
        else if (a == L"--low-mb") {
            if (!count(1, 65536, o.report_low_mb)) {
                o.bad = "--low-mb takes a whole number of MB from 1 to 65536, not '" + narrow(v) + "'";
                return false;
            }
        }
        else if (a == L"--interval") { if (!count(1, 86400, o.interval_s)) return false; }
        else if (a == L"--guest-avail-mb") { if (!count(0, dm::kMaxQuantity, guest)) return false; o.guest_avail_mb = guest; }
        else if (a == L"--guest-total-mb") { if (!count(0, dm::kMaxQuantity, guest)) return false; o.guest_total_mb = guest; }
        else if (a == L"--min-guest-mb") { if (!count(0, dm::kMaxQuantity, guest)) return false; o.min_guest_mb = guest; o.limit_flags["min_guest_mb"] = guest; }
        else if (a == L"--queued-at-ms") { if (!count(0, UINT64_MAX, guest)) return false; o.queued_at_ms = guest; }
        else if (a == L"--guest-pid") { if (!count(1, INT32_MAX, guest)) return false; o.guest_pid = static_cast<int64_t>(guest); }
        else if (a == L"--guest-distro") {
            if (!dm::distro_name_ok(narrow(v))) return false;
            o.guest_distro = v;
        }
        else if (a == L"--wsl-logger") {
            if (v != L"running" && v != L"stopped" && v != L"unknown") return false;
            o.wsl_logger = v;
        }
        else if (a == L"--ns") o.ns = v;
        else if (a == L"--repo") o.repo = v;
        else if (a == L"--side") {
            if (v != L"windows" && v != L"wsl") return false;
            o.side = v;
        }
        else if (a == L"--label") o.label = v;
        else return false;
    }
    return true;
}

// The guest's two figures come together or not at all, so a half-measured guest never reads as a real one.
bool parse_options(const std::vector<std::wstring>& args, Options& o) {
    return parse_flags(args, o) && o.guest_avail_mb.has_value() == o.guest_total_mb.has_value();
}

// Slot mutexes are opened by processes of either integrity level, so the
// descriptor grants the session's users access and carries a medium label.
HANDLE open_slot(const std::wstring& ns, int i) {
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;AU)S:(ML;;NW;;;ME)", SDDL_REVISION_1, &sd, nullptr)) return nullptr;
    SECURITY_ATTRIBUTES sa{sizeof(sa), sd, FALSE};
    std::wstring name = L"Local\\" + ns + L".Slot." + std::to_wstring(i);
    HANDLE h = CreateMutexW(&sa, FALSE, name.c_str());
    LocalFree(sd);
    return h;
}

struct Slot {
    HANDLE handle = nullptr;
    int index = -1;
};

// Takes a slot, or returns index -1 after max_wait so that a job is delayed and never stalled for good. `reason` hears
// why it waits ("slot" while every slot it may take is held, "memory" while a free slot's floor is unmet) each time
// that changes, so the waiter's record says why without being rewritten on every poll.
Slot acquire_slot(const Options& o, const std::function<void(const char*)>& reason = {}) {
    std::vector<HANDLE> handles;
    for (int i = 0; i < o.slots; ++i) {
        HANDLE h = open_slot(o.ns, i);
        if (!h) {
            std::cerr << "[bosun] cannot open slot " << i << ", error " << GetLastError() << "\n";
            ExitProcess(70);
        }
        handles.push_back(h);
    }
    auto start = std::chrono::steady_clock::now();
    bool announced = false;
    const char* told = "slot";  // what the waiter record already says
    for (;;) {
        const char* why = "slot";
        for (int i = 0; i < o.slots; ++i) {
            DWORD w = WaitForSingleObject(handles[i], 0);
            if (w != WAIT_OBJECT_0 && w != WAIT_ABANDONED) continue;
            if (dm::slot_admits(i, available_mb(), o.min_avail_mb)) {
                for (int j = 0; j < o.slots; ++j)
                    if (j != i) CloseHandle(handles[j]);
                return {handles[i], i};
            }
            ReleaseMutex(handles[i]);
            why = "memory";
        }
        if (reason && std::strcmp(why, told) != 0) reason(why);
        told = why;
        auto waited = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - start).count();
        if (o.max_wait_s && static_cast<uint64_t>(waited) >= o.max_wait_s) {
            std::cerr << "[bosun] waited " << waited << " s, starting anyway without a slot\n";
            for (HANDLE h : handles) CloseHandle(h);
            return {};
        }
        if (!announced) {
            std::cerr << "[bosun] waiting for a build slot (" << available_mb() << " MB available)\n";
            announced = true;
        }
        Sleep(500);
    }
}

// --- Slot records (D01 T01 §1): who holds each slot and who waits, beside the mutexes, which stay the authority ---

uint64_t own_process_start() {
    FILETIME created{}, exited{}, kernel{}, user{};
    GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user);
    return (uint64_t{created.dwHighDateTime} << 32) | created.dwLowDateTime;
}

uint32_t own_session() {
    DWORD session = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &session);
    return session;
}

int64_t epoch_ms() {
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    uint64_t t = (uint64_t{ft.dwHighDateTime} << 32) | ft.dwLowDateTime;
    return static_cast<int64_t>(t / 10000ULL) - 11644473600000LL;  // FILETIME counts 100 ns from 1601
}

std::string utc_now() {
    SYSTEMTIME t;
    GetSystemTime(&t);
    char buf[32];
    std::snprintf(buf, sizeof(buf), "%04u-%02u-%02uT%02u:%02u:%02uZ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return buf;
}

// %LOCALAPPDATA%\Bosun\run\<session>\<ns>\: per Windows session, as the Local\ mutexes are, and per namespace.
fs::path records_dir(const Options& o) {
    return fs::path(env(L"LOCALAPPDATA")) / L"Bosun" / L"run" / std::to_wstring(own_session()) / widen(dm::ns_dir(narrow(o.ns)));
}

// The job's repo: the one the WSL shim named, or the nearest folder above the working directory holding .git.
std::optional<std::string> job_repo(const Options& o) {
    if (!o.repo.empty()) return narrow(o.repo);
    wchar_t cwd[MAX_PATH * 4];
    DWORD n = GetCurrentDirectoryW(MAX_PATH * 4, cwd);
    if (!n || n >= MAX_PATH * 4) return std::nullopt;
    return dm::repo_from_path(narrow(std::wstring(cwd, n)), [](const std::string& dir) {
        std::error_code ec;
        return fs::exists(fs::path(widen(dir)) / L".git", ec);
    });
}

std::string job_command(const Options& o) {
    if (!o.label.empty()) return narrow(o.label);
    std::wstring line;
    for (const auto& a : o.command) line += (line.empty() ? L"" : L" ") + dm::quote_arg(a);
    return narrow(line);
}

// Publishes this job's records. A record that can't be written says so once and changes nothing the queue does.
class Records {
  public:
    explicit Records(const Options& o) : dir_(records_dir(o)) {
        base_.pid = static_cast<int64_t>(GetCurrentProcessId());
        base_.process_start = own_process_start();
        base_.started_at = utc_now();
        base_.command = job_command(o);
        base_.repo = job_repo(o);
        base_.side = o.side.empty() ? "windows" : narrow(o.side);
        base_.session = own_session();
        base_.ns = narrow(o.ns);
        base_.guest_pid = o.guest_pid;
        if (!o.guest_distro.empty()) base_.guest_distro = narrow(o.guest_distro);
        min_avail_mb_ = o.min_avail_mb;
        max_wait_s_ = o.max_wait_s;
        min_guest_mb_ = o.min_guest_mb;
    }
    // Written as the wait starts and rewritten (temporary file, then replace) each time its reason changes.
    // Every change goes under one lock: a cancelled wait's console handler runs done() on another thread while the
    // waiting thread may be rewriting the waiter's reason (round 1 of D01 T01 §3). Once done() has run, nothing is
    // written again, so a cancelled job never republishes a record after its cleanup.
    void waiting(const char* why = "slot") {
        std::lock_guard<std::mutex> lock(mu_);
        if (closed_) return;
        dm::SlotRecord r = base_;
        r.kind = "waiter";
        r.waiting_for = why;
        r.min_avail_mb = min_avail_mb_;
        r.max_wait_s = max_wait_s_;
        waiter_ = dir_ / widen(dm::waiter_file(r.pid, r.process_start));
        write(waiter_, r);
    }
    // Called holding slot N's mutex: only its holder ever writes or removes that slot's record.
    void holding(int slot) {
        std::lock_guard<std::mutex> lock(mu_);
        if (closed_) return;
        // A reader can hold the waiter file open for a moment, which makes the removal fail: it is retried briefly, and
        // a removal that still fails stays this job's to finish when it ends (a reader also drops a waiter whose own
        // process now holds a slot, so a job is never shown both waiting and holding for longer than that).
        unsent_.erase(waiter_);  // the waiter's record goes, so an unsent version of it must never land later
        if (!remove_retrying(waiter_)) pending_.push_back(waiter_);
        waiter_.clear();
        if (slot < 0) return;  // ran without a slot: there is no holder to name
        holder_record_ = base_;
        holder_record_.kind = "holder";
        holder_record_.slot = slot;
        holder_record_.acquired_at = utc_now();
        if (slot > 0) holder_record_.min_guest_mb = min_guest_mb_;  // the shim waits for guest memory past slot 0 only
        holder_ = dir_ / widen(dm::slot_file(slot));
        write(holder_, holder_record_);
    }
    // Called holding the slot, once the command has started.
    void started() {
        std::lock_guard<std::mutex> lock(mu_);
        if (closed_ || holder_.empty()) return;
        holder_record_.command_started_at = utc_now();
        write(holder_, holder_record_);
    }
    // Called before the mutex is released, so a successor's record is never the one removed.
    void done() {
        {
            std::lock_guard<std::mutex> lock(mu_);
            if (closed_) return;
            closed_ = true;
            unsent_.clear();
            remove_retrying(waiter_);
            remove_retrying(holder_);
            for (const auto& path : pending_) remove_retrying(path);
            waiter_.clear();
            holder_.clear();
            pending_.clear();
        }
        wake_.notify_all();
        if (retry_.joinable() && retry_.get_id() != std::this_thread::get_id()) retry_.join();
    }
    ~Records() { done(); }

  private:
    void write(const fs::path& path, const dm::SlotRecord& r) {
        std::error_code ec;
        fs::create_directories(dir_, ec);
        std::string text = dm::record_json(r);
        fs::path temp = path;
        temp += L"." + std::to_wstring(GetCurrentProcessId()) + L".tmp";
        bool ok = !ec;
        if (ok) {
            std::ofstream out(temp, std::ios::binary | std::ios::trunc);
            out.write(text.data(), static_cast<std::streamsize>(text.size()));
            ok = static_cast<bool>(out.flush());
        }
        // A reader (status, the panel, a test) can hold the record open for a moment, which makes the replace fail with
        // a sharing violation or access denied: it is retried for up to a second, since a record is rewritten only when
        // what it says changes, so one lost replace would leave it saying the old thing for good.
        for (int attempt = 0; ok; ++attempt) {
            if (MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) break;
            DWORD error = GetLastError();
            if ((error != ERROR_SHARING_VIOLATION && error != ERROR_ACCESS_DENIED) || attempt == 20) ok = false;
            else Sleep(50);
        }
        if (ok) {
            unsent_.erase(path);
            return;
        }
        {
            DWORD error = ec ? static_cast<DWORD>(ec.value()) : GetLastError();
            fs::remove(temp, ec);
            // Kept and retried until it lands or the job ends (completion review): a record is rewritten only when what
            // it says changes, so a write lost to a longer lock or a full disk would otherwise leave it wrong for good.
            unsent_[path] = r;
            if (!retry_.joinable()) retry_ = std::thread([this] { retry_unsent(); });
            if (!warned_) {
                std::cerr << "[bosun] can't publish this job's slot record (" << narrow(path.parent_path().wstring()) << ", error " << error
                          << "): the queue works, but status can't name it\n";
                warned_ = true;
            }
        }
    }
    // Every half second, tries again each record that couldn't be written, newest version only, until the job ends.
    void retry_unsent() {
        std::unique_lock<std::mutex> lock(mu_);
        while (!closed_) {
            wake_.wait_for(lock, std::chrono::milliseconds(500));
            if (closed_) break;
            auto unsent = unsent_;
            for (const auto& [path, record] : unsent) write(path, record);
        }
    }
    // Removes the file, retrying for up to half a second while another process has it open; true once it is gone.
    static bool remove_retrying(const fs::path& path) {
        if (path.empty()) return true;
        for (int attempt = 0;; ++attempt) {
            std::error_code ec;
            fs::remove(path, ec);
            if (!ec || !fs::exists(path, ec)) return true;
            if (attempt == 10) return false;
            Sleep(50);
        }
    }
    fs::path dir_, waiter_, holder_;
    std::vector<fs::path> pending_;  // removals that failed and are retried when the job ends
    dm::SlotRecord base_, holder_record_;
    uint64_t min_avail_mb_ = 0, max_wait_s_ = 0;
    std::optional<uint64_t> min_guest_mb_;
    bool warned_ = false;
    bool closed_ = false;
    std::mutex mu_;
    std::map<fs::path, dm::SlotRecord> unsent_;  // the newest version of each record that couldn't be written
    std::condition_variable wake_;
    std::thread retry_;  // started at the first write that fails
};

// --- The job history log (D01 T01 §2) ---

double seconds_since(std::chrono::steady_clock::time_point from, std::chrono::steady_clock::time_point to) {
    return std::chrono::duration<double>(to - from).count();
}

// Appends the job's line to %LOCALAPPDATA%\Bosun\logs\jobs-<UTC date>.jsonl as one write to a handle opened for
// appending, so lines from jobs ending at once never interleave. A line that can't be written is said once, and changes
// nothing else.
void append_job_line(const dm::JobLine& j) {
    fs::path dir = fs::path(env(L"LOCALAPPDATA")) / L"Bosun" / L"logs";
    std::error_code ec;
    fs::create_directories(dir, ec);
    fs::path file = dir / (L"jobs-" + widen(j.ended_at.substr(0, 10)) + L".jsonl");
    std::string line = dm::job_line_json(j) + "\n";
    HANDLE h = CreateFileW(file.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    DWORD written = 0;
    bool ok = h != INVALID_HANDLE_VALUE && WriteFile(h, line.data(), static_cast<DWORD>(line.size()), &written, nullptr) && written == line.size();
    DWORD error = ok ? 0 : GetLastError();
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    if (!ok)
        std::cerr << "[bosun] can't add this job to the job log (" << narrow(file.wstring()) << ", error " << error
                  << "): the job ran as it would, but the Queue page won't list it\n";
}

// The line for a job of this bosun, from its Records identity and the times it kept.
dm::JobLine job_line(const Options& o, const std::string& started_at, int slot, double waited, double ran, std::optional<int64_t> code) {
    dm::JobLine j;
    j.started_at = started_at;
    int64_t ended_ms = epoch_ms();
    j.ended_at = dm::utc_stamp(ended_ms / 1000);  // the same instant as ended_ms, so the two never straddle a second
    j.ended_ms = ended_ms;
    j.ran_ms = static_cast<int64_t>(std::llround(ran * 1000));
    j.pid = static_cast<int64_t>(GetCurrentProcessId());
    j.command = job_command(o);
    j.repo = job_repo(o);
    j.side = o.side.empty() ? "windows" : narrow(o.side);
    j.ns = narrow(o.ns);
    if (slot >= 0) j.slot = slot;
    j.waited_s = waited;
    j.ran_s = ran;
    j.exit_code = code;
    j.timed_out_into_queue = slot < 0;
    return j;
}

// A job cancelled while it waits for a slot (Ctrl+C, Ctrl+Break, its console closing) still leaves its line: no slot, all
// of its time waiting, none running, and the exit code Windows gives an interrupted process. The phase decides once
// whether the slot or the cancel came first, so the line is never written for a job that went on to run.
struct WaitCancel {
    std::atomic<int> phase{0};  // 0 waiting, 1 past the wait, 2 cancelled
    const Options* o = nullptr;
    Records* records = nullptr;
    std::string started_at;
    std::chrono::steady_clock::time_point took;
};
WaitCancel g_wait;

BOOL WINAPI cancel_wait(DWORD) {
    int waiting = 0;
    if (!g_wait.phase.compare_exchange_strong(waiting, 2)) return TRUE;  // past the wait: the command takes the signal
    g_wait.records->done();
    double waited = seconds_since(g_wait.took, std::chrono::steady_clock::now());
    dm::JobLine j = job_line(*g_wait.o, g_wait.started_at, -1, waited, 0, static_cast<int32_t>(STATUS_CONTROL_C_EXIT));
    j.timed_out_into_queue = false;  // no slot because it was cancelled, not because the wait ran out
    append_job_line(j);
    ExitProcess(STATUS_CONTROL_C_EXIT);
}

// Whether a process runs, and since when; access denied is "can't tell", never "gone".
dm::ProbeAnswer probe_process(int64_t pid) {
    if (pid <= 0 || pid > UINT32_MAX) return {dm::ProbeAnswer::Gone, 0};
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(pid));
    if (!h) return {GetLastError() == ERROR_INVALID_PARAMETER ? dm::ProbeAnswer::Gone : dm::ProbeAnswer::CantTell, 0};
    DWORD code = 0;
    FILETIME created{}, exited{}, kernel{}, user{};
    dm::ProbeAnswer answer{dm::ProbeAnswer::CantTell, 0};
    if (GetExitCodeProcess(h, &code) && code != STILL_ACTIVE) answer = {dm::ProbeAnswer::Gone, 0};
    else if (GetProcessTimes(h, &created, &exited, &kernel, &user))
        answer = {dm::ProbeAnswer::Running, (uint64_t{created.dwHighDateTime} << 32) | created.dwLowDateTime};
    CloseHandle(h);
    return answer;
}

// What a reader's pass found: whether the folder could be read at all (so an unreadable folder is never taken for an
// empty queue), how many record files it couldn't read or parse, and every record it parsed and kept, with its state.
struct RecordsPass {
    bool readable = true;
    int skipped = 0;
    std::vector<dm::SeenRecord> seen;
};

// A reader's pass over the records: removes stale and superseded waiters only, and returns the rest.
RecordsPass sweep_records(const Options& o) {
    RecordsPass pass;
    std::error_code ec;
    fs::path dir = records_dir(o);
    if (!fs::exists(dir, ec)) {
        pass.readable = !ec;
        return pass;
    }
    fs::directory_iterator it(dir, ec), end;
    if (ec) {
        pass.readable = false;
        return pass;
    }
    std::vector<std::pair<fs::path, dm::SeenRecord>> found;
    // An error while advancing can also leave the iterator at its end, ending the loop before the check inside it, so
    // it is checked after the loop too: what was read is kept, and the pass says it is incomplete (completion review).
    for (; !ec && it != end; it.increment(ec)) {
        const fs::path& path = it->path();
        if (path.extension() != L".json") continue;
        std::string text;
        {
            // Closed before any removal below: Windows won't delete a file this process still has open.
            std::ifstream in(path, std::ios::binary);
            text.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
        }
        auto record = dm::parse_record(text);
        if (!record) {  // half written, held open or not one of ours: a writer replaces it, a reader leaves it
            ++pass.skipped;
            continue;
        }
        found.push_back({path, {*record, dm::record_state(*record, probe_process)}});
    }
    if (ec) pass.readable = false;
    std::vector<dm::SlotRecord> records;
    for (const auto& [path, seen] : found) records.push_back(seen.record);
    for (const auto& [path, seen] : found) {
        if (dm::reader_removes(seen.record, seen.state, records)) {
            std::error_code gone;
            fs::remove(path, gone);
        } else {
            pass.seen.push_back(seen);
        }
    }
    return pass;
}

BOOL WINAPI ignore_ctrl(DWORD) { return TRUE; }  // The child receives Ctrl+C and decides; the parent outlives it.

// Finds the program as cmd.exe would, trying each PATHEXT extension.
std::wstring resolve_program(const std::wstring& name) {
    wchar_t buf[MAX_PATH * 4];
    std::vector<std::wstring> exts{L""};
    std::wstring pathext = env(L"PATHEXT");
    if (pathext.empty()) pathext = L".COM;.EXE;.BAT;.CMD";
    std::wstringstream ss(pathext);
    std::wstring e;
    while (std::getline(ss, e, L';')) exts.push_back(e);
    for (const auto& ext : exts) {
        if (ext.empty() && fs::path(name).extension().empty()) continue;
        DWORD n = SearchPathW(nullptr, name.c_str(), ext.empty() ? nullptr : ext.c_str(), MAX_PATH * 4, buf, nullptr);
        if (n && n < MAX_PATH * 4) return std::wstring(buf, n);
    }
    return {};
}

bool is_batch(const std::wstring& path) {
    std::wstring ext = fs::path(path).extension().wstring();
    for (auto& c : ext) c = static_cast<wchar_t>(towlower(c));
    return ext == L".cmd" || ext == L".bat";
}

// `on_start` runs once the command is running, so its holder record can say since when.
int run_child(const Options& o, const std::function<void()>& on_start = {}) {
    std::wstring program = resolve_program(o.command[0]);
    if (program.empty()) {
        std::wcerr << L"[bosun] command not found: " << o.command[0] << L"\n";
        return 127;
    }
    std::vector<std::wstring> rest(o.command.begin() + 1, o.command.end());
    bool batch = is_batch(program);
    auto built = dm::build_command_line(program, rest, batch);
    if (!built) {
        std::cerr << "[bosun] a batch file cannot safely receive an argument containing a line break\n";
        return 64;
    }
    std::wstring line = *built;
    std::wstring image = batch ? env(L"ComSpec") : program;

    // Ignored before the child exists, so Ctrl+C cannot strand a suspended child.
    SetConsoleCtrlHandler(ignore_ctrl, TRUE);

    // Closing the job kills the tree, so a slot is never freed while an orphaned build still runs.
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info{};
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info, sizeof(info))) {
        std::cerr << "[bosun] cannot create a job object, error " << GetLastError() << "\n";
        return 70;
    }

    STARTUPINFOW si{};

    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(image.c_str(), line.data(), nullptr, nullptr, TRUE, CREATE_SUSPENDED, nullptr, nullptr, &si, &pi)) {
        std::cerr << "[bosun] cannot start command, error " << GetLastError() << "\n";
        CloseHandle(job);
        return 126;
    }
    if (!AssignProcessToJobObject(job, pi.hProcess)) {
        std::cerr << "[bosun] cannot contain the command in a job, error " << GetLastError() << "\n";
        TerminateProcess(pi.hProcess, 70);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        CloseHandle(job);
        return 70;
    }
    ResumeThread(pi.hThread);
    if (on_start) on_start();
    // The job counts what every process in the tree writes, a child the command left behind included, so a runaway is
    // stopped wherever in the tree it is, before it fills the drive. Windows keeps no per-job count of disk writes
    // alone: this one includes bytes written to pipes, so the budget is generous enough for heavy pipelines too.
    bool over = false;
    while (WaitForSingleObject(pi.hProcess, 1000) == WAIT_TIMEOUT) {
        JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION io{};
        if (QueryInformationJobObject(job, JobObjectBasicAndIoAccountingInformation, &io, sizeof(io), nullptr) &&
            dm::over_write_budget(io.IoInfo.WriteTransferCount, o.max_write_gb)) {
            over = true;
            TerminateJobObject(job, 74);
            WaitForSingleObject(pi.hProcess, INFINITE);
            break;
        }
    }
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    if (over) {
        std::cerr << "[bosun] stopped the command and everything it started: together they wrote more than "
                  << o.max_write_gb << " GB to files and pipes (--max-write-gb). Something in it writes without a limit.\n";
        code = 74;
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    CloseHandle(job);
    return static_cast<int>(code);
}

int cmd_run(const Options& o) {
    if (o.command.empty()) {
        std::cerr << "usage: bosun run [options] -- command [args]\n";
        return 64;
    }
    if (!env(L"BOSUN_SLOT").empty()) return run_child(o);  // Already inside a slot: the outer job's record and line stand.
    std::string started_at = utc_now();
    auto took = std::chrono::steady_clock::now();
    Records records(o);
    g_wait.o = &o;
    g_wait.records = &records;
    g_wait.started_at = started_at;
    g_wait.took = took;
    SetConsoleCtrlHandler(cancel_wait, TRUE);
    records.waiting();
    Slot slot = acquire_slot(o, [&](const char* why) { records.waiting(why); });
    int waiting = 0;
    if (!g_wait.phase.compare_exchange_strong(waiting, 1)) Sleep(INFINITE);  // cancelled meanwhile: its handler exits
    records.holding(slot.index);
    auto started = std::chrono::steady_clock::now();
    SetEnvironmentVariableW(L"BOSUN_SLOT", std::to_wstring(slot.index).c_str());
    // WSLENV carries the marker into WSL, so a nested run on the other side also passes through.
    std::wstring wslenv = env(L"WSLENV");
    SetEnvironmentVariableW(L"WSLENV", (wslenv.empty() ? L"BOSUN_SLOT" : wslenv + L":BOSUN_SLOT").c_str());
    int code = run_child(o, [&] { records.started(); });
    auto ended = std::chrono::steady_clock::now();
    records.done();  // before the release, so the next holder's record is never the one removed
    if (slot.handle) ReleaseMutex(slot.handle);
    append_job_line(job_line(o, started_at, slot.index, seconds_since(took, started), seconds_since(started, ended), code));
    return code;
}

// Holds a slot for the WSL shim until stdin closes, which happens whenever the shim exits or dies.
int cmd_acquire(const Options& o) {
    Options job = o;
    if (job.side.empty()) job.side = L"wsl";  // the WSL shim is acquire's caller
    std::string started_at = utc_now();
    auto took = std::chrono::steady_clock::now();
    // The time between the shim beginning and this process starting is waiting too; bounded at a minute, so a clock
    // that jumped between the two sides can never make a wait up.
    double interop_lag_s = 0;
    if (o.queued_at_ms) {
        int64_t lag = epoch_ms() - static_cast<int64_t>(*o.queued_at_ms);
        if (lag > 0 && lag < 60000) interop_lag_s = static_cast<double>(lag) / 1000;
    }
    Records records(job);
    records.waiting();
    Slot slot = acquire_slot(o, [&](const char* why) { records.waiting(why); });
    records.holding(slot.index);
    std::cout << "acquired " << slot.index << std::endl;
    // The shim says "start" as its command starts (after any wait for guest memory) and "exit <code>" once it ends; the
    // pipe closing with no code means the shim went away.
    auto started = std::chrono::steady_clock::now();
    bool told_start = false;
    std::optional<int64_t> code;
    std::string pending;
    char buf[256];
    DWORD got = 0;
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    while (ReadFile(in, buf, sizeof(buf), &got, nullptr) && got) {
        pending.append(buf, got);
        for (size_t end; (end = pending.find('\n')) != std::string::npos; pending.erase(0, end + 1)) {
            std::string line = pending.substr(0, end);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line == "start" && !told_start) {
                started = std::chrono::steady_clock::now();
                told_start = true;
                records.started();
            } else if (line.rfind("exit ", 0) == 0 && line.size() > 5 && line.size() < 25) {
                char* tail = nullptr;
                long long value = std::strtoll(line.c_str() + 5, &tail, 10);
                if (tail && *tail == '\0') code = value;
            }
        }
    }
    auto ended = std::chrono::steady_clock::now();
    if (!told_start) started = ended;  // the command never started: all of it was waiting, none of it running
    records.done();
    if (slot.handle) ReleaseMutex(slot.handle);
    append_job_line(job_line(job, started_at, slot.index, seconds_since(took, started) + interop_lag_s, seconds_since(started, ended), code));
    return 0;
}

int cmd_status(const Options& o) {
    // Stale waiters go; holders' records are only ever their holders' to change. A folder that can't be read is said,
    // never shown as an empty queue.
    RecordsPass pass = sweep_records(o);
    int busy = 0;
    std::vector<std::optional<bool>> held;
    // A slot above a lowered limit is still probed while a holder's record names it, so its job never vanishes.
    const int probed = dm::slots_to_probe(o.slots, pass.seen);
    for (int i = 0; i < probed; ++i) {
        HANDLE h = open_slot(o.ns, i);
        if (!h) {  // can't be told; the text count has always treated it as free
            held.push_back(std::nullopt);
            continue;
        }
        DWORD w = WaitForSingleObject(h, 0);
        if (w == WAIT_OBJECT_0 || w == WAIT_ABANDONED) {
            ReleaseMutex(h);
            held.push_back(false);
        } else if (w == WAIT_TIMEOUT) {
            ++busy;
            held.push_back(true);
        } else {
            ++busy;
            held.push_back(std::nullopt);
        }
        CloseHandle(h);
    }
    MEMORYSTATUSEX m{};
    m.dwLength = sizeof(m);
    bool measured = GlobalMemoryStatusEx(&m) != 0;
    HANDLE logger = OpenMutexW(SYNCHRONIZE, FALSE, L"Local\\Bosun.Logger");
    DWORD logger_error = logger ? 0 : GetLastError();
    std::string logger_says = dm::logger_state(logger != nullptr, logger_error);
    if (logger) CloseHandle(logger);
    if (o.json) {
        dm::StatusInput in;
        in.ns = narrow(o.ns);
        in.at = utc_now();
        in.busy = held;
        in.limit = o.slots;
        in.records = pass.seen;
        in.records_readable = pass.readable;
        in.records_skipped = pass.skipped;
        in.records_dir = narrow(records_dir(o).wstring());
        if (measured) {
            in.available_mb = m.ullAvailPhys / (1024 * 1024);
            in.total_mb = m.ullTotalPhys / (1024 * 1024);
        }
        in.min_avail_mb = o.min_avail_mb;
        if (o.guest_avail_mb) in.guest = dm::GuestFigures{*o.guest_avail_mb, *o.guest_total_mb, o.min_guest_mb.value_or(0)};
        in.windows_logger = logger_says;
        if (!o.wsl_logger.empty()) in.wsl_logger = o.wsl_logger == L"running" ? "running" : o.wsl_logger == L"stopped" ? "not running" : "unknown";
        std::cout << dm::status_json(in) << "\n";
        return 0;
    }
    std::cout << "slots busy: " << busy << " of " << o.slots << "\n";
    std::cout << "available: " << available_mb() << " of " << m.ullTotalPhys / (1024 * 1024) << " MB\n";
    std::cout << "logger: " << logger_says;
    if (logger_says == "unknown") std::cout << " (error " << logger_error << " opening its mutex)";
    std::cout << "\n";
    if (!pass.readable) std::cout << "records: unavailable (" << narrow(records_dir(o).wstring()) << " can't be read), so who holds and waits can't be named\n";
    return 0;
}

fs::path log_dir() {
    fs::path p = fs::path(env(L"LOCALAPPDATA")) / L"Bosun" / L"logs";
    std::error_code ec;  // a folder that can't be made is reported where it is written to, never thrown here
    fs::create_directories(p, ec);
    return p;
}

// UTC on both sides, so samples still join if Windows and WSL disagree on the time zone.
std::string now_iso() {
    SYSTEMTIME t;
    GetSystemTime(&t);
    char buf[32];
    snprintf(buf, sizeof(buf), "%04d-%02d-%02dT%02d:%02d:%02d", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return buf;
}

std::string date_days_ago(int days) {
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER u{};
    u.LowPart = ft.dwLowDateTime;
    u.HighPart = ft.dwHighDateTime;
    u.QuadPart -= static_cast<uint64_t>(days) * 864000000000ULL;
    ft.dwLowDateTime = u.LowPart;
    ft.dwHighDateTime = u.HighPart;
    SYSTEMTIME st;
    FileTimeToSystemTime(&ft, &st);
    char buf[16];
    snprintf(buf, sizeof(buf), "%04d-%02d-%02d", st.wYear, st.wMonth, st.wDay);
    return buf;
}

// The date from a name such as windows-2026-11-07.csv, or empty for any other file.
// The date at the end of a log's name: the memory logs (.csv) and the job logs (.jsonl) alike.
std::string file_date(const fs::path& p) {
    std::string stem = narrow(p.stem().wstring());
    bool log = p.extension() == L".csv" || p.extension() == L".jsonl";
    return stem.size() >= 10 && log ? stem.substr(stem.size() - 10) : std::string{};
}

struct Proc {
    ULONG_PTR pid = 0, ppid = 0;
    std::string name;
    uint64_t private_bytes = 0, ws_bytes = 0;
};

// The one API that reports memory for every process, including ones this user cannot open.
// `ok`, when given, is false when the list could not be taken at all: an empty list is then not "no processes".
std::vector<Proc> snapshot_processes(bool* ok = nullptr) {
    using QueryFn = NTSTATUS(NTAPI*)(ULONG, PVOID, ULONG, PULONG);
    static auto query = reinterpret_cast<QueryFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation"));
    std::vector<BYTE> buf(1 << 20);
    ULONG needed = 0;
    NTSTATUS st;
    while ((st = query(SystemProcessInformation, buf.data(), static_cast<ULONG>(buf.size()), &needed)) == static_cast<NTSTATUS>(0xC0000004L))
        buf.resize(std::max<size_t>(buf.size() * 2, needed + 65536));
    std::vector<Proc> out;
    if (ok) *ok = st >= 0;
    if (st < 0) return out;
    auto* p = reinterpret_cast<SYSTEM_PROCESS_INFORMATION*>(buf.data());
    for (;;) {
        if (p->UniqueProcessId) {
            Proc r;
            r.pid = reinterpret_cast<ULONG_PTR>(p->UniqueProcessId);
            r.ppid = reinterpret_cast<ULONG_PTR>(p->Reserved2);  // InheritedFromUniqueProcessId; winternl.h leaves it unnamed.
            r.name = narrow(std::wstring(p->ImageName.Buffer, p->ImageName.Length / sizeof(wchar_t)));
            r.private_bytes = p->PrivatePageCount;
            r.ws_bytes = p->WorkingSetSize;
            out.push_back(std::move(r));
        }
        if (!p->NextEntryOffset) break;
        p = reinterpret_cast<SYSTEM_PROCESS_INFORMATION*>(reinterpret_cast<BYTE*>(p) + p->NextEntryOffset);
    }
    return out;
}

// WORKAROUND: Windows has no public API for another process's working directory.
// This reads it from the process parameters, read-only, at offsets stable on x64 Windows.
std::wstring process_cwd(ULONG_PTR pid) {
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, FALSE, static_cast<DWORD>(pid));
    if (!h) return {};
    std::wstring result;
    PROCESS_BASIC_INFORMATION pbi{};
    using QueryFn = NTSTATUS(NTAPI*)(HANDLE, PROCESSINFOCLASS, PVOID, ULONG, PULONG);
    static auto query = reinterpret_cast<QueryFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationProcess"));
    if (query(h, ProcessBasicInformation, &pbi, sizeof(pbi), nullptr) >= 0 && pbi.PebBaseAddress) {
        BYTE* params = nullptr;
        UNICODE_STRING dir{};
        BYTE* peb = reinterpret_cast<BYTE*>(pbi.PebBaseAddress);
        if (ReadProcessMemory(h, peb + 0x20, &params, sizeof(params), nullptr) && params &&
            ReadProcessMemory(h, params + 0x38, &dir, sizeof(dir), nullptr) && dir.Buffer && dir.Length && dir.Length < 8192) {
            std::wstring w(dir.Length / sizeof(wchar_t), L'\0');
            if (ReadProcessMemory(h, dir.Buffer, w.data(), dir.Length, nullptr)) result = std::move(w);
        }
    }
    CloseHandle(h);
    return result;
}

std::string repo_for_dir(const std::wstring& dir, std::unordered_map<std::wstring, std::string>& cache) {
    auto it = cache.find(dir);
    if (it != cache.end()) return it->second;
    std::string repo;
    if (dir.size() >= 2 && dir[1] == L':') {
        auto found = dm::repo_from_path(narrow(dir), [](const std::string& d) {
            std::error_code ec;
            std::u8string utf8(reinterpret_cast<const char8_t*>(d.data()), d.size());
            return fs::exists(fs::path(utf8) / u8".git", ec);
        });
        if (found) {
            // A working directory can carry any letter case, so use the name stored on disk.
            std::error_code ec;
            std::u8string utf8(reinterpret_cast<const char8_t*>(found->data()), found->size());
            fs::path real = fs::canonical(fs::path(utf8), ec);
            repo = ec ? *found : narrow(real.generic_wstring());
        }
    }
    cache[dir] = repo;
    return repo;
}

// Replaces current-<side>.json (D01 T01 §4): a temporary file in the folder moved over the old one, so a reader sees
// the old snapshot or the new one, never part of either. The move is retried for up to a second while a reader holds
// the file open; a snapshot that still can't be replaced keeps the old one, removes its temporary file, is said once
// per logger run and is written again at the next sample.
// Renames `from` over `to` with POSIX semantics (Windows 10 1809 and later, on NTFS): the replace succeeds even while a
// reader holds the old file open sharing delete, and that reader goes on reading the old file. MoveFileExW refuses
// that case with access denied, so a panel reading the snapshot would block every replace.
bool posix_replace(const fs::path& from, const fs::path& to) {
    HANDLE h = CreateFileW(from.c_str(), DELETE | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    std::wstring target = to.wstring();
    std::vector<BYTE> buf(sizeof(FILE_RENAME_INFO) + target.size() * sizeof(wchar_t), 0);
    auto* info = reinterpret_cast<FILE_RENAME_INFO*>(buf.data());
    // The first member is a union of ReplaceIfExists and, for FileRenameInfoEx, Flags: written as the DWORD so the
    // code doesn't depend on the SDK naming the union's second member.
    constexpr DWORD kReplaceIfExists = 0x1, kPosixSemantics = 0x2;  // FILE_RENAME_FLAG_*
    *reinterpret_cast<DWORD*>(buf.data()) = kReplaceIfExists | kPosixSemantics;
    info->RootDirectory = nullptr;
    info->FileNameLength = static_cast<DWORD>(target.size() * sizeof(wchar_t));
    std::memcpy(info->FileName, target.data(), info->FileNameLength);
    constexpr auto kFileRenameInfoEx = static_cast<FILE_INFO_BY_HANDLE_CLASS>(22);
    BOOL ok = SetFileInformationByHandle(h, kFileRenameInfoEx, info, static_cast<DWORD>(buf.size()));
    DWORD error = ok ? 0 : GetLastError();
    CloseHandle(h);
    SetLastError(error);
    return ok != 0;
}

void publish_snapshot(const fs::path& path, const std::string& text) {
    static bool warned = false;
    fs::path temp = path;
    temp += L"." + std::to_wstring(GetCurrentProcessId()) + L".tmp";
    bool ok = false;
    DWORD error = 0;
    {
        std::ofstream out(temp, std::ios::binary | std::ios::trunc);
        out.write(text.data(), static_cast<std::streamsize>(text.size()));
        ok = static_cast<bool>(out.flush());
        if (!ok) error = GetLastError();
    }
    for (int attempt = 0; ok; ++attempt) {
        if (posix_replace(temp, path)) return;
        // Where POSIX renames aren't supported (an older Windows, a FAT or network drive) the classic replace still is.
        if (MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) return;
        error = GetLastError();
        if ((error != ERROR_SHARING_VIOLATION && error != ERROR_ACCESS_DENIED) || attempt == 20) ok = false;
        else Sleep(50);
    }
    std::error_code ec;
    fs::remove(temp, ec);
    if (!warned) {
        std::cerr << "[bosun] can't replace the live snapshot " << narrow(path.wstring()) << " (error " << error
                  << "): the previous one stays, and the next sample tries again\n";
        warned = true;
    }
}

void write_sample(PDH_HCOUNTER counter, std::unordered_map<std::wstring, std::string>& cache, int interval_s) {
    auto procs = snapshot_processes();
    std::unordered_map<ULONG_PTR, size_t> by_pid;
    std::unordered_map<ULONG_PTR, std::string> own_repo;
    for (size_t i = 0; i < procs.size(); ++i) {
        by_pid[procs[i].pid] = i;
        own_repo[procs[i].pid] = repo_for_dir(process_cwd(procs[i].pid), cache);
    }
    struct Group { int64_t count = 0; uint64_t priv = 0, ws = 0; };
    std::map<std::string, Group> repos, names;
    for (const auto& p : procs) {
        std::string repo;
        ULONG_PTR cur = p.pid;
        for (int depth = 0; depth < 16 && repo.empty(); ++depth) {
            repo = own_repo[cur];
            auto it = by_pid.find(cur);
            if (it == by_pid.end()) break;
            cur = procs[it->second].ppid;
        }
        if (!repo.empty()) {
            auto& g = repos[repo];
            ++g.count; g.priv += p.private_bytes; g.ws += p.ws_bytes;
        }
        auto& n = names[p.name];
        ++n.count; n.priv += p.private_bytes; n.ws += p.ws_bytes;
    }

    std::string ts = now_iso();
    std::ostringstream o;
    auto row = [&](const char* kind, const std::string& name, int64_t count, int64_t priv, int64_t rss) {
        o << ts << ",win," << kind << "," << dm::csv_name(name) << "," << count << "," << priv << "," << rss << "\n";
    };
    constexpr uint64_t MB = 1024 * 1024;
    // The live snapshot (D01 T01 §4) holds the same figures as this sample's rows; what wasn't measured is null there
    // and has no row here, never a 0.
    dm::Snapshot snap;
    snap.side = "windows";
    snap.at = ts + "Z";
    snap.interval_s = interval_s;
    MEMORYSTATUSEX m{};
    m.dwLength = sizeof(m);
    std::optional<int64_t> avail, total, commit, limit, pages;
    if (GlobalMemoryStatusEx(&m)) {
        avail = static_cast<int64_t>(m.ullAvailPhys / MB);
        total = static_cast<int64_t>(m.ullTotalPhys / MB);
        commit = static_cast<int64_t>((m.ullTotalPageFile - m.ullAvailPageFile) / MB);
        limit = static_cast<int64_t>(m.ullTotalPageFile / MB);
        row("sys", "available_mb", 0, *avail, 0);
        row("sys", "commit_mb", 0, *commit, 0);
        row("sys", "commit_limit_mb", 0, *limit, 0);
        // The installed memory, so the report can tell an upgrade within its period (D03 T01 §6).
        row("sys", "total_mb", 0, *total, 0);
    }
    PDH_FMT_COUNTERVALUE value{};
    if (PdhGetFormattedCounterValue(counter, PDH_FMT_LARGE, nullptr, &value) == ERROR_SUCCESS) {
        pages = value.largeValue;
        row("sys", "pages_in_per_sec", 0, value.largeValue, 0);
    }
    snap.memory = {{"available_mb", avail}, {"total_mb", total}, {"commit_mb", commit}, {"commit_limit_mb", limit}, {"pages_in_per_sec", pages}};
    for (const auto& [name, g] : repos) {
        row("repo", name, g.count, g.priv / MB, g.ws / MB);
        snap.repos.push_back({name, g.count, static_cast<int64_t>(g.priv / MB), static_cast<int64_t>(g.ws / MB)});
    }
    std::vector<std::pair<std::string, Group>> sorted(names.begin(), names.end());
    std::sort(sorted.begin(), sorted.end(), [](const auto& a, const auto& b) { return a.second.priv > b.second.priv; });
    for (size_t i = 0; i < sorted.size() && i < 15; ++i) {
        row("proc", sorted[i].first, sorted[i].second.count, sorted[i].second.priv / MB, sorted[i].second.ws / MB);
        snap.processes.push_back({sorted[i].first, sorted[i].second.count, static_cast<int64_t>(sorted[i].second.priv / MB),
                                  static_cast<int64_t>(sorted[i].second.ws / MB)});
    }
    // The WSL VM as the host sees it, from this same process list: never a call into WSL, so it never starts the VM.
    dm::WslVm vm;
    for (const auto& [name, g] : names) {
        if (_stricmp(name.c_str(), "vmmemWSL") != 0 && _stricmp(name.c_str(), "vmmem") != 0) continue;
        vm.running = true;
        vm.processes += g.count;
        vm.private_mb += static_cast<int64_t>(g.priv / MB);
        vm.rss_mb += static_cast<int64_t>(g.ws / MB);
    }
    snap.wsl_vm = vm;

    fs::path file = log_dir() / (L"windows-" + std::wstring(ts.begin(), ts.begin() + 10) + L".csv");
    bool fresh = !fs::exists(file);
    std::ofstream f(file, std::ios::app | std::ios::binary);
    if (fresh) f << "ts,side,kind,name,count,private_mb,rss_mb\n";
    f << o.str();
    f.close();
    // A history row that couldn't be written (a full disk, a denied or held file) is said once per logger run and shown
    // in the snapshot, and sampling carries on, so the live view never hides that history is being lost (completion
    // review of D01 T01 §4).
    snap.history_written = !f.fail();
    static bool history_warned = false;
    if (!snap.history_written && !history_warned) {
        std::cerr << "[bosun] can't add this sample to " << narrow(file.wstring()) << " (error " << GetLastError()
                  << "): history is being lost, though sampling goes on\n";
        history_warned = true;
    }
    publish_snapshot(log_dir() / L"current-windows.json", dm::snapshot_json(snap));
}

void prune_logs(int keep_days) {
    std::string cutoff = date_days_ago(keep_days);
    std::error_code ec;
    for (const auto& e : fs::directory_iterator(log_dir(), ec)) {
        std::string d = file_date(e.path());
        if (!d.empty() && d < cutoff) fs::remove(e.path(), ec);
    }
}

// --- bosun instances (D03 T02 §1) ------------------------------------------------------------------------------------
// Every agent and editor on Windows, and every job holding a slot (both sides'), as dm::instances_json writes them.

std::optional<uint64_t> process_start_of(ULONG_PTR pid) {
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, static_cast<DWORD>(pid));
    if (!h) return std::nullopt;
    FILETIME created{}, exited{}, kernel{}, user{};
    std::optional<uint64_t> start;
    if (GetProcessTimes(h, &created, &exited, &kernel, &user)) start = (uint64_t{created.dwHighDateTime} << 32) | created.dwLowDateTime;
    CloseHandle(h);
    return start;
}

std::optional<std::string> utc_of_filetime(uint64_t value) {
    FILETIME ft{static_cast<DWORD>(value), static_cast<DWORD>(value >> 32)};
    SYSTEMTIME t{};
    if (!FileTimeToSystemTime(&ft, &t)) return std::nullopt;
    char buf[32];
    std::snprintf(buf, sizeof(buf), "%04u-%02u-%02uT%02u:%02u:%02uZ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return std::string(buf);
}

// A small file's text, or nothing with why. A file that went while it was read is `gone`, not a fault.
std::optional<std::string> read_small(const fs::path& path, std::string& why, bool& gone) {
    gone = false;
    std::ifstream in(path, std::ios::binary);
    if (!in) {
        std::error_code ec;
        gone = !fs::exists(path, ec) && !ec;
        why = narrow(path.wstring()) + " can't be read";
        return std::nullopt;
    }
    std::string text(64 * 1024 + 1, '\0');
    in.read(text.data(), static_cast<std::streamsize>(text.size()));
    text.resize(static_cast<size_t>(in.gcount()));
    if (text.size() > 64 * 1024) {
        why = narrow(path.wstring()) + " is larger than any such record";
        return std::nullopt;
    }
    return text;
}

// The JSON files in `dir`, or nothing when the folder exists and can't be listed. A missing folder is none.
std::optional<std::vector<fs::path>> json_files(const fs::path& dir, std::string& why) {
    std::vector<fs::path> out;
    std::error_code ec;
    if (!fs::exists(dir, ec)) {
        if (!ec) return out;
        why = narrow(dir.wstring()) + " can't be read (error " + std::to_string(ec.value()) + ")";
        return std::nullopt;
    }
    fs::directory_iterator it(dir, ec), end;
    if (ec) {
        why = narrow(dir.wstring()) + " can't be read (error " + std::to_string(ec.value()) + ")";
        return std::nullopt;
    }
    for (; it != end; it.increment(ec)) {
        if (ec) break;
        if (it->path().extension() == L".json") out.push_back(it->path());
    }
    if (ec) {
        why = narrow(dir.wstring()) + " can't be read (error " + std::to_string(ec.value()) + ")";
        return std::nullopt;
    }
    std::sort(out.begin(), out.end());
    return out;
}

// The program a command line runs, by its file name: what a job is called.
std::string job_name(const std::string& command) {
    std::string first;
    if (!command.empty() && command[0] == '"') {
        size_t end = command.find('"', 1);
        first = command.substr(1, end == std::string::npos ? std::string::npos : end - 1);
    } else {
        first = command.substr(0, command.find(' '));
    }
    size_t slash = first.find_last_of("/\\");
    return slash == std::string::npos ? first : first.substr(slash + 1);
}

int cmd_instances(const Options& o) {
    bool listed = false;
    auto procs = snapshot_processes(&listed);
    if (!listed) {
        std::cerr << "[bosun] the process list can't be taken (NtQuerySystemInformation failed), so no instance can be named\n";
        return 2;
    }
    dm::InstancesDoc doc;
    doc.side = "windows";
    doc.at = utc_now();

    std::vector<dm::ProcInfo> infos;
    infos.reserve(procs.size());
    for (const auto& p : procs) {
        dm::ProcInfo info;
        info.pid = static_cast<int64_t>(p.pid);
        info.ppid = static_cast<int64_t>(p.ppid);
        info.name = p.name;
        info.private_kb = static_cast<int64_t>(p.private_bytes / 1024);
        info.start = process_start_of(p.pid);
        infos.push_back(std::move(info));
    }
    auto parent = dm::parents_of(infos);
    auto agents = dm::instance_roots(infos, parent);

    // The jobs holding slots, from the queue's records: both sides', since the queue is one, kept here.
    RecordsPass pass = sweep_records(o);
    if (!pass.readable) doc.problems.push_back({"queue-records", narrow(records_dir(o).wstring()) + " can't be read, so no job can be named"});
    else if (pass.skipped) doc.problems.push_back({"queue-records", std::to_string(pass.skipped) + " of the queue's records couldn't be read"});
    std::vector<dm::SlotRecord> holders;
    for (const auto& seen : pass.seen)
        if (seen.record.kind == "holder" && seen.state != dm::RecordState::Stale) holders.push_back(seen.record);
    std::set<int64_t> roots = agents;
    for (const auto& h : holders) roots.insert(h.pid);
    auto memory = dm::owned_memory(infos, parent, roots);

    // Claude Code's session records, by process.
    std::map<int64_t, dm::SessionRecord> sessions;
    std::string why;
    fs::path session_dir = fs::path(env(L"USERPROFILE")) / L".claude" / L"sessions";
    if (auto files = json_files(session_dir, why)) {
        for (const auto& file : *files) {
            bool gone = false;
            auto text = read_small(file, why, gone);
            if (!text) {
                if (!gone) doc.problems.push_back({"sessions", why});
                continue;
            }
            auto record = dm::parse_session_record(*text);
            if (!record) doc.problems.push_back({"sessions", narrow(file.wstring()) + " is not a session record"});
            else sessions[record->pid] = *record;
        }
    } else {
        doc.problems.push_back({"sessions", why});
    }

    std::map<int64_t, const dm::ProcInfo*> by_pid;
    for (const auto& info : infos) by_pid[info.pid] = &info;
    std::unordered_map<std::wstring, std::string> repo_cache;
    std::map<std::string, std::vector<dm::ReviewMark>> marks_by_repo;  // read once per repo
    const double now_s = static_cast<double>(epoch_ms()) / 1000;
    auto marks_of = [&](const std::string& repo) -> const std::vector<dm::ReviewMark>& {
        auto found = marks_by_repo.find(repo);
        if (found != marks_by_repo.end()) return found->second;
        auto& marks = marks_by_repo[repo];
        std::string listing;
        fs::path dir = fs::path(widen(repo)) / L"build" / L"review-in-flight";
        auto files = json_files(dir, listing);
        if (!files) {
            doc.problems.push_back({"review-marks", listing});
            return marks;
        }
        for (const auto& file : *files) {
            bool gone = false;
            std::string unread;
            auto text = read_small(file, unread, gone);
            if (!text) {
                if (!gone) doc.problems.push_back({"review-marks", unread});
                continue;
            }
            auto obj = dm::parse_flat_object(*text);
            auto pid = obj ? obj->find("pid") : decltype(obj->find("pid")){};
            auto deadline = obj ? obj->find("deadline") : decltype(obj->find("deadline")){};
            if (!obj || pid == obj->end() || pid->second.kind != dm::FlatValue::Integer || pid->second.negative || deadline == obj->end() ||
                (deadline->second.kind != dm::FlatValue::Integer && deadline->second.kind != dm::FlatValue::Decimal)) {
                doc.problems.push_back({"review-marks", narrow(file.wstring()) + " is not a review mark"});
                continue;
            }
            dm::ReviewMark mark;
            mark.pid = pid->second.number;
            auto reviewer = obj->find("reviewer");
            mark.reviewer = reviewer != obj->end() && reviewer->second.kind == dm::FlatValue::String ? reviewer->second.text : "a reviewer";
            mark.live = deadline->second.decimal >= now_s && probe_process(mark.pid).what == dm::ProbeAnswer::Running;
            marks.push_back(mark);
        }
        return marks;
    };

    for (int64_t pid : agents) {
        const dm::ProcInfo& p = *by_pid[pid];
        dm::Instance i;
        i.side = "windows";
        i.kind = dm::instance_kind(p.name);
        i.name = p.name;
        i.pid = pid;
        std::wstring dir = process_cwd(static_cast<ULONG_PTR>(pid));
        if (dir.empty()) {
            ++doc.unreadable;  // another user's process, or one that went: its folder can't be read
        } else {
            if (dir.size() > 3 && (dir.back() == L'\\' || dir.back() == L'/')) dir.pop_back();
            i.cwd = narrow(dir);
            std::string repo = repo_for_dir(dir, repo_cache);
            if (!repo.empty()) i.repo = repo;
        }
        i.private_mb = memory[pid];
        if (p.start) i.started_at = utc_of_filetime(*p.start);
        const dm::ReviewMark* under = nullptr;
        if (i.repo) {
            const auto& marks = marks_of(*i.repo);
            std::set<int64_t> mark_pids;
            for (const auto& m : marks) mark_pids.insert(m.pid);
            int64_t found = dm::first_root(pid, parent, mark_pids);
            for (const auto& m : marks)
                if (found && m.pid == found && (!under || (m.live && !under->live))) under = &m;
        }
        auto session = sessions.find(pid);
        dm::instance_state(i, nullptr, under, session == sessions.end() ? nullptr : &session->second, p.start);
        doc.instances.push_back(std::move(i));
    }
    for (const auto& h : holders) {
        dm::Instance i;
        i.side = h.side;
        i.kind = "job";
        i.name = job_name(h.command);
        i.pid = h.pid;
        i.repo = h.repo;
        i.slot = h.slot;
        i.command = h.command;
        i.guest_pid = h.guest_pid;
        i.guest_distro = h.guest_distro;
        i.started_at = h.command_started_at;
        if (h.side == "windows") {
            // A Windows job's folder is its `bosun run`'s, read as an agent's is (round 2); a WSL job's is WSL's to say.
            i.private_mb = memory[h.pid];
            std::wstring dir = process_cwd(static_cast<ULONG_PTR>(h.pid));
            if (dir.empty()) {
                ++doc.unreadable;
            } else {
                if (dir.size() > 3 && (dir.back() == L'\\' || dir.back() == L'/')) dir.pop_back();
                i.cwd = narrow(dir);
            }
        }
        dm::instance_state(i, &h, nullptr, nullptr, std::nullopt);
        doc.instances.push_back(std::move(i));
    }
    std::sort(doc.instances.begin(), doc.instances.end(), [](const dm::Instance& a, const dm::Instance& b) {
        return std::tie(a.repo, a.kind, a.pid) < std::tie(b.repo, b.kind, b.pid);
    });
    std::cout << (o.json ? dm::instances_json(doc) + "\n" : dm::instances_text(doc));
    return 0;
}

int cmd_log(const Options& o) {
    HANDLE single = CreateMutexW(nullptr, TRUE, L"Local\\Bosun.Logger");
    if (!o.once && GetLastError() == ERROR_ALREADY_EXISTS) return 0;  // One logger per session.
    PDH_HQUERY q = nullptr;
    PDH_HCOUNTER c = nullptr;
    PdhOpenQueryW(nullptr, 0, &q);
    PdhAddEnglishCounterW(q, L"\\Memory\\Pages Input/sec", 0, &c);
    PdhCollectQueryData(q);
    if (o.once) Sleep(1000);  // A rate counter needs two readings.
    std::unordered_map<std::wstring, std::string> cache;
    int since_prune = INT_MAX / 2;
    for (;;) {
        PdhCollectQueryData(q);
        write_sample(c, cache, o.interval_s);
        if (o.once) {
            prune_logs(14);  // one sample still keeps the folder to its retention
            break;
        }
        since_prune += o.interval_s;
        if (since_prune >= 3600) {
            prune_logs(14);
            cache.clear();  // Repos get created and deleted during long runs.
            since_prune = 0;
        }
        Sleep(o.interval_s * 1000);
    }
    PdhCloseQuery(q);
    if (single) CloseHandle(single);
    return 0;
}

int64_t now_epoch_s() {
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    uint64_t t = (uint64_t{ft.dwHighDateTime} << 32) | ft.dwLowDateTime;
    return static_cast<int64_t>(t / 10000000ULL) - 11644473600LL;  // FILETIME counts 100 ns from 1601
}

// History over the last --days times 24 hours (D01 T01 §4): memory from the .csv logs, builds from the job logs, each
// sample filtered by its own time, and what couldn't be read said, so a partial history never reads as a whole one.
int cmd_report(const Options& o) {
    int64_t now = now_epoch_s();
    std::string since = dm::utc_stamp(now - int64_t{o.days} * 86400);  // YYYY-MM-DDTHH:MM:SSZ
    std::string since_row = since.substr(0, 19);                       // as the CSV rows write it
    // The day before too: the WSL OOM counter's baseline is its last sample before the period (D03 T01 §6).
    std::string since_date = dm::utc_stamp(now - int64_t{o.days} * 86400 - 86400).substr(0, 10);
    std::vector<dm::Row> rows;
    std::vector<dm::JobLine> jobs;
    dm::ReadStatus read;
    std::error_code ec;
    fs::path dir = log_dir();
    fs::directory_iterator it(dir, ec), end;
    if (ec) read.complete = false;
    for (; !ec && it != end; it.increment(ec)) {
        const fs::path& path = it->path();
        std::string d = file_date(path);
        if (d.empty() || d < since_date) continue;
        bool csv = path.extension() == L".csv";
        bool job_log = path.extension() == L".jsonl" && path.filename().wstring().rfind(L"jobs-", 0) == 0;
        if (!csv && !job_log) continue;
        ++read.files;
        std::ifstream f(path, std::ios::binary);
        if (!f) {
            read.complete = false;
            ++read.unreadable_files;
            continue;
        }
        std::string line;
        while (std::getline(f, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty()) continue;
            if (csv) {
                if (line.rfind("ts,", 0) == 0) continue;  // the header
                if (auto r = dm::parse_row(line)) rows.push_back(std::move(*r));
                else ++read.rejected_rows;
            } else if (auto j = dm::parse_job_line(line)) {
                jobs.push_back(std::move(*j));
            } else {
                ++read.rejected_rows;
            }
        }
        if (f.bad()) {
            read.complete = false;
            ++read.unreadable_files;
        }
    }
    if (ec) read.complete = false;  // a listing that failed part way ends the loop early
    dm::Report report = dm::aggregate(rows, o.report_low_mb, since_row);
    if (o.json) {
        std::cout << dm::report_json(report, dm::build_overlap(jobs, now - int64_t{o.days} * 86400), read, dm::utc_stamp(now), since, o.days, o.report_low_mb) << "\n";
        return 0;
    }
    std::cout << dm::render(report, o.report_low_mb);
    return 0;
}

}  // namespace


// --- One config file for queue limits (D01 T01 §5) ---

fs::path config_path() { return fs::path(env(L"LOCALAPPDATA")) / L"Bosun" / L"config.json"; }

struct LoadedConfig {
    bool exists = false, readable = true;
    dm::ConfigFile file;
};

LoadedConfig load_config() {
    LoadedConfig c;
    std::error_code ec;
    fs::path path = config_path();
    c.exists = fs::exists(path, ec);
    if (ec) {  // can't tell whether it is there (a folder denied, say): unreadable, so it is said and never overwritten
        c.exists = true;
        c.readable = false;
        return c;
    }
    if (!c.exists) return c;
    std::ifstream in(path, std::ios::binary);
    if (!in) {
        c.readable = false;
        return c;
    }
    // At most 64 KB is read (round 1): the four limits take a few hundred bytes, and a file of any size would otherwise
    // be loaded whole before every command, so a huge one is refused whole, as a damaged one is.
    constexpr auto kMaxConfig = static_cast<std::streamsize>(dm::kMaxConfigBytes);
    std::string text(static_cast<size_t>(kMaxConfig) + 1, '\0');
    in.read(text.data(), kMaxConfig + 1);
    if (in.bad()) {
        c.readable = false;
        return c;
    }
    text.resize(static_cast<size_t>(in.gcount()));
    if (static_cast<std::streamsize>(text.size()) > kMaxConfig) {
        c.file.valid = false;
        c.file.error = "larger than 64 KB, far past what its limits take";
        return c;
    }
    c.file = dm::parse_config(text);
    return c;
}

std::optional<std::string> env_text(const char* name) {
    std::wstring v = env(widen(name).c_str());
    if (v.empty()) return std::nullopt;
    return narrow(v);
}

std::optional<uint64_t> saved_value(const LoadedConfig& c, const char* key) {
    if (!c.exists || !c.readable || !c.file.valid) return std::nullopt;
    auto it = c.file.values.find(key);
    return it == c.file.values.end() ? std::nullopt : std::optional<uint64_t>(it->second);
}

// The four limits for this command: flag, then variable, then config.json, then default. What was ignored is said once
// on stderr, and the command runs on the fallbacks, so a damaged file never stops a build.
void apply_limits(Options& o) {
    LoadedConfig c = load_config();
    std::string where = narrow(config_path().wstring());
    std::map<std::string, dm::ResolvedLimit> resolved;
    for (const auto& spec : dm::limit_specs()) {
        auto flag = o.limit_flags.find(spec.key);
        dm::ResolvedLimit r = dm::resolve_limit(spec, flag == o.limit_flags.end() ? std::nullopt : std::optional<uint64_t>(flag->second),
                                                env_text(spec.env), saved_value(c, spec.key));
        if (!r.env_error.empty()) std::cerr << "[bosun] ignoring " << r.env_error << ", so " << spec.key << " is " << r.value << " from the " << r.source << "\n";
        std::string key = spec.key;
        if (key == "slots") o.slots = static_cast<int>(r.value);
        else if (key == "min_avail_mb") o.min_avail_mb = r.value;
        else if (key == "max_wait_s") o.max_wait_s = r.value;
        else if (key == "min_guest_mb") o.min_guest_mb = r.value;
        resolved[key] = r;
    }
    // Said after the limits are resolved, so each warning names what the command runs on instead (round 5).
    auto in_use = [&](const std::string& key) {
        const dm::ResolvedLimit& r = resolved.at(key);
        return key + " uses " + std::to_string(r.value) + " from the " + r.source;
    };
    std::string all;
    for (const auto& spec : dm::limit_specs()) all += (all.empty() ? "" : ", ") + in_use(spec.key);
    if (c.exists && !c.readable) std::cerr << "[bosun] ignoring " << where << ": it can't be read; " << all << "\n";
    else if (c.exists && !c.file.valid) std::cerr << "[bosun] ignoring " << where << ": " << c.file.error << "; " << all << "\n";
    for (const auto& [key, why] : c.file.refused) std::cerr << "[bosun] ignoring " << key << " in " << where << ": it " << why << "; " << in_use(key) << "\n";
}

// config.json replaced whole, under a mutex so two writers can't lose each other's change: 0 saved, 74 it couldn't be
// (said in `error`), the old file kept and no temporary file left.
int write_config(const dm::ConfigFile& f, std::string& error) {
    fs::path path = config_path();
    std::error_code ec;
    fs::create_directories(path.parent_path(), ec);
    if (ec) {
        error = "can't make " + narrow(path.parent_path().wstring()) + " (" + ec.message() + ")";
        return 74;
    }
    fs::path temp = path;
    temp += L"." + std::to_wstring(GetCurrentProcessId()) + L".tmp";
    std::string text = dm::config_json(f);
    if (text.size() > dm::kMaxConfigBytes) {
        error = "the saved file would be larger than 64 KB, more than it may hold, so " + narrow(path.wstring()) + " is unchanged";
        return 74;
    }
    bool ok;
    {
        std::ofstream out(temp, std::ios::binary | std::ios::trunc);
        out.write(text.data(), static_cast<std::streamsize>(text.size()));
        ok = static_cast<bool>(out.flush());
    }
    DWORD code = ok ? 0 : GetLastError();
    if (!ok) {  // the temporary file couldn't be made or written (a folder denied, a full disk): nothing was replaced
        fs::remove(temp, ec);
        error = "can't write a temporary file beside " + narrow(path.wstring()) + " (error " + std::to_string(code) + "); it is unchanged";
        return 74;
    }
    for (int attempt = 0; ok; ++attempt) {
        if (posix_replace(temp, path)) return 0;
        if (MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) return 0;
        code = GetLastError();
        if ((code != ERROR_SHARING_VIOLATION && code != ERROR_ACCESS_DENIED) || attempt == 20) ok = false;
        else Sleep(50);
    }
    fs::remove(temp, ec);
    error = "can't replace " + narrow(path.wstring()) + " (error " + std::to_string(code) + "); it is unchanged";
    return 74;
}

void config_reply(bool json, bool ok, const std::string& key, const std::string& message, std::optional<uint64_t> saved = std::nullopt) {
    if (json) {
        std::cout << "{\"schema_version\":1,\"ok\":" << (ok ? "true" : "false") << ",\"key\":\"" << dm::json_escape(key) << "\"";
        if (ok) {
            std::cout << ",\"saved\":";
            if (saved) std::cout << *saved;
            else std::cout << "null";
        } else {
            std::cout << ",\"error\":\"" << dm::json_escape(message) << "\"";
        }
        std::cout << "}\n";
    } else if (ok) {
        std::cout << message << "\n";
    } else {
        std::cerr << "[bosun] " << message << "\n";
    }
}

// bosun config get [<key>...] [--json] | set <key> <value> [--json] | unset <key> [--json]
int cmd_config(const std::vector<std::wstring>& rest) {
    bool json = false;
    std::vector<std::string> words;
    for (const auto& a : rest) {
        if (a == L"--json") json = true;
        else words.push_back(narrow(a));
    }
    if (words.empty()) {
        std::cerr << "usage: bosun config get [<key>...] [--json] | set <key> <value> [--json] | unset <key> [--json]\n";
        return 64;
    }
    const std::string verb = words[0];
    if (verb == "get") {
        LoadedConfig c = load_config();
        std::vector<const dm::LimitSpec*> asked;
        for (size_t i = 1; i < words.size(); ++i) {
            const dm::LimitSpec* spec = dm::find_limit(words[i]);
            if (!spec) {
                config_reply(json, false, words[i], "no limit named " + words[i]);
                return 64;
            }
            asked.push_back(spec);
        }
        if (!json) {
            if (asked.empty())
                for (const auto& spec : dm::limit_specs()) asked.push_back(&spec);
            for (const auto* spec : asked) {
                dm::ResolvedLimit r = dm::resolve_limit(*spec, std::nullopt, env_text(spec->env), saved_value(c, spec->key));
                if (!r.env_error.empty()) std::cerr << "[bosun] ignoring " << r.env_error << ", so " << spec->key << " is " << r.value << " from the " << r.source << "\n";
                std::cout << spec->key << "=" << r.value << "\n";
            }
            return 0;
        }
        std::ostringstream o;
        o << "{\"schema_version\":1,\"path\":\"" << dm::json_escape(narrow(config_path().wstring())) << "\",\"exists\":" << (c.exists ? "true" : "false")
          << ",\"readable\":" << (c.readable ? "true" : "false") << ",\"valid\":" << (c.exists && c.readable && !c.file.valid ? "false" : "true") << ",\"error\":";
        if (c.exists && !c.readable) o << "\"it can't be read\"";
        else if (c.exists && !c.file.valid) o << "\"" << dm::json_escape(c.file.error) << "\"";
        else o << "null";
        o << ",\"refused\":[";
        for (size_t i = 0; i < c.file.refused.size(); ++i)
            o << (i ? "," : "") << "{\"key\":\"" << dm::json_escape(c.file.refused[i].first) << "\",\"error\":\"" << dm::json_escape(c.file.refused[i].second) << "\"}";
        o << "],\"limits\":{";
        bool first = true;
        for (const auto& spec : dm::limit_specs()) {
            auto saved = saved_value(c, spec.key);
            dm::ResolvedLimit r = dm::resolve_limit(spec, std::nullopt, env_text(spec.env), saved);
            o << (first ? "" : ",") << "\"" << spec.key << "\":{\"value\":" << r.value << ",\"source\":\"" << r.source << "\",\"saved\":";
            if (saved) o << *saved;
            else o << "null";
            o << ",\"min\":" << spec.min << ",\"max\":" << spec.max << ",\"default\":" << spec.fallback << ",\"variable\":\"" << spec.env
              << "\",\"variable_error\":";
            if (r.env_error.empty()) o << "null";
            else o << "\"" << dm::json_escape(r.env_error) << "\"";
            o << "}";
            first = false;
        }
        o << "}}";
        std::cout << o.str() << "\n";
        return 0;
    }
    if (verb != "set" && verb != "unset") {
        std::cerr << "[bosun] config takes get, set or unset\n";
        return 64;
    }
    const std::string key = words.size() > 1 ? words[1] : "";
    const dm::LimitSpec* spec = dm::find_limit(key);
    if (!spec || words.size() != (verb == "set" ? 3u : 2u)) {
        config_reply(json, false, key, spec ? "usage: bosun config " + verb + " <key>" + (verb == "set" ? " <value>" : "") : "no limit named " + key);
        return 64;
    }
    std::optional<uint64_t> value;
    if (verb == "set") {
        value = dm::parse_count(words[2], spec->min, spec->max);
        if (!value) {
            config_reply(json, false, key, key + " must be " + dm::limit_range(*spec));
            return 64;
        }
    }
    // One writer at a time: the read, the change and the replace happen under this mutex.
    // Only a writer that holds it goes on (round 1): one that can't make it, or waits 10 s in vain, changes nothing.
    HANDLE lock = CreateMutexW(nullptr, FALSE, L"Local\\Bosun.Config");
    DWORD owned = lock ? WaitForSingleObject(lock, 10000) : WAIT_FAILED;
    if (owned != WAIT_OBJECT_0 && owned != WAIT_ABANDONED) {
        if (lock) CloseHandle(lock);
        config_reply(json, false, key, "another config save is still running, so nothing was changed; try again");
        return 75;
    }
    LoadedConfig c = load_config();
    int code = 0;
    std::string error;
    if (c.exists && !c.readable) {
        error = narrow(config_path().wstring()) + " can't be read, so it isn't overwritten";
        code = 74;
    } else if (c.exists && !c.file.valid) {
        error = narrow(config_path().wstring()) + " is damaged (" + c.file.error + "), so it isn't overwritten; fix or remove it";
        code = 74;
    } else {
        if (value) c.file.values[key] = *value;
        else c.file.values.erase(key);
        code = write_config(c.file, error);
    }
    if (lock) {
        ReleaseMutex(lock);
        CloseHandle(lock);
    }
    if (code) {
        config_reply(json, false, key, error);
        return code;
    }
    config_reply(json, true, key, value ? key + " saved as " + std::to_string(*value) : key + " removed; it now comes from the variable or the default", value);
    return 0;
}

int bosun_main(const std::vector<std::wstring>& args) {
    if (args.empty()) {
        std::cerr << "usage: bosun run|acquire|status|log|report|instances|config [options] [-- command]\n";
        return 64;
    }
    std::vector<std::wstring> rest(args.begin() + 1, args.end());
    if (args[0] == L"config") return cmd_config(rest);
    Options o;
    try {
        if (!parse_options(rest, o)) {
            std::cerr << "[bosun] " << (o.bad.empty() ? std::string("bad options") : o.bad) << "\n";
            return 64;
        }
    } catch (const std::exception&) {
        std::cerr << "[bosun] bad option value\n";
        return 64;
    }
    apply_limits(o);
    if (o.slots < 1) {
        std::cerr << "[bosun] --slots must be at least 1\n";
        return 64;
    }
    if (args[0] == L"run") return cmd_run(o);
    if (args[0] == L"acquire") return cmd_acquire(o);
    if (args[0] == L"status") return cmd_status(o);
    if (args[0] == L"log") return cmd_log(o);
    if (args[0] == L"report") return cmd_report(o);
    if (args[0] == L"instances") return cmd_instances(o);
    std::cerr << "[bosun] unknown command\n";
    return 64;
}

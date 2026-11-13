#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <winternl.h>
#include <pdh.h>
#include <sddl.h>

#include <atomic>
#include <chrono>
#include <climits>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iostream>
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

uint64_t env_num(const wchar_t* name, uint64_t fallback) {
    std::wstring v = env(name);
    return v.empty() ? fallback : std::wcstoull(v.c_str(), nullptr, 10);
}

uint64_t available_mb() {
    MEMORYSTATUSEX m{};
    m.dwLength = sizeof(m);
    GlobalMemoryStatusEx(&m);
    return m.ullAvailPhys / (1024 * 1024);
}

struct Options {
    int slots = static_cast<int>(env_num(L"BOSUN_SLOTS", 2));
    uint64_t min_avail_mb = env_num(L"BOSUN_MIN_AVAIL_MB", 6144);
    uint64_t max_wait_s = env_num(L"BOSUN_MAX_WAIT", 900);
    uint64_t max_write_gb = env_num(L"BOSUN_MAX_WRITE_GB", 100);
    std::wstring ns = L"Bosun";
    int days = 7;
    int interval_s = 30;
    bool once = false;
    std::vector<std::wstring> command;
    // What a record says of the job (acquire takes them from the WSL shim; run works them out for itself).
    std::wstring repo, side, label;
};

// Returns false on a bad flag. Everything after -- is the command.
bool parse_options(const std::vector<std::wstring>& args, Options& o) {
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
        if (i + 1 >= args.size()) return false;
        const std::wstring& v = args[++i];
        if (a == L"--slots") o.slots = std::stoi(v);
        else if (a == L"--min-avail-mb") o.min_avail_mb = std::stoull(v);
        else if (a == L"--max-wait") o.max_wait_s = std::stoull(v);
        else if (a == L"--max-write-gb") o.max_write_gb = std::stoull(v);
        else if (a == L"--ns") o.ns = v;
        else if (a == L"--days") o.days = std::stoi(v);
        else if (a == L"--interval") o.interval_s = std::stoi(v);
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

// Takes a slot, or returns index -1 after max_wait so that a job is delayed and never stalled for good.
Slot acquire_slot(const Options& o) {
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
    for (;;) {
        for (int i = 0; i < o.slots; ++i) {
            DWORD w = WaitForSingleObject(handles[i], 0);
            if (w != WAIT_OBJECT_0 && w != WAIT_ABANDONED) continue;
            if (dm::slot_admits(i, available_mb(), o.min_avail_mb)) {
                for (int j = 0; j < o.slots; ++j)
                    if (j != i) CloseHandle(handles[j]);
                return {handles[i], i};
            }
            ReleaseMutex(handles[i]);
        }
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
    }
    void waiting() {
        dm::SlotRecord r = base_;
        r.kind = "waiter";
        waiter_ = dir_ / widen(dm::waiter_file(r.pid, r.process_start));
        write(waiter_, r);
    }
    // Called holding slot N's mutex: only its holder ever writes or removes that slot's record.
    void holding(int slot) {
        // A reader can hold the waiter file open for a moment, which makes the removal fail: it is retried briefly, and
        // a removal that still fails stays this job's to finish when it ends (a reader also drops a waiter whose own
        // process now holds a slot, so a job is never shown both waiting and holding for longer than that).
        if (!remove_retrying(waiter_)) pending_.push_back(waiter_);
        waiter_.clear();
        if (slot < 0) return;  // ran without a slot: there is no holder to name
        dm::SlotRecord r = base_;
        r.kind = "holder";
        r.slot = slot;
        holder_ = dir_ / widen(dm::slot_file(slot));
        write(holder_, r);
    }
    // Called before the mutex is released, so a successor's record is never the one removed.
    void done() {
        remove_retrying(waiter_);
        remove_retrying(holder_);
        for (const auto& path : pending_) remove_retrying(path);
        waiter_.clear();
        holder_.clear();
        pending_.clear();
    }

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
        if (ok) ok = MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
        if (!ok) {
            DWORD error = ec ? static_cast<DWORD>(ec.value()) : GetLastError();
            fs::remove(temp, ec);
            if (!warned_) {
                std::cerr << "[bosun] can't publish this job's slot record (" << narrow(path.parent_path().wstring()) << ", error " << error
                          << "): the queue works, but status can't name it\n";
                warned_ = true;
            }
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
    dm::SlotRecord base_;
    bool warned_ = false;
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
    j.ended_at = utc_now();
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

// A reader's pass over the records: removes stale waiters only, and says whether the folder could be read at all, so
// an unreadable folder is never taken for an empty queue.
bool sweep_records(const Options& o) {
    std::error_code ec;
    fs::path dir = records_dir(o);
    if (!fs::exists(dir, ec)) return !ec;
    fs::directory_iterator it(dir, ec), end;
    if (ec) return false;
    std::vector<std::pair<fs::path, dm::SlotRecord>> found;
    for (; it != end; it.increment(ec)) {
        if (ec) return false;
        const fs::path& path = it->path();
        if (path.extension() != L".json") continue;
        std::string text;
        {
            // Closed before any removal below: Windows won't delete a file this process still has open.
            std::ifstream in(path, std::ios::binary);
            text.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
        }
        auto record = dm::parse_record(text);
        if (!record) continue;  // half written, or not one of ours: a writer replaces it, a reader leaves it
        found.emplace_back(path, *record);
    }
    std::vector<dm::SlotRecord> records;
    for (const auto& [path, record] : found) records.push_back(record);
    for (const auto& [path, record] : found) {
        if (dm::reader_removes(record, dm::record_state(record, probe_process), records)) {
            std::error_code gone;
            fs::remove(path, gone);
        }
    }
    return true;
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

int run_child(const Options& o) {
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
    Slot slot = acquire_slot(o);
    int waiting = 0;
    if (!g_wait.phase.compare_exchange_strong(waiting, 1)) Sleep(INFINITE);  // cancelled meanwhile: its handler exits
    records.holding(slot.index);
    auto started = std::chrono::steady_clock::now();
    SetEnvironmentVariableW(L"BOSUN_SLOT", std::to_wstring(slot.index).c_str());
    // WSLENV carries the marker into WSL, so a nested run on the other side also passes through.
    std::wstring wslenv = env(L"WSLENV");
    SetEnvironmentVariableW(L"WSLENV", (wslenv.empty() ? L"BOSUN_SLOT" : wslenv + L":BOSUN_SLOT").c_str());
    int code = run_child(o);
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
    Records records(job);
    records.waiting();
    Slot slot = acquire_slot(o);
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
    append_job_line(job_line(job, started_at, slot.index, seconds_since(took, started), seconds_since(started, ended), code));
    return 0;
}

int cmd_status(const Options& o) {
    // Stale waiters go; holders' records are only ever their holders' to change. A folder that can't be read is said,
    // never shown as an empty queue.
    bool records_readable = sweep_records(o);
    int busy = 0;
    for (int i = 0; i < o.slots; ++i) {
        HANDLE h = open_slot(o.ns, i);
        if (!h) continue;
        DWORD w = WaitForSingleObject(h, 0);
        if (w == WAIT_OBJECT_0 || w == WAIT_ABANDONED) ReleaseMutex(h);
        else ++busy;
        CloseHandle(h);
    }
    MEMORYSTATUSEX m{};
    m.dwLength = sizeof(m);
    GlobalMemoryStatusEx(&m);
    std::cout << "slots busy: " << busy << " of " << o.slots << "\n";
    std::cout << "available: " << available_mb() << " of " << m.ullTotalPhys / (1024 * 1024) << " MB\n";
    HANDLE logger = OpenMutexW(SYNCHRONIZE, FALSE, L"Local\\Bosun.Logger");
    std::cout << "logger: " << (logger ? "running" : "not running") << "\n";
    if (logger) CloseHandle(logger);
    if (!records_readable) std::cout << "records: unavailable (" << narrow(records_dir(o).wstring()) << " can't be read), so who holds and waits can't be named\n";
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
std::vector<Proc> snapshot_processes() {
    using QueryFn = NTSTATUS(NTAPI*)(ULONG, PVOID, ULONG, PULONG);
    static auto query = reinterpret_cast<QueryFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation"));
    std::vector<BYTE> buf(1 << 20);
    ULONG needed = 0;
    NTSTATUS st;
    while ((st = query(SystemProcessInformation, buf.data(), static_cast<ULONG>(buf.size()), &needed)) == static_cast<NTSTATUS>(0xC0000004L))
        buf.resize(std::max<size_t>(buf.size() * 2, needed + 65536));
    std::vector<Proc> out;
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

void write_sample(PDH_HCOUNTER counter, std::unordered_map<std::wstring, std::string>& cache) {
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
    MEMORYSTATUSEX m{};
    m.dwLength = sizeof(m);
    GlobalMemoryStatusEx(&m);
    row("sys", "available_mb", 0, m.ullAvailPhys / MB, 0);
    row("sys", "commit_mb", 0, (m.ullTotalPageFile - m.ullAvailPageFile) / MB, 0);
    row("sys", "commit_limit_mb", 0, m.ullTotalPageFile / MB, 0);
    PDH_FMT_COUNTERVALUE value{};
    if (PdhGetFormattedCounterValue(counter, PDH_FMT_LARGE, nullptr, &value) == ERROR_SUCCESS)
        row("sys", "pages_in_per_sec", 0, value.largeValue, 0);
    for (const auto& [name, g] : repos) row("repo", name, g.count, g.priv / MB, g.ws / MB);
    std::vector<std::pair<std::string, Group>> sorted(names.begin(), names.end());
    std::sort(sorted.begin(), sorted.end(), [](const auto& a, const auto& b) { return a.second.priv > b.second.priv; });
    for (size_t i = 0; i < sorted.size() && i < 15; ++i)
        row("proc", sorted[i].first, sorted[i].second.count, sorted[i].second.priv / MB, sorted[i].second.ws / MB);

    fs::path file = log_dir() / (L"windows-" + std::wstring(ts.begin(), ts.begin() + 10) + L".csv");
    bool fresh = !fs::exists(file);
    std::ofstream f(file, std::ios::app | std::ios::binary);
    if (fresh) f << "ts,side,kind,name,count,private_mb,rss_mb\n";
    f << o.str();
}

void prune_logs(int keep_days) {
    std::string cutoff = date_days_ago(keep_days);
    std::error_code ec;
    for (const auto& e : fs::directory_iterator(log_dir(), ec)) {
        std::string d = file_date(e.path());
        if (!d.empty() && d < cutoff) fs::remove(e.path(), ec);
    }
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
        write_sample(c, cache);
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

int cmd_report(const Options& o) {
    std::string cutoff = date_days_ago(o.days);
    std::vector<dm::Row> rows;
    std::error_code ec;
    for (const auto& e : fs::directory_iterator(log_dir(), ec)) {
        std::string d = file_date(e.path());
        if (d.empty() || d < cutoff) continue;
        std::ifstream f(e.path());
        std::string line;
        while (std::getline(f, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (auto r = dm::parse_row(line)) rows.push_back(std::move(*r));
        }
    }
    std::cout << dm::render(dm::aggregate(rows));
    return 0;
}

}  // namespace

int bosun_main(const std::vector<std::wstring>& args) {
    if (args.empty()) {
        std::cerr << "usage: bosun run|acquire|status|log|report [options] [-- command]\n";
        return 64;
    }
    std::vector<std::wstring> rest(args.begin() + 1, args.end());
    Options o;
    try {
        if (!parse_options(rest, o)) {
            std::cerr << "[bosun] bad options\n";
            return 64;
        }
    } catch (const std::exception&) {
        std::cerr << "[bosun] bad option value\n";
        return 64;
    }
    if (o.slots < 1) {
        std::cerr << "[bosun] --slots must be at least 1\n";
        return 64;
    }
    if (args[0] == L"run") return cmd_run(o);
    if (args[0] == L"acquire") return cmd_acquire(o);
    if (args[0] == L"status") return cmd_status(o);
    if (args[0] == L"log") return cmd_log(o);
    if (args[0] == L"report") return cmd_report(o);
    std::cerr << "[bosun] unknown command\n";
    return 64;
}

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <set>

#include "../src/core.hpp"

// Named groups, so a section's checkpoint can run exactly the tests it owns: `test_core <name>`. With no name every
// implemented group runs. A reserved name belongs to a section not built yet: selecting it fails, so that section
// can't pass on tests that don't exist, and it joins the default run once its function is filled in.
static int failures = 0;
#define CHECK(cond)                                                \
    do {                                                           \
        if (!(cond)) {                                             \
            std::printf("FAIL %s:%d %s\n", __FILE__, __LINE__, #cond); \
            ++failures;                                            \
        }                                                          \
    } while (0)

static dm::Row sys(const char* ts, const char* side, const char* name, int64_t v) {
    return {ts, side, "sys", name, 0, v, 0};
}
static dm::Row repo(const char* ts, const char* side, const char* name, int64_t v) {
    return {ts, side, "repo", name, 1, v, 0};
}

static void slots() {
    // The memory gate never blocks slot 0, so a lone job always runs.
    CHECK(dm::slot_admits(0, 0, 6144));
    CHECK(!dm::slot_admits(1, 6143, 6144));
    CHECK(dm::slot_admits(1, 6144, 6144));
}

static void write_budget() {
    const uint64_t gb = uint64_t{1} << 30;
    CHECK(!dm::over_write_budget(100 * gb, 100));     // at the budget is still within it
    CHECK(dm::over_write_budget(100 * gb + 1, 100));
    CHECK(dm::over_write_budget(287 * gb, 100));      // the flood that filled C: on 2026-11-08
    CHECK(!dm::over_write_budget(UINT64_MAX, 0));     // 0 turns the budget off
}

static void quoting() {
    // Quoting follows the CommandLineToArgvW rules.
    CHECK(dm::quote_arg(L"plain") == L"plain");
    CHECK(dm::quote_arg(L"") == L"\"\"");
    CHECK(dm::quote_arg(L"a b") == L"\"a b\"");
    CHECK(dm::quote_arg(L"a\"b") == L"\"a\\\"b\"");
    CHECK(dm::quote_arg(L"C:\\a b\\") == L"\"C:\\a b\\\\\"");
}

static void batch() {
    // Batch words are quoted, so metacharacters stay text, and % cannot form a variable reference.
    CHECK(dm::batch_quote(L"a&calc") == L"\"a&calc\"");
    CHECK(dm::batch_quote(L"a\"b") == L"\"a\"\"b\"");
    CHECK(dm::batch_quote(L"%PATH%") == L"\"%%cd:~,%PATH%%cd:~,%\"");
    CHECK(dm::batch_quote(L"C:\\a\\") == L"\"C:\\a\\\\\"");
    auto line = dm::build_command_line(L"C:\\Program Files\\npm.cmd", {L"test"}, true);
    CHECK(line == L"cmd.exe /e:ON /v:OFF /d /s /c \"\"C:\\Program Files\\npm.cmd\" \"test\"\"");
    CHECK(!dm::build_command_line(L"npm.cmd", {L"a\nb"}, true).has_value());
    CHECK(dm::build_command_line(L"x.exe", {L"a&b"}, false) == L"x.exe a&b");
}

static void repos() {
    // Repo attribution walks up to the nearest .git and keeps same-named repos apart.
    std::set<std::string> gits{"D:/GitHub/Bosun", "/home/u/a/app", "/home/u/b/app"};
    auto has = [&](const std::string& d) { return gits.count(d) > 0; };
    CHECK(dm::repo_from_path("D:\\GitHub\\Bosun\\src\\x", has) == "D:/GitHub/Bosun");
    CHECK(dm::repo_from_path("D:\\GitHub\\Bosun\\", has) == "D:/GitHub/Bosun");
    CHECK(dm::repo_from_path("/home/u/a/app/src", has) != dm::repo_from_path("/home/u/b/app/src", has));
    CHECK(!dm::repo_from_path("C:\\Windows\\System32", has).has_value());
}

static void rows() {
    // Rows: the header and truncated lines from a killed sampler are skipped.
    CHECK(!dm::parse_row("ts,side,kind,name,count,private_mb,rss_mb"));
    CHECK(!dm::parse_row("2026-11-07T10:00:00,win,sys,avail"));
    CHECK(!dm::parse_row("2026-11-07T10:00:00,win,sys,available_mb,0,x,0"));
    auto ok = dm::parse_row("2026-11-07T10:00:00,win,repo,Bosun,3,120,90");
    CHECK(ok && ok->name == "Bosun" && ok->count == 3 && ok->private_mb == 120 && ok->rss_mb == 90);
    CHECK(dm::csv_name("a,b\"c%") == "a%2Cb%22c%25");
    CHECK(dm::csv_name("a,b") != dm::csv_name("a_b"));
}

static void aggregate() {
    // Aggregation joins both sides on the minute and finds the worst moment.
    std::vector<dm::Row> samples{
        sys("2026-11-07T10:00:05", "win", "available_mb", 9000),
        sys("2026-11-07T10:00:35", "win", "available_mb", 8000),
        repo("2026-11-07T10:00:05", "win", "A", 1000),
        sys("2026-11-07T10:01:05", "win", "available_mb", 1500),
        repo("2026-11-07T10:01:05", "win", "A", 3000),
        repo("2026-11-07T10:01:20", "wsl", "B", 4000),
        repo("2026-11-07T10:01:50", "wsl", "B", 3500),
        sys("2026-11-07T10:01:20", "wsl", "mem_available_mb", 700),
        sys("2026-11-07T10:01:20", "wsl", "oom_kills", 2),
        sys("2026-11-07T10:01:05", "win", "commit_mb", 30000),
        sys("2026-11-07T10:01:05", "win", "commit_limit_mb", 80000),
    };
    dm::Report r = dm::aggregate(samples);
    CHECK(r.win_samples == 3);
    CHECK(r.low_samples == 1);
    CHECK(r.min_avail_mb == 1500);
    CHECK(r.worst_minute == "2026-11-07T10:01");
    CHECK(r.worst_repos["win:A"] == 3000);
    CHECK(r.worst_repos["wsl:B"] == 4000);
    CHECK(r.repo_peak_mb["win:A"] == 3000);
    CHECK(r.busiest_total_mb == 7000);
    CHECK(r.busiest_repo_count == 2);
    CHECK(r.wsl_min_avail_mb == 700);
    CHECK(r.wsl_max_oom_kills == 2);
    CHECK(r.max_commit_mb == 30000);
    CHECK(dm::render(r).find("lowest available: 1500 MB") != std::string::npos);
    CHECK(dm::render(dm::Report{}).find("No samples") != std::string::npos);
}

static void records() {
    // A holder's record round-trips, its command holding quotes, backslashes, a control character and non-ASCII text.
    dm::SlotRecord h;
    h.kind = "holder";
    h.pid = 4242;
    h.process_start = 134390106322799740ULL;
    h.started_at = "2026-11-13T04:00:00Z";
    h.command = "\"C:\\Program Files\\x.exe\" --name \"caf\xc3\xa9\" a\tb";
    h.repo = "D:/GitHub/Bosun";
    h.side = "windows";
    h.slot = 1;
    h.session = 3;
    h.ns = "Bosun";
    std::string text = dm::record_json(h);
    CHECK(text.find("\\u0009") != std::string::npos);  // the tab, as a control character
    auto back = dm::parse_record(text);
    CHECK(back && back->command == h.command && back->repo == h.repo && back->slot == 1 && back->pid == 4242);
    CHECK(back && back->process_start == h.process_start && back->session == 3 && back->kind == "holder" && back->side == "windows");
    // A waiter outside any checkout: no slot, and a null repo.
    dm::SlotRecord w = h;
    w.kind = "waiter";
    w.slot.reset();
    w.repo.reset();
    w.side = "wsl";
    auto wb = dm::parse_record(dm::record_json(w));
    CHECK(wb && !wb->repo && !wb->slot && wb->side == "wsl");
    CHECK(dm::record_json(w).find("\"repo\":null") != std::string::npos);
    // Refused, never thrown: truncated, not an object, another version, a missing field, a holder with no slot.
    CHECK(!dm::parse_record(text.substr(0, text.size() / 2)));
    CHECK(!dm::parse_record("[]"));
    CHECK(!dm::parse_record(""));
    CHECK(!dm::parse_record("null"));
    std::string v2 = text;
    v2.replace(v2.find("\"schema_version\":1"), 18, "\"schema_version\":2");
    CHECK(!dm::parse_record(v2));
    CHECK(!dm::parse_record("{\"schema_version\":1,\"kind\":\"holder\"}"));
    std::string noslot = dm::record_json(h);
    noslot.replace(noslot.find(",\"slot\":1"), 9, "");
    CHECK(!dm::parse_record(noslot));
    // \u escapes, a surrogate pair included, read back as UTF-8.
    std::string escaped = text;
    escaped.replace(escaped.find("\"ns\":\"Bosun\""), 12, "\"ns\":\"\\u00e9\\ud83d\\ude00\"");
    auto e = dm::parse_record(escaped);
    CHECK(e && e->ns == "\xc3\xa9\xf0\x9f\x98\x80");

    // Namespaces map injectively onto valid folder names.
    CHECK(dm::ns_dir("Bosun") == "ns-%42osun");
    CHECK(dm::ns_dir("a.b") != dm::ns_dir("a_b") && dm::ns_dir("a/b") != dm::ns_dir("a%2Fb"));
    CHECK(dm::ns_dir("") == "ns-" && dm::ns_dir("CON") == "ns-%43%4F%4E" && dm::ns_dir("x.") == "ns-x%2E");
    // Distinct however the file system folds case (round 1 of the review): mutex names are case-sensitive.
    auto folded = [](std::string d) { for (auto& c : d) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c))); return d; };
    CHECK(folded(dm::ns_dir("ReviewCase")) != folded(dm::ns_dir("reviewcase")));
    CHECK(folded(dm::ns_dir("%4a")) != folded(dm::ns_dir("J")) && folded(dm::ns_dir("%4A")) != folded(dm::ns_dir("J")));
    for (const char* ns : {"a b", "x:y", "q?", "\xc3\xa9", ".", ".."}) {
        std::string d = dm::ns_dir(ns);
        CHECK(d.find_first_not_of("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-%") == std::string::npos);
        CHECK(d.back() != '.');
    }
    CHECK(dm::waiter_file(7, 99) == "wait-7-99.json" && dm::slot_file(0) == "slot-0.json");
    // A waiter record as another program writes it, compact, with a null repo and session 0.
    CHECK(dm::parse_record("{\"schema_version\":1,\"kind\":\"waiter\",\"pid\":45980,\"process_start\":134390171843506943,\"started_at\":\"2026-11-13T00:00:00Z\",\"command\":\"x\",\"repo\":null,\"side\":\"windows\",\"session\":0,\"ns\":\"Dbg\"}").has_value());

    // A record is live while its pid runs with its start time; gone or reused is stale; can't tell is unknown.
    auto running = [](uint64_t start) { return [start](int64_t) { return dm::ProbeAnswer{dm::ProbeAnswer::Running, start}; }; };
    CHECK(dm::record_state(h, running(h.process_start)) == dm::RecordState::Live);
    CHECK(dm::record_state(h, running(h.process_start + 1)) == dm::RecordState::Stale);
    CHECK(dm::record_state(h, [](int64_t) { return dm::ProbeAnswer{dm::ProbeAnswer::Gone, 0}; }) == dm::RecordState::Stale);
    CHECK(dm::record_state(h, [](int64_t) { return dm::ProbeAnswer{dm::ProbeAnswer::CantTell, 0}; }) == dm::RecordState::Unknown);
    // Readers remove only a stale waiter: never a slot record, a live waiter or one whose process can't be told.
    CHECK(dm::reader_removes(w, dm::RecordState::Stale));
    CHECK(!dm::reader_removes(w, dm::RecordState::Live) && !dm::reader_removes(w, dm::RecordState::Unknown));
    CHECK(!dm::reader_removes(h, dm::RecordState::Stale));
    // A live waiter superseded by its own process's holder record goes; another process's holder doesn't supersede it.
    dm::SlotRecord sameHolder = h;
    sameHolder.pid = w.pid;
    sameHolder.process_start = w.process_start;
    CHECK(dm::reader_removes(w, dm::RecordState::Live, {w, sameHolder}));
    dm::SlotRecord otherHolder = sameHolder;
    otherHolder.process_start = w.process_start + 1;
    CHECK(!dm::reader_removes(w, dm::RecordState::Live, {w, otherHolder}));
    CHECK(!dm::reader_removes(h, dm::RecordState::Live, {h, sameHolder}));
}

static void jobs() {
    // A finished job's line round-trips, its command holding quotes and non-ASCII text.
    dm::JobLine j;
    j.started_at = "2026-11-13T05:00:00Z";
    j.ended_at = "2026-11-13T05:00:12Z";
    j.pid = 77;
    j.command = "pytest -k \"a and b\" caf\xc3\xa9";
    j.repo = "D:/GitHub/Bosun";
    j.side = "windows";
    j.ns = "Bosun";
    j.slot = 1;
    j.waited_s = 3.04;
    j.ran_s = 8.96;
    j.exit_code = -1073741819;  // 0xC0000005, an access violation, as a signed 32-bit exit code
    std::string text = dm::job_line_json(j);
    CHECK(text.find("\"waited_s\":3.0") != std::string::npos && text.find("\"ran_s\":9.0") != std::string::npos);
    auto back = dm::parse_job_line(text);
    CHECK(back && back->command == j.command && back->repo == j.repo && back->slot == 1 && back->exit_code == j.exit_code);
    CHECK(back && back->waited_s > 2.95 && back->waited_s < 3.05 && back->ran_s > 8.95 && back->ran_s < 9.05 && !back->timed_out_into_queue);
    // A WSL job that timed out into the queue, outside any checkout, whose shim went away: no slot, repo or exit code.
    dm::JobLine w = j;
    w.side = "wsl";
    w.slot.reset();
    w.repo.reset();
    w.exit_code.reset();
    w.timed_out_into_queue = true;
    auto wb = dm::parse_job_line(dm::job_line_json(w));
    CHECK(wb && !wb->slot && !wb->repo && !wb->exit_code && wb->timed_out_into_queue && wb->side == "wsl");
    // Refused: truncated, another version, a missing field, a side that isn't one.
    CHECK(!dm::parse_job_line(text.substr(0, text.size() - 5)));
    std::string v2 = text;
    v2.replace(v2.find("\"schema_version\":1"), 18, "\"schema_version\":2");
    CHECK(!dm::parse_job_line(v2));
    CHECK(!dm::parse_job_line("{\"schema_version\":1}"));
    std::string side = text;
    side.replace(side.find("\"side\":\"windows\""), 16, "\"side\":\"mac\"");
    CHECK(!dm::parse_job_line(side));
    // The records still demand whole numbers: a decimal pid is no record.
    std::string rec = "{\"schema_version\":1,\"kind\":\"waiter\",\"pid\":7.5,\"process_start\":1,\"started_at\":\"x\",\"command\":\"x\",\"repo\":null,\"side\":\"windows\",\"session\":0,\"ns\":\"n\"}";
    CHECK(!dm::parse_record(rec));
}

struct Group {
    const char* name;
    void (*run)();      // null while the group is reserved
    const char* owner;  // the section that fills a reserved group in
};

static const Group groups[] = {
    {"slots", slots, nullptr},
    {"write_budget", write_budget, nullptr},
    {"quoting", quoting, nullptr},
    {"batch", batch, nullptr},
    {"repos", repos, nullptr},
    {"rows", rows, nullptr},
    {"aggregate", aggregate, nullptr},
    {"records", records, nullptr},
    {"jobs", jobs, nullptr},
    {"status_json", nullptr, "D01 T01"},
    {"report_json", nullptr, "D01 T01"},
    {"config", nullptr, "D01 T01"},
    {"pressure", nullptr, "D05 T02"},
};

static std::string env(const char* name) {
#ifdef _MSC_VER
    char* value = nullptr;
    size_t size = 0;
    std::string out;
    if (_dupenv_s(&value, &size, name) == 0 && value) out = value;
    std::free(value);
    return out;
#else
    const char* value = std::getenv(name);
    return value ? value : "";
#endif
}

static void run_group(const Group& g) {
    std::printf("group %s\n", g.name);
    g.run();
    // A test of the runner itself: a forced failure proves a group's failure reaches the exit code.
    CHECK(env("TEST_CORE_FORCE_FAIL") != g.name);
}

int main(int argc, char** argv) {
    if (argc > 2) {
        std::printf("usage: test_core [--list | <group>]\n");
        return 2;
    }
    if (argc == 2 && std::strcmp(argv[1], "--list") == 0) {
        for (const Group& g : groups) std::printf("%s %s\n", g.name, g.run ? "implemented" : "reserved");
        return 0;
    }
    if (argc == 2) {
        for (const Group& g : groups) {
            if (std::strcmp(argv[1], g.name) != 0) continue;
            if (!g.run) {
                std::printf("FAIL %s is reserved for %s and has no tests yet\n", g.name, g.owner);
                return 1;
            }
            run_group(g);
            if (failures) return 1;
            std::printf("ok\n");
            return 0;
        }
        std::printf("FAIL no test group named %s (test_core --list shows them)\n", argv[1]);
        return 2;
    }
    for (const Group& g : groups) {
        if (g.run) run_group(g);
    }
    if (failures) return 1;
    std::printf("ok\n");
    return 0;
}

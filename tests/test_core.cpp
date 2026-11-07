#include <cstdio>
#include <set>

#include "../src/core.hpp"

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

int main() {
    // The memory gate never blocks slot 0, so a lone job always runs.
    CHECK(dm::slot_admits(0, 0, 6144));
    CHECK(!dm::slot_admits(1, 6143, 6144));
    CHECK(dm::slot_admits(1, 6144, 6144));

    // Quoting follows the CommandLineToArgvW rules.
    CHECK(dm::quote_arg(L"plain") == L"plain");
    CHECK(dm::quote_arg(L"") == L"\"\"");
    CHECK(dm::quote_arg(L"a b") == L"\"a b\"");
    CHECK(dm::quote_arg(L"a\"b") == L"\"a\\\"b\"");
    CHECK(dm::quote_arg(L"C:\\a b\\") == L"\"C:\\a b\\\\\"");

    // Batch words are quoted, so metacharacters stay text, and % cannot form a variable reference.
    CHECK(dm::batch_quote(L"a&calc") == L"\"a&calc\"");
    CHECK(dm::batch_quote(L"a\"b") == L"\"a\"\"b\"");
    CHECK(dm::batch_quote(L"%PATH%") == L"\"%%cd:~,%PATH%%cd:~,%\"");
    CHECK(dm::batch_quote(L"C:\\a\\") == L"\"C:\\a\\\\\"");
    auto line = dm::build_command_line(L"C:\\Program Files\\npm.cmd", {L"test"}, true);
    CHECK(line == L"cmd.exe /e:ON /v:OFF /d /s /c \"\"C:\\Program Files\\npm.cmd\" \"test\"\"");
    CHECK(!dm::build_command_line(L"npm.cmd", {L"a\nb"}, true).has_value());
    CHECK(dm::build_command_line(L"x.exe", {L"a&b"}, false) == L"x.exe a&b");

    // Repo attribution walks up to the nearest .git and keeps same-named repos apart.
    std::set<std::string> gits{"D:/GitHub/Bosun", "/home/u/a/app", "/home/u/b/app"};
    auto has = [&](const std::string& d) { return gits.count(d) > 0; };
    CHECK(dm::repo_from_path("D:\\GitHub\\Bosun\\src\\x", has) == "D:/GitHub/Bosun");
    CHECK(dm::repo_from_path("D:\\GitHub\\Bosun\\", has) == "D:/GitHub/Bosun");
    CHECK(dm::repo_from_path("/home/u/a/app/src", has) != dm::repo_from_path("/home/u/b/app/src", has));
    CHECK(!dm::repo_from_path("C:\\Windows\\System32", has).has_value());

    // Rows: the header and truncated lines from a killed sampler are skipped.
    CHECK(!dm::parse_row("ts,side,kind,name,count,private_mb,rss_mb"));
    CHECK(!dm::parse_row("2026-11-07T10:00:00,win,sys,avail"));
    CHECK(!dm::parse_row("2026-11-07T10:00:00,win,sys,available_mb,0,x,0"));
    auto ok = dm::parse_row("2026-11-07T10:00:00,win,repo,Bosun,3,120,90");
    CHECK(ok && ok->name == "Bosun" && ok->count == 3 && ok->private_mb == 120 && ok->rss_mb == 90);
    CHECK(dm::csv_name("a,b\"c%") == "a%2Cb%22c%25");
    CHECK(dm::csv_name("a,b") != dm::csv_name("a_b"));

    // Aggregation joins both sides on the minute and finds the worst moment.
    std::vector<dm::Row> rows{
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
    dm::Report r = dm::aggregate(rows);
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

    if (failures) return 1;
    std::printf("ok\n");
    return 0;
}

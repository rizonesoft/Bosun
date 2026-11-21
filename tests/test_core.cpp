#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

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
    // The largest budget never wraps to a small one (round 1 of D01 T01 §3): 2^34 GB once came to 0 bytes.
    CHECK(!dm::over_write_budget(gb, dm::kMaxWriteGb) && !dm::over_write_budget(dm::kMaxWriteGb << 30, dm::kMaxWriteGb));
    CHECK(dm::over_write_budget((dm::kMaxWriteGb << 30) + 1, dm::kMaxWriteGb));
    CHECK(!dm::over_write_budget(gb, uint64_t{1} << 34) && !dm::over_write_budget(gb, UINT64_MAX));
    CHECK(dm::over_write_budget(UINT64_MAX, dm::kMaxWriteGb - 1));
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
    std::set<std::string> gits{"D:/src/Bosun", "/home/u/a/app", "/home/u/b/app"};
    auto has = [&](const std::string& d) { return dm::GitProbe{gits.count(d) > 0, std::nullopt}; };
    CHECK(dm::repo_walk("D:\\src\\Bosun\\src\\x", has).repo == "D:/src/Bosun");
    CHECK(dm::repo_walk("D:\\src\\Bosun\\", has).repo == "D:/src/Bosun");
    CHECK(dm::repo_walk("/home/u/a/app/src", has, false).repo != dm::repo_walk("/home/u/b/app/src", has, false).repo);
    CHECK(!dm::repo_walk("C:\\Windows\\System32", has).repo.has_value());
    // A drive's root is asked as "D:/", never "D:", which Windows reads relative to the drive's current folder.
    std::vector<std::string> asked;
    auto record = [&](const std::string& d) { asked.push_back(d); return dm::GitProbe{d == "D:/", std::nullopt}; };
    CHECK(dm::repo_walk("D:\\work\\x", record).repo == std::optional<std::string>("D:/"));
    CHECK((asked == std::vector<std::string>{"D:/work/x", "D:/work", "D:/"}));
    // D03 T02 §7: a probe that fails stops the walk with why; a checkout above it is never taken for its repo.
    auto failing = [](const std::string& d) -> dm::GitProbe {
        if (d == "D:/outer/denied") return {false, std::string("Access is denied.")};
        return {d == "D:/outer", std::nullopt};
    };
    auto denied = dm::repo_walk("D:\\outer\\denied\\work", failing);
    CHECK(!denied.repo && denied.why == std::optional<std::string>("D:/outer/denied could not be checked for a repo: Access is denied."));
    auto beside = dm::repo_walk("D:\\outer\\open\\work", failing);
    CHECK(beside.repo == std::optional<std::string>("D:/outer") && !beside.why);
    // Round 2: a Linux path keeps a backslash as a name's own character.
    std::vector<std::string> walked;
    dm::repo_walk("/home/me/client\\api", [&](const std::string& d) { walked.push_back(d); return dm::GitProbe{}; }, false);
    CHECK(!walked.empty() && walked.front() == "/home/me/client\\api");
    auto outside = dm::repo_walk("C:\\loose", [](const std::string&) { return dm::GitProbe{}; });
    CHECK(!outside.repo && !outside.why);
    // The text form says a repo that could not be told apart from no repo.
    dm::InstancesDoc doc;
    doc.side = "windows";
    dm::Instance unknown;
    unknown.side = "windows";
    unknown.kind = "claude";
    unknown.pid = 7;
    unknown.repo_why = denied.why;
    dm::Instance loose = unknown;
    loose.pid = 8;
    loose.repo_why = std::nullopt;
    doc.instances = {unknown, loose};
    auto text = dm::instances_text(doc);
    CHECK(text.find("claude 7 (repo unknown: D:/outer/denied could not be checked for a repo: Access is denied.) unknown") != std::string::npos);
    CHECK(text.find("claude 8 (no repo) unknown") != std::string::npos);
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
    h.repo = "D:/src/Bosun";
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

    // The §3 fields round-trip: a holder's acquisition, command start and guest floor; a waiter's reason and limits.
    dm::SlotRecord t = h;
    t.acquired_at = "2026-11-13T04:00:05Z";
    t.command_started_at = "2026-11-13T04:00:06Z";
    t.min_guest_mb = 3072;
    auto tb = dm::parse_record(dm::record_json(t));
    CHECK(tb && tb->acquired_at == t.acquired_at && tb->command_started_at == t.command_started_at && tb->min_guest_mb == 3072u);
    dm::SlotRecord u = w;
    u.waiting_for = "memory";
    u.min_avail_mb = 6144;
    u.max_wait_s = 900;
    auto ub = dm::parse_record(dm::record_json(u));
    CHECK(ub && ub->waiting_for == "memory" && ub->min_avail_mb == 6144u && ub->max_wait_s == 900u);
    // A §1 record, without them, still reads, with them null.
    CHECK(back && !back->acquired_at && !back->command_started_at && !back->min_guest_mb);
    CHECK(wb && !wb->waiting_for && !wb->min_avail_mb && !wb->max_wait_s);
    // A wrong shape refuses the record: a stamp that isn't one, an unknown reason, a negative or string count.
    auto with = [&](const dm::SlotRecord& r, const std::string& from, const std::string& to) {
        std::string s = dm::record_json(r);
        size_t at = s.find(from);
        CHECK(at != std::string::npos);
        return at == std::string::npos ? s : s.replace(at, from.size(), to);
    };
    CHECK(!dm::parse_record(with(t, "\"acquired_at\":\"2026-11-13T04:00:05Z\"", "\"acquired_at\":\"yesterday\"")));
    CHECK(!dm::parse_record(with(t, "\"acquired_at\":\"2026-11-13T04:00:05Z\"", "\"acquired_at\":5")));
    CHECK(!dm::parse_record(with(u, "\"waiting_for\":\"memory\"", "\"waiting_for\":\"tea\"")));
    CHECK(!dm::parse_record(with(u, "\"min_avail_mb\":6144", "\"min_avail_mb\":-1")));
    CHECK(!dm::parse_record(with(u, "\"max_wait_s\":900", "\"max_wait_s\":\"900\"")));
    CHECK(dm::parse_record(with(u, "\"waiting_for\":\"memory\"", "\"waiting_for\":null")).has_value());
    CHECK(dm::utc_seconds("1970-01-01T00:00:00Z") == 0 && dm::utc_seconds("2026-11-13T04:00:05Z") == 1794542405);
    CHECK(!dm::utc_seconds("2026-13-01T00:00:00Z") && !dm::utc_seconds("2026-11-13 04:00:05Z") && !dm::utc_seconds(""));
}

static void options() {
    // Whole numbers only: no sign, space, prefix or trailing text, and within the flag's bounds.
    for (const char* bad : {"", "-1", "+1", "12x", " 12", "12 ", "0x10", "1.5", "123456789012345678901234567890", "18446744073709551616"})
        CHECK(!dm::parse_count(bad, 0, dm::kMaxQuantity));
    CHECK(dm::parse_count("0", 0, 10) == 0u && dm::parse_count("10", 0, 10) == 10u && dm::parse_count("007", 0, 10) == 7u);
    CHECK(!dm::parse_count("11", 0, 10) && !dm::parse_count("0", 1, 10));
    CHECK(dm::parse_count("18446744073709551615", 0, UINT64_MAX) == UINT64_MAX);
    CHECK(dm::parse_count("1099511627776", 0, dm::kMaxQuantity) == dm::kMaxQuantity && !dm::parse_count("1099511627777", 0, dm::kMaxQuantity));
}

// A small JSON reader for the tests alone, so status --json is checked by parsing it, not by searching its text.
struct Json {
    enum Kind { Null, Bool, Number, String, Array, Object } kind = Null;
    bool b = false;
    double n = 0;
    std::string s;
    std::vector<Json> items;
    std::map<std::string, Json> fields;
    const Json& operator[](const char* k) const {
        static const Json none;
        auto it = fields.find(k);
        return it == fields.end() ? none : it->second;
    }
    const Json& item(size_t i) const {
        static const Json none;
        return i < items.size() ? items[i] : none;
    }
    bool has(const char* k) const { return fields.count(k) != 0; }
};

static bool parse_json(const std::string& t, size_t& i, Json& out) {
    auto ws = [&] { while (i < t.size() && (t[i] == ' ' || t[i] == '\n' || t[i] == '\r' || t[i] == '\t')) ++i; };
    ws();
    if (i >= t.size()) return false;
    char c = t[i];
    if (c == '{' || c == '[') {
        bool object = c == '{';
        out.kind = object ? Json::Object : Json::Array;
        ++i;
        ws();
        if (i < t.size() && t[i] == (object ? '}' : ']')) return ++i, true;
        for (;;) {
            std::string key;
            if (object) {
                Json k;
                if (!parse_json(t, i, k) || k.kind != Json::String) return false;
                key = k.s;
                ws();
                if (i >= t.size() || t[i++] != ':') return false;
            }
            Json v;
            if (!parse_json(t, i, v)) return false;
            if (object) {
                if (out.fields.count(key)) return false;
                out.fields[key] = v;
            } else out.items.push_back(v);
            ws();
            if (i < t.size() && t[i] == ',') { ++i; continue; }
            if (i < t.size() && t[i] == (object ? '}' : ']')) return ++i, true;
            return false;
        }
    }
    if (c == '"') {
        // Strings go through the records' own reader, which handles every escape.
        size_t end = i + 1;
        while (end < t.size() && t[end] != '"') end += t[end] == '\\' ? 2 : 1;
        if (end >= t.size()) return false;
        auto flat = dm::parse_flat_object("{\"v\":" + t.substr(i, end - i + 1) + "}");
        if (!flat) return false;
        out.kind = Json::String;
        out.s = flat->at("v").text;
        i = end + 1;
        return true;
    }
    for (const char* word : {"null", "true", "false"}) {
        if (t.compare(i, std::strlen(word), word) == 0) {
            out.kind = word[0] == 'n' ? Json::Null : Json::Bool;
            out.b = word[0] == 't';
            i += std::strlen(word);
            return true;
        }
    }
    size_t start = i;
    if (i < t.size() && t[i] == '-') ++i;
    while (i < t.size() && ((t[i] >= '0' && t[i] <= '9') || t[i] == '.')) ++i;
    if (i == start) return false;
    out.kind = Json::Number;
    out.n = std::strtod(t.substr(start, i - start).c_str(), nullptr);
    return true;
}

static std::optional<Json> json(const std::string& t) {
    size_t i = 0;
    Json out;
    if (!parse_json(t, i, out)) return std::nullopt;
    while (i < t.size() && (t[i] == ' ' || t[i] == '\n' || t[i] == '\r')) ++i;
    if (i != t.size()) return std::nullopt;
    return out;
}

static void report_json() {
    // CSV names come back decoded, exactly as both writers encode them: a literal "%2C" and a comma stay apart.
    auto lit = dm::parse_row("2026-11-12T10:00:05,win,repo,D:/a%252Cb,1,10,5");
    auto comma = dm::parse_row("2026-11-12T10:00:05,win,repo,D:/a%2Cb,1,20,6");
    CHECK(lit && comma && lit->name == "D:/a%2Cb" && comma->name == "D:/a,b");
    CHECK(dm::csv_unname(dm::csv_name("q\"x%y,z\r\n")) == "q\"x%y,z\r\n" && dm::csv_unname("%zz%4") == "%zz%4");
    CHECK(!dm::parse_row("2026-11-12T10:00:05,win,sys,available_mb,0,12x,0") && !dm::parse_row("2026-11-12T10:0,win,sys,available_mb,0,1,0"));
    // A name that decodes to bytes that aren't UTF-8 is refused, so the JSON stays valid (round 1); real UTF-8 passes.
    CHECK(!dm::parse_row("2026-11-12T10:00:05,win,repo,%FF,1,100,100") && !dm::parse_row("2026-11-12T10:00:05,win,repo,a%C3,1,1,1"));
    auto utf = dm::parse_row("2026-11-12T10:00:05,wsl,repo,/home/u/caf%C3%A9,1,1,1");
    CHECK(utf && utf->name == "/home/u/caf\xc3\xa9");
    CHECK(dm::valid_utf8("caf\xc3\xa9 \xf0\x9f\x98\x80") && !dm::valid_utf8("\xc0\xaf") && !dm::valid_utf8("\xed\xa0\x80") && !dm::valid_utf8("\xf4\x90\x80\x80"));
    // A stamp that is 19 bytes but no time, and quantities past what any sampler writes, are refused (round 3).
    CHECK(!dm::parse_row("2026-11-13T23:59:5\xff,win,repo,test,1,100,100") && !dm::parse_row("2026-13-13T23:59:59,win,repo,test,1,100,100"));
    CHECK(!dm::parse_row("2026-11-13T23:59:59,win,repo,test,1,9223372036854775807,1") && !dm::parse_row("2026-11-13T23:59:59,win,repo,test,-1,1,1"));
    CHECK(dm::parse_row("2026-11-13T23:59:59,win,repo,test,1,1099511627776,1").has_value() && !dm::parse_row("2026-11-13T23:59:59,win,repo,test,1,1099511627777,1"));
    // With each quantity bounded at 2^40, thousands of repos at the bound in one minute still total exactly; only past
    // millions of them would the sum saturate (at INT64_MAX / 2), never wrap negative.
    std::vector<dm::Row> big;
    for (int k = 0; k < 9000; ++k) big.push_back({"2026-11-13T10:00:00", "win", "repo", "r" + std::to_string(k), 1, int64_t{1} << 40, 1});
    dm::Report bg = dm::aggregate(big);
    CHECK(bg.busiest_total_mb == 9000 * (int64_t{1} << 40) && bg.active_total_mb == bg.busiest_total_mb && bg.active_max == 9000);

    // Two days of both sides, in the writers' own row format.
    std::vector<std::string> csv{
        "2026-11-12T10:00:05,win,sys,available_mb,0,9000,0",
        "2026-11-12T10:00:05,win,sys,commit_mb,0,30000,0",
        "2026-11-12T10:00:05,win,sys,commit_limit_mb,0,80000,0",
        "2026-11-12T10:00:05,win,repo,D:/A,3,1000,800",
        "2026-11-12T10:00:35,win,sys,available_mb,0,8000,0",
        "2026-11-12T10:00:35,win,repo,D:/A,5,1200,700",
        "2026-11-12T10:00:20,wsl,sys,mem_available_mb,0,7000,0",
        "2026-11-12T10:00:20,wsl,sys,swap_used_mb,0,100,0",
        "2026-11-12T10:00:20,wsl,sys,oom_kills,0,1,0",
        "2026-11-12T10:00:20,wsl,repo,/home/u/b%2C\"c,2,4000,3000",
        "2026-11-13T09:00:05,win,sys,available_mb,0,1500,0",
        "2026-11-13T09:00:05,win,sys,pages_in_per_sec,0,77,0",
        "2026-11-13T09:00:05,win,repo,D:/A,4,3000,2500",
        "2026-11-13T09:00:05,win,repo,D:/Q\\uote,1,50,40",
    };
    std::vector<dm::Row> rows;
    for (const auto& line : csv) {
        auto r = dm::parse_row(line);
        CHECK(r.has_value());
        if (r) rows.push_back(*r);
    }
    dm::Report r = dm::aggregate(rows);
    std::vector<dm::JobLine> runs(4);
    runs[0].ended_at = "2026-11-13T09:00:10Z"; runs[0].ran_s = 10;  // 09:00:00 to 09:00:10
    runs[1].ended_at = "2026-11-13T09:00:08Z"; runs[1].ran_s = 4;   // 09:00:04 to 09:00:08: overlaps the first
    runs[2].ended_at = "2026-11-13T09:00:20Z"; runs[2].ran_s = 0;   // only waited
    runs[3].ended_at = "2026-11-01T00:00:00Z"; runs[3].ran_s = 50;  // outside the window
    dm::BuildOverlap b = dm::build_overlap(runs, *dm::utc_seconds("2026-11-06T00:00:00Z"));
    CHECK(b.jobs == 3 && b.max_overlap == 2 && b.at && dm::utc_stamp(*b.at) == "2026-11-13T09:00:04Z");
    // Two runs that follow each other within one minute don't overlap; a job that never ran counts for nothing.
    std::vector<dm::JobLine> follow(2);
    follow[0].ended_at = "2026-11-13T09:00:10Z"; follow[0].ran_s = 10;
    follow[1].ended_at = "2026-11-13T09:00:30Z"; follow[1].ran_s = 20;  // starts as the first ends
    CHECK(dm::build_overlap(follow, 0).max_overlap == 1);
    std::vector<dm::JobLine> waited(1);
    waited[0].ended_at = "2026-11-13T09:00:10Z";
    CHECK(dm::build_overlap(waited, 0).max_overlap == 0 && !dm::build_overlap(waited, 0).at);
    // Completion review: runs are clipped to the window, so one that began before it counts from `since` and one that
    // ended as the window began counts for nothing; and in milliseconds, two jobs run one after the other within the
    // same second (both ending at 09:00:10) don't overlap, while two that truly share 200 ms do.
    int64_t cut = *dm::utc_seconds("2026-11-13T09:00:00Z");
    std::vector<dm::JobLine> span(2);
    span[0].ended_at = "2026-11-13T09:00:05Z"; span[0].ran_s = 60;  // began a minute before the cutoff
    span[1].ended_at = "2026-11-13T09:00:00Z"; span[1].ran_s = 30;  // ended exactly at it
    dm::BuildOverlap sp = dm::build_overlap(span, cut);
    CHECK(sp.jobs == 2 && sp.max_overlap == 1 && sp.at && *sp.at == cut);
    std::vector<dm::JobLine> quick(2);
    quick[0].ended_at = "2026-11-13T09:00:10Z"; quick[0].ended_ms = cut * 1000 + 10200; quick[0].ran_ms = 300;  // 09:00:09.900 to .200
    quick[1].ended_at = "2026-11-13T09:00:10Z"; quick[1].ended_ms = cut * 1000 + 10700; quick[1].ran_ms = 400;  // 09:00:10.300 to .700
    CHECK(dm::build_overlap(quick, 0).max_overlap == 1);
    quick[1].ended_ms = cut * 1000 + 10500;
    quick[1].ran_ms = 500;  // 09:00:10.000 to .500, sharing 200 ms with the first
    CHECK(dm::build_overlap(quick, 0).max_overlap == 2);
    // The millisecond fields round-trip, and a line from before them still reads, with them null.
    dm::JobLine ms = quick[0];
    ms.started_at = "2026-11-13T09:00:09Z";
    ms.side = "windows";
    auto msb = dm::parse_job_line(dm::job_line_json(ms));
    CHECK(msb && msb->ended_ms == ms.ended_ms && msb->ran_ms == 300);
    dm::JobLine old = ms;
    old.ended_ms.reset();
    old.ran_ms.reset();
    auto oldb = dm::parse_job_line(dm::job_line_json(old));
    CHECK(oldb && !oldb->ended_ms && !oldb->ran_ms);
    CHECK(dm::utc_stamp(0) == "1970-01-01T00:00:00Z" && dm::utc_stamp(*dm::utc_seconds("2028-02-29T23:59:59Z")) == "2028-02-29T23:59:59Z");
    // A job line whose time isn't one, or whose duration is negative, is refused (and so counted), never read and then
    // dropped where its time is used (round 4).
    CHECK(!dm::parse_job_line("{\"schema_version\":1,\"started_at\":\"2026-11-13T09:00:00Z\",\"ended_at\":\"broken\",\"pid\":1,\"command\":\"build\",\"repo\":null,\"side\":\"windows\",\"ns\":\"default\",\"slot\":null,\"waited_s\":0,\"ran_s\":10,\"exit_code\":0,\"timed_out_into_queue\":false}"));
    dm::JobLine neg = runs[0];
    neg.started_at = "2026-11-13T09:00:00Z";
    neg.side = "windows";
    CHECK(dm::parse_job_line(dm::job_line_json(neg)).has_value());
    std::string negText = dm::job_line_json(neg);
    negText.replace(negText.find("\"ran_s\":10.0"), 12, "\"ran_s\":-1.0");
    CHECK(!dm::parse_job_line(negText));

    dm::ReadStatus read;
    read.files = 3;
    auto j = json(dm::report_json(r, b, read, "2026-11-13T10:00:00Z", "2026-11-06T10:00:00Z", 7));
    CHECK(j.has_value());
    if (!j) return;
    const Json& s = *j;
    CHECK(s["schema_version"].n == 1 && s["days_requested"].n == 7 && s["days_covered"].n == 2 && s["since"].s == "2026-11-06T10:00:00Z");
    CHECK(s["first_sample"].s == "2026-11-12T10:00:05" && s["last_sample"].s == "2026-11-13T09:00:05");
    CHECK(s["read"]["complete"].b && s["read"]["files"].n == 3 && s["read"]["rejected_rows"].n == 0);
    CHECK(s["windows"]["samples"].n == 3 && s["windows"]["low_samples"].n == 1 && s["windows"]["min_available_mb"].n == 1500);
    CHECK(s["windows"]["worst_minute"].s == "2026-11-13T09:00" && s["windows"]["max_commit_mb"].n == 30000 && s["windows"]["max_pages_in_per_sec"].n == 77);
    CHECK(s["windows"]["worst_repos"].item(0)["repo"].s == "D:/A" && s["windows"]["worst_repos"].item(0)["private_mb"].n == 3000);
    CHECK(s["wsl"]["samples"].n == 1 && s["wsl"]["min_available_mb"].n == 7000 && s["wsl"]["max_swap_mb"].n == 100 && s["wsl"]["max_oom_kills"].n == 1);
    CHECK(s["wsl"]["max_psi_full_avg10_x100"].kind == Json::Null);  // never sampled: null, not 0
    CHECK(s["builds"]["jobs"].n == 3 && s["builds"]["max_overlap"].n == 2 && s["builds"]["at"].s == "2026-11-13T09:00:04Z");
    CHECK(s["active_repos"]["max"].n == 2 && s["active_repos"]["minute"].s == "2026-11-12T10:00" && s["active_repos"]["total_private_mb"].n == 5200);
    // Repos by peak private memory, highest first, each name decoded and read back unchanged.
    const Json& repos = s["repos"];
    CHECK(repos.items.size() == 3);
    CHECK(repos.item(0)["repo"].s == "/home/u/b,\"c" && repos.item(0)["side"].s == "wsl" && repos.item(0)["peak_private_mb"].n == 4000);
    CHECK(repos.item(1)["repo"].s == "D:/A" && repos.item(1)["peak_private_mb"].n == 3000 && repos.item(1)["peak_rss_mb"].n == 2500 && repos.item(1)["peak_processes"].n == 5);
    CHECK(repos.item(2)["repo"].s == "D:/Q\\uote");
    // The series: one entry per sampled minute, oldest first, each side null where it has no sample.
    const Json& series = s["series"];
    CHECK(series.items.size() == 2);
    CHECK(series.item(0)["minute"].s == "2026-11-12T10:00" && series.item(0)["win_available_mb"].n == 8000 && series.item(0)["wsl_available_mb"].n == 7000);
    CHECK(series.item(0)["repos"].n == 2 && series.item(0)["repo_private_mb"].n == 5200);
    CHECK(series.item(1)["minute"].s == "2026-11-13T09:00" && series.item(1)["win_available_mb"].n == 1500 && series.item(1)["wsl_available_mb"].kind == Json::Null);

    // The most repos active at once is a count, not the count of the minute with the most memory (round 2): two repos
    // of 100 MB at 10:00 and one of 500 MB at 10:01 make 2, at 10:00.
    std::vector<dm::Row> counted{{"2026-11-12T10:00:05", "win", "repo", "D:/x", 1, 100, 1},
                                 {"2026-11-12T10:00:05", "win", "repo", "D:/y", 1, 100, 1},
                                 {"2026-11-12T10:01:05", "win", "repo", "D:/z", 1, 500, 1}};
    auto ac = json(dm::report_json(dm::aggregate(counted), dm::BuildOverlap{}, read, "t", "s", 7));
    CHECK(ac && (*ac)["active_repos"]["max"].n == 2 && (*ac)["active_repos"]["minute"].s == "2026-11-12T10:00" && (*ac)["active_repos"]["total_private_mb"].n == 200);

    // The window: only samples at or after `since` count, for 1, 7 and 14 days alike, one second either side.
    for (int days : {1, 7, 14}) {
        int64_t now = *dm::utc_seconds("2026-11-20T12:00:00Z");
        std::string since = dm::utc_stamp(now - int64_t{days} * 86400).substr(0, 19);
        std::string before = dm::utc_stamp(now - int64_t{days} * 86400 - 1).substr(0, 19);
        std::vector<dm::Row> edge{{before, "win", "sys", "available_mb", 0, 100, 0}, {since, "win", "sys", "available_mb", 0, 5000, 0}};
        dm::Report w = dm::aggregate(edge, 2048, since);
        CHECK(w.win_samples == 1 && w.min_avail_mb == 5000 && w.first_sample == since && w.dates.size() == 1);
    }

    // A Windows-only history: the WSL figures are null, never 0; no samples at all give 0 days and an empty series.
    std::vector<dm::Row> winOnly{rows[0], rows[1]};
    auto wo = json(dm::report_json(dm::aggregate(winOnly), dm::BuildOverlap{}, read, "t", "s", 7));
    CHECK(wo && (*wo)["wsl"]["min_available_mb"].kind == Json::Null && (*wo)["wsl"]["max_swap_mb"].kind == Json::Null && (*wo)["wsl"]["samples"].n == 0);
    CHECK(wo && (*wo)["windows"]["max_pages_in_per_sec"].kind == Json::Null && (*wo)["builds"]["at"].kind == Json::Null);
    dm::ReadStatus partial;
    partial.complete = false;
    partial.unreadable_files = 1;
    partial.rejected_rows = 2;
    auto none = json(dm::report_json(dm::aggregate({}), dm::BuildOverlap{}, partial, "t", "s", 1));
    CHECK(none && (*none)["days_covered"].n == 0 && (*none)["series"].items.empty() && (*none)["first_sample"].kind == Json::Null);
    CHECK(none && (*none)["windows"]["min_available_mb"].kind == Json::Null && !(*none)["read"]["complete"].b && (*none)["read"]["unreadable_files"].n == 1);
    CHECK(none && (*none)["read"]["rejected_rows"].n == 2 && (*none)["active_repos"]["minute"].kind == Json::Null);
    // The text report reads the same join.
    CHECK(dm::render(r).find("lowest available: 1500 MB at 2026-11-13T09:00") != std::string::npos);

    // The live snapshot's schema, the same for both sides.
    dm::Snapshot snap;
    snap.side = "windows";
    snap.at = "2026-11-13T09:00:05Z";
    snap.interval_s = 30;
    snap.memory = {{"available_mb", 1500}, {"total_mb", 32000}, {"pages_in_per_sec", std::nullopt}};
    snap.wsl_vm = dm::WslVm{true, 1, 14000, 5000};
    snap.repos = {{"D:/a\"b\\c", 3, 100, 80}};
    snap.processes = {{"cl.exe", 4, 900, 700}, {"link.exe", 1, 300, 200}};
    auto sj = json(dm::snapshot_json(snap));
    CHECK(sj.has_value());
    if (!sj) return;
    CHECK((*sj)["schema_version"].n == 1 && (*sj)["side"].s == "windows" && (*sj)["at"].s == snap.at && (*sj)["interval_s"].n == 30);
    CHECK((*sj)["memory"]["available_mb"].n == 1500 && (*sj)["memory"]["total_mb"].n == 32000 && (*sj)["memory"]["pages_in_per_sec"].kind == Json::Null);
    CHECK((*sj)["wsl_vm"]["running"].b && (*sj)["wsl_vm"]["private_mb"].n == 14000 && (*sj)["repos"].item(0)["repo"].s == "D:/a\"b\\c");
    CHECK((*sj)["processes"].items.size() == 2 && (*sj)["processes"].item(0)["name"].s == "cl.exe" && (*sj)["processes"].item(1)["rss_mb"].n == 200);
    snap.wsl_vm.reset();
    auto sw = json(dm::snapshot_json(snap));
    CHECK(sw && (*sw).has("wsl_vm") && (*sw)["wsl_vm"].kind == Json::Null);
}

static void config() {
    // A file round-trips: the keys it sets, in table order, and a key this version doesn't know kept as it was.
    dm::ConfigFile f = dm::parse_config("{\"schema_version\":1,\"max_wait_s\":0,\"slots\":3,\"future\":\"x\\\"y\"}");
    CHECK(f.valid && f.refused.empty() && f.values.at("slots") == 3 && f.values.at("max_wait_s") == 0 && f.unknown.size() == 1);
    dm::ConfigFile back = dm::parse_config(dm::config_json(f));
    CHECK(back.valid && back.values == f.values && back.unknown.size() == 1 && back.unknown[0].second.text == "x\"y");
    CHECK(dm::config_json(f).find("\"slots\": 3,\n  \"max_wait_s\": 0") != std::string::npos);  // table order
    // The whole file is refused, every key falling back, for text that isn't one object or isn't this version's.
    for (const char* bad : {"{\"schema_version\":1,\"slots\":", "[]", "", "{\"slots\":3}", "{\"schema_version\":2,\"slots\":3}",
                            "{\"schema_version\":1,\"slots\":3,\"slots\":4}"}) {
        dm::ConfigFile r = dm::parse_config(bad);
        CHECK(!r.valid && !r.error.empty() && r.values.empty());
    }
    // One key is refused, with its reason, for a value outside its bounds or not a whole number; its siblings are kept.
    const std::vector<std::pair<std::string, std::string>> badKeys{{"slots", "0"},          {"slots", "65"},          {"slots", "\"2\""},
                                                                   {"min_avail_mb", "-1"},   {"max_wait_s", "604801"}, {"min_guest_mb", "1.5"}};
    for (const auto& [key, value] : badKeys) {
        std::string text = "{\"schema_version\":1";
        for (const auto& spec : dm::limit_specs())  // every other key, valid, beside the bad one
            if (key != spec.key) text += ",\"" + std::string(spec.key) + "\":" + std::to_string(spec.min + 1);
        text += ",\"" + key + "\":" + value + "}";
        dm::ConfigFile r = dm::parse_config(text);
        CHECK(r.valid && r.refused.size() == 1 && r.refused[0].first == key && r.refused[0].second.find("whole number from") != std::string::npos);
        CHECK(r.values.size() == 3 && !r.values.count(key));
    }
    CHECK(dm::parse_config("{\"schema_version\":1,\"slots\":64,\"max_wait_s\":604800}").values.size() == 2);  // the bounds themselves
    // Round 1: an unknown decimal written back as %.17g reads again (exponents are JSON), and an integer past 64 bits
    // refuses its own key, keeping its siblings; an unknown one is written back with its own digits.
    dm::ConfigFile tiny = dm::parse_config("{\"schema_version\":1,\"slots\":1,\"future\":0.000001}");
    dm::ConfigFile tinyBack = dm::parse_config(dm::config_json(tiny));
    CHECK(tiny.valid && tinyBack.valid && tinyBack.values.at("slots") == 1 && tinyBack.unknown.size() == 1);
    CHECK(tinyBack.unknown.size() == 1 && tinyBack.unknown[0].second.decimal > 0.0000009 && tinyBack.unknown[0].second.decimal < 0.0000011);
    auto exp = dm::parse_flat_object("{\"a\":1e-6,\"b\":2.5E+3,\"c\":7e2}");
    CHECK(exp && exp->at("a").kind == dm::FlatValue::Decimal && exp->at("b").decimal == 2500 && exp->at("c").decimal == 700);
    CHECK(!dm::parse_flat_object("{\"a\":1e}") && !dm::parse_flat_object("{\"a\":1e9999}"));
    // Completion review: a leading zero is no JSON number, so the whole config file is refused rather than kept.
    CHECK(!dm::parse_flat_object("{\"a\":01}") && !dm::parse_flat_object("{\"a\":-01}") && !dm::parse_flat_object("{\"a\":00.5}"));
    auto zeros = dm::parse_flat_object("{\"a\":0,\"b\":-0,\"c\":0.5,\"d\":10}");
    CHECK(zeros && zeros->at("a").magnitude == 0 && zeros->at("c").decimal == 0.5 && zeros->at("d").magnitude == 10);
    CHECK(!dm::parse_config("{\"schema_version\":1,\"slots\":01}").valid && !dm::parse_config("{\"schema_version\":1,\"later\":007}").valid);
    // Round 4: a long exponent is read whole: 0e9999 and 1e-9999 are 0, and in config.json 1e9999 refuses only its key.
    auto longExp = dm::parse_flat_object("{\"a\":0e9999,\"b\":1e-9999}");
    CHECK(longExp && longExp->at("a").decimal == 0 && longExp->at("b").decimal == 0);
    dm::ConfigFile longCfg = dm::parse_config("{\"schema_version\":1,\"slots\":1,\"min_guest_mb\":1e9999}");
    CHECK(longCfg.valid && longCfg.values.at("slots") == 1 && longCfg.refused.size() == 1 && longCfg.refused[0].first == "min_guest_mb");
    dm::ConfigFile big = dm::parse_config("{\"schema_version\":1,\"slots\":1,\"min_guest_mb\":18446744073709551616}");
    CHECK(big.valid && big.values.at("slots") == 1 && big.refused.size() == 1 && big.refused[0].first == "min_guest_mb");
    CHECK(!dm::parse_flat_object("{\"a\":18446744073709551616}"));  // every other reader still refuses the object
    dm::ConfigFile bigUnknown = dm::parse_config("{\"schema_version\":1,\"later\":123456789012345678901234567890}");
    CHECK(bigUnknown.valid && dm::config_json(bigUnknown).find("\"later\": 123456789012345678901234567890") != std::string::npos);
    CHECK(!dm::parse_config("{\"schema_version\":18446744073709551617}").valid);
    // Round 2: unknown numbers go back exactly as read: an unsigned one past INT64_MAX, one past a double's range, and
    // one with an exponent, each survives a save and reads again.
    dm::ConfigFile odd = dm::parse_config("{\"schema_version\":1,\"slots\":1,\"u\":18446744073709551615,\"huge\":1e309,\"e\":2.50E-3}");
    std::string written = dm::config_json(odd);
    CHECK(odd.valid && written.find("\"u\": 18446744073709551615") != std::string::npos && written.find("\"huge\": 1e309") != std::string::npos);
    CHECK(written.find("\"e\": 2.50E-3") != std::string::npos && written.find("inf") == std::string::npos && written.find("-1") == std::string::npos);
    // Round 3: 0e309 is zero, not NaN, and a value past a double's range is refused by every reader but config.json's
    // unknown keys, so no job line can carry a non-finite duration.
    auto zero = dm::parse_flat_object("{\"a\":0e309}");
    CHECK(zero && zero->at("a").decimal == 0 && !dm::parse_flat_object("{\"a\":1e309}") && !dm::parse_flat_object("{\"a\":-1e309}"));
    CHECK(!dm::parse_job_line("{\"schema_version\":1,\"started_at\":\"2026-11-13T09:00:00Z\",\"ended_at\":\"2026-11-13T09:00:10Z\",\"pid\":1,\"command\":\"b\",\"repo\":null,\"side\":\"windows\",\"ns\":\"d\",\"slot\":null,\"waited_s\":0,\"ran_s\":1e309,\"exit_code\":0,\"timed_out_into_queue\":false}"));
    dm::ConfigFile oddBack = dm::parse_config(written);
    CHECK(oddBack.valid && oddBack.values.at("slots") == 1 && oddBack.unknown.size() == 3 && dm::config_json(oddBack) == written);
    // Precedence: flag, then a valid variable, then the file, then the default, each named.
    const dm::LimitSpec& slots = *dm::find_limit("slots");
    CHECK(dm::resolve_limit(slots, 7, std::string("5"), 3).value == 7 && dm::resolve_limit(slots, 7, std::string("5"), 3).source == "flag");
    CHECK(dm::resolve_limit(slots, std::nullopt, std::string("5"), 3).value == 5 && dm::resolve_limit(slots, std::nullopt, std::string("5"), 3).source == "environment");
    CHECK(dm::resolve_limit(slots, std::nullopt, std::nullopt, 3).value == 3 && dm::resolve_limit(slots, std::nullopt, std::nullopt, 3).source == "file");
    CHECK(dm::resolve_limit(slots, std::nullopt, std::nullopt, std::nullopt).value == 2 && dm::resolve_limit(slots, std::nullopt, std::nullopt, std::nullopt).source == "default");
    // A bad variable is skipped for its key alone, with its reason: the saved value then wins.
    dm::ResolvedLimit skipped = dm::resolve_limit(slots, std::nullopt, std::string("3x"), 4);
    CHECK(skipped.value == 4 && skipped.source == "file" && skipped.env_error.find("BOSUN_SLOTS=3x") != std::string::npos);
    CHECK(dm::resolve_limit(slots, std::nullopt, std::string("99"), std::nullopt).source == "default");  // past the bound
    dm::ResolvedLimit good = dm::resolve_limit(*dm::find_limit("max_wait_s"), std::nullopt, std::string("30"), 5);
    CHECK(good.value == 30 && good.source == "environment" && good.env_error.empty());
    CHECK(!dm::find_limit("nonsense") && dm::limit_specs().size() == 4);
}

static void status_json() {
    auto running = [](uint64_t start) { return [start](int64_t) { return dm::ProbeAnswer{dm::ProbeAnswer::Running, start}; }; };
    (void)running;
    auto rec = [](const char* kind, int64_t pid, const char* at) {
        dm::SlotRecord r;
        r.kind = kind;
        r.pid = pid;
        r.process_start = 1000 + static_cast<uint64_t>(pid);
        r.started_at = at;
        r.command = "cmake --build build";
        r.repo = "D:/src/Bosun";
        r.side = "windows";
        r.session = 1;
        r.ns = "Bosun";
        return r;
    };
    // Five slots: 0 busy with a live holder that is running; 1 free, holding a killed holder's leftover; 2 busy with no
    // record; 3 busy with a WSL holder (state unknown) waiting for guest memory; 4 can't be probed, no record.
    dm::SlotRecord h0 = rec("holder", 10, "2026-11-13T04:00:00Z");
    h0.slot = 0;
    h0.acquired_at = "2026-11-13T04:00:30Z";
    h0.command_started_at = "2026-11-13T04:00:31Z";
    h0.command = "\"C:\\Program Files\\x.exe\" --name \"q\" a\tb";
    dm::SlotRecord left1 = rec("holder", 11, "2026-11-13T03:00:00Z");
    left1.slot = 1;
    left1.acquired_at = "2026-11-13T03:00:00Z";
    dm::SlotRecord h3 = rec("holder", 13, "2026-11-13T04:01:00Z");
    h3.slot = 3;
    h3.side = "wsl";
    h3.acquired_at = "2026-11-13T04:01:10Z";
    h3.min_guest_mb = 3072;
    // Waiters: an old one (given last, printed first), a newer one, a stale one, and one superseded by its own holder.
    dm::SlotRecord wNew = rec("waiter", 20, "2026-11-13T04:02:00Z");
    wNew.waiting_for = "memory";
    wNew.min_avail_mb = 6144;
    wNew.max_wait_s = 900;
    dm::SlotRecord wOld = rec("waiter", 21, "2026-11-13T04:01:30Z");
    wOld.waiting_for = "slot";
    dm::SlotRecord wStale = rec("waiter", 22, "2026-11-13T04:00:00Z");
    dm::SlotRecord wSuper = rec("waiter", 10, "2026-11-13T04:00:00Z");  // h0's own process, before it took slot 0
    dm::StatusInput in;
    in.ns = "Bo\\sun \"q\"";
    in.at = "2026-11-13T04:03:00Z";
    in.busy = {true, false, true, true, std::nullopt};
    in.records = {{h0, dm::RecordState::Live},   {left1, dm::RecordState::Live},   {h3, dm::RecordState::Unknown},
                  {wNew, dm::RecordState::Live}, {wStale, dm::RecordState::Stale}, {wSuper, dm::RecordState::Live},
                  {wOld, dm::RecordState::Live}};
    in.records_readable = true;
    in.records_skipped = 2;
    in.records_dir = "C:\\Users\\a\\AppData\\Local\\Bosun\\run\\1\\ns-%42osun";
    in.available_mb = 12000;
    in.total_mb = 32000;
    in.min_avail_mb = 6144;
    std::string text = dm::status_json(in);
    CHECK(text.find('\n') == std::string::npos);  // one line
    auto j = json(text);
    CHECK(j.has_value());
    if (!j) return;
    const Json& s = *j;
    CHECK(s["schema_version"].n == 1 && s["ns"].s == in.ns && s["at"].s == in.at);
    CHECK(s["slots"].items.size() == 5);
    const Json& s0 = s["slots"].item(0);
    CHECK(s0["index"].n == 0 && s0["busy"].kind == Json::Bool && s0["busy"].b);
    CHECK(s0["holder"]["pid"].n == 10 && s0["holder"]["state"].s == "live" && s0["holder"]["phase"].s == "running");
    CHECK(s0["holder"]["command"].s == h0.command && s0["holder"]["repo"].s == "D:/src/Bosun" && s0["holder"]["slot"].n == 0);
    CHECK(s0["holder"]["waited_s"].n == 30 && s0["holder"]["running_s"].n == 149);
    const Json& s1 = s["slots"].item(1);
    CHECK(s1["busy"].kind == Json::Bool && !s1["busy"].b && s1["holder"].kind == Json::Null);  // the leftover names nobody
    const Json& s2 = s["slots"].item(2);
    CHECK(s2["busy"].b && s2["holder"].kind == Json::Null);
    const Json& s3 = s["slots"].item(3);
    CHECK(s3["holder"]["state"].s == "unknown" && s3["holder"]["phase"].s == "waiting for guest memory");
    CHECK(s3["holder"]["running_s"].kind == Json::Null && s3["holder"]["waited_s"].n == 10 && s3["holder"]["min_guest_mb"].n == 3072);
    const Json& s4 = s["slots"].item(4);
    CHECK(s4["busy"].kind == Json::Null && s4["holder"].kind == Json::Null);
    CHECK(s0["above_limit"].kind == Json::Bool && !s0["above_limit"].b && !s4["above_limit"].b);  // no limit given: none above

    // D03 T02 §3: a holder of a slot above a lowered limit keeps it listed, marked above the limit.
    dm::SlotRecord high = rec("holder", 30, "2026-11-13T04:00:00Z");
    high.slot = 1;
    dm::SlotRecord gone = rec("holder", 31, "2026-11-13T04:00:00Z");
    gone.slot = 5;
    CHECK(dm::slots_to_probe(1, {{high, dm::RecordState::Live}}) == 2);
    CHECK(dm::slots_to_probe(1, {{high, dm::RecordState::Unknown}}) == 2);
    CHECK(dm::slots_to_probe(1, {{gone, dm::RecordState::Stale}}) == 1);  // a stale record names nothing
    CHECK(dm::slots_to_probe(3, {{high, dm::RecordState::Live}}) == 3);
    dm::SlotRecord far = rec("holder", 32, "2026-11-13T04:00:00Z");
    far.slot = 200;
    CHECK(dm::slots_to_probe(1, {{far, dm::RecordState::Live}}) == 64);
    dm::StatusInput lowered;
    lowered.ns = "Bosun";
    lowered.at = in.at;
    lowered.busy = {true, true};
    lowered.limit = 1;
    lowered.records = {{high, dm::RecordState::Live}};
    auto low = json(dm::status_json(lowered));
    CHECK(low.has_value());
    if (low) {
        CHECK(!(*low)["slots"].item(0)["above_limit"].b);
        CHECK((*low)["slots"].item(1)["above_limit"].b && (*low)["slots"].item(1)["holder"]["pid"].n == 30);
    }
    // Waiters: oldest first, the stale and the superseded left out, each with its reason, limits and wait so far.
    CHECK(s["waiters"].items.size() == 2);
    CHECK(s["waiters"].item(0)["pid"].n == 21 && s["waiters"].item(0)["waiting_for"].s == "slot" && s["waiters"].item(0)["waited_s"].n == 90);
    CHECK(s["waiters"].item(1)["pid"].n == 20 && s["waiters"].item(1)["waiting_for"].s == "memory" && s["waiters"].item(1)["min_avail_mb"].n == 6144);
    CHECK(s["waiters"].item(1)["max_wait_s"].n == 900 && s["waiters"].item(1)["state"].s == "live" && !s["waiters"].item(1).has("slot"));
    CHECK(s["memory"]["available_mb"].n == 12000 && s["memory"]["total_mb"].n == 32000 && s["memory"]["min_avail_mb"].n == 6144);
    // A Windows call: no guest, no WSL logger.
    CHECK(s.has("guest") && s["guest"].kind == Json::Null && s["logger"]["windows"].s == "not running" && s["logger"]["wsl"].kind == Json::Null);
    CHECK(s["records"]["readable"].b && s["records"]["skipped"].n == 2 && s["records"]["dir"].s == in.records_dir);

    // A WSL call with failed memory reads: null, never 0; the guest and its logger as the shim gave them.
    dm::StatusInput w2;
    w2.ns = "Bosun";
    w2.at = in.at;
    w2.busy = {false};
    w2.records_readable = false;
    w2.guest = dm::GuestFigures{4000, 16000, 3072};
    w2.windows_logger = dm::logger_state(true, 0);
    w2.wsl_logger = "unknown";
    auto k = json(dm::status_json(w2));
    CHECK(k.has_value());
    if (!k) return;
    CHECK((*k)["memory"]["available_mb"].kind == Json::Null && (*k)["memory"]["total_mb"].kind == Json::Null);
    CHECK((*k)["guest"]["available_mb"].n == 4000 && (*k)["guest"]["total_mb"].n == 16000 && (*k)["guest"]["min_guest_mb"].n == 3072);
    CHECK((*k)["logger"]["windows"].s == "running" && (*k)["logger"]["wsl"].s == "unknown");
    CHECK(!(*k)["records"]["readable"].b && (*k)["waiters"].kind == Json::Array && (*k)["waiters"].items.empty());

    // The logger: access denied means its mutex exists (another security context), so it runs; not found is stopped;
    // anything else can't be told (completion review).
    CHECK(std::string(dm::logger_state(true, 0)) == "running" && std::string(dm::logger_state(false, 5)) == "running");
    CHECK(std::string(dm::logger_state(false, 2)) == "not running" && std::string(dm::logger_state(false, 6)) == "unknown");

    // A holder record from before §3, with no acquisition time: its wait and phase can't be told, so they are null.
    dm::SlotRecord old = rec("holder", 30, "2026-11-13T04:00:00Z");
    old.slot = 0;
    dm::StatusInput w3 = w2;
    w3.busy = {true};
    w3.records = {{old, dm::RecordState::Live}};
    auto m = json(dm::status_json(w3));
    CHECK(m && (*m)["slots"].item(0)["holder"]["phase"].kind == Json::Null && (*m)["slots"].item(0)["holder"]["waited_s"].kind == Json::Null);

    // Escaping: quotes, backslashes and a control character in the command, namespace and folder all read back unchanged.
    for (const char* hostile : {"a\"b", "C:\\x\\\"y\\\\", "tab\there\x01", "caf\xc3\xa9 \\\\?\\UNC\\s"}) {
        dm::StatusInput e = w2;
        e.ns = hostile;
        e.records_dir = hostile;
        dm::SlotRecord r = old;
        r.command = hostile;
        e.busy = {true};
        e.records = {{r, dm::RecordState::Live}};
        auto back = json(dm::status_json(e));
        CHECK(back && (*back)["ns"].s == hostile && (*back)["records"]["dir"].s == hostile && (*back)["slots"].item(0)["holder"]["command"].s == hostile);
    }
}

static void jobs() {
    // A finished job's line round-trips, its command holding quotes and non-ASCII text.
    dm::JobLine j;
    j.started_at = "2026-11-13T05:00:00Z";
    j.ended_at = "2026-11-13T05:00:12Z";
    j.pid = 77;
    j.command = "pytest -k \"a and b\" caf\xc3\xa9";
    j.repo = "D:/src/Bosun";
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


// --- Instances (D03 T02 §1) ---
static dm::ProcInfo proc(int64_t pid, int64_t ppid, const char* name, int64_t mb, uint64_t start = 100) {
    dm::ProcInfo p;
    p.pid = pid;
    p.ppid = ppid;
    p.name = name;
    p.private_kb = mb * 1024;
    p.start = start;
    return p;
}

// --- Editor windows (D03 T02 §4) ---
static std::string window_record(const std::string& session, int64_t main, int64_t host, const std::string& roots, const std::string& written = "2026-11-20T11:00:05.123Z") {
    return "{\"schema_version\":1,\"app_name\":\"Visual Studio Code\",\"main_pid\":" + std::to_string(main) + ",\"host_pid\":" + std::to_string(host) +
           ",\"session_id\":\"" + session + "\",\"workspace_file\":null,\"roots\":" + roots + ",\"written_utc\":\"" + written + "\"}";
}

static void editor_windows() {
    // A record: its fields read, and anything else refused with why.
    std::string why;
    auto w = dm::parse_editor_window(window_record("s1", 10, 11, R"(["file:///d%3A/a/app","vscode-remote://wsl%2Bubuntu/home/me/app"])"), why);
    CHECK(w && w->main_pid == 10 && w->host_pid == 11 && w->session_id == "s1" && w->roots.size() == 2 && w->roots[1] == "vscode-remote://wsl%2Bubuntu/home/me/app");
    CHECK(dm::parse_editor_window(window_record("s1", 10, 11, "[]"), why)->roots.empty());
    CHECK(!dm::parse_editor_window(window_record("s1", 10, 11, R"(["a",1])"), why) && why == "its roots are not a list of URIs");
    CHECK(!dm::parse_editor_window(window_record("s1", 10, 11, R"(["a")"), why));
    CHECK(!dm::parse_editor_window(window_record("", 10, 11, "[]"), why) && why == "its session_id is empty");
    CHECK(!dm::parse_editor_window(window_record("s1", 0, 11, "[]"), why) && why == "it has no main_pid");
    CHECK(!dm::parse_editor_window("{\"schema_version\":2}", why) && why == "it has no schema_version 1");
    CHECK(!dm::parse_editor_window("{\"schema_version\":1,", why));  // cut short
    CHECK(dm::parse_string_list(R"(["a\"b","c\\d"])") == std::optional<std::vector<std::string>>(std::vector<std::string>{"a\"b", "c\\d"}));

    // Each root as a place: two same-named checkouts are two paths, a WSL root is its distro's, anything else is said.
    auto a = dm::editor_root("file:///d%3A/one/app", why);
    auto b = dm::editor_root("file:///e%3A/two/app/", why);
    CHECK(a && a->location == "windows" && a->path == "D:\\one\\app" && b && b->path == "E:\\two\\app" && a->path != b->path);
    CHECK(dm::editor_root("file:///c%3A/", why)->path == "C:\\");
    CHECK(dm::editor_root("file://server/share/x%20y", why)->path == "\\\\server\\share\\x y");
    auto r = dm::editor_root("vscode-remote://wsl%2BUbuntu-24.04/home/me/app", why);
    CHECK(r && r->location == "wsl" && r->distro == std::optional<std::string>("Ubuntu-24.04") && r->path == "/home/me/app");
    CHECK(!dm::editor_root("vscode-remote://ssh-remote%2Bhost/x", why) && why.find("not WSL") != std::string::npos);
    CHECK(!dm::editor_root("vscode-remote://wsl%2Ba%2Fb/x", why));  // no distro is named so
    CHECK(!dm::editor_root("vscode-vfs://src/x", why) && !dm::editor_root("file:///d%3A/x%2", why));
    // A distro's folder opened through its Windows share is a WSL root, so it never wakes a stopped distro (completion review).
    auto viaShare = dm::editor_root("file://wsl.localhost/Ubuntu/home/me/app/", why);
    CHECK(viaShare && viaShare->location == "wsl" && viaShare->distro == std::optional<std::string>("Ubuntu") && viaShare->path == "/home/me/app");
    auto viaDollar = dm::editor_root("file://wsl%24/Debian/srv", why);
    CHECK(viaDollar && viaDollar->location == "wsl" && viaDollar->distro == std::optional<std::string>("Debian") && viaDollar->path == "/srv");
    CHECK(!dm::editor_root("file://wsl.localhost/a%20b/x", why));  // no distro is named so
    // An editor's windows are its renderers started with a window's configuration; helper renderers are not (D03 T02 §5).
    CHECK(dm::is_editor_window("\"Code.exe\" --type=renderer --vscode-window-config=vscode:7ba3 --lang=en"));
    CHECK(!dm::is_editor_window("\"Code.exe\" --type=renderer --lang=en"));
    CHECK(!dm::is_editor_window("\"Code.exe\" --type=utility --vscode-window-config=vscode:x"));
    // The command that places an editor's windows names the editor's own folders (round 1 of D03 T02 §5).
    const std::string run = "in the Bosun checkout, run pwsh scripts/install-editor-extension.ps1";
    CHECK(dm::editor_install_command("\"C:\\VS Code\\Code.exe\"") == run);
    CHECK(dm::editor_install_command("\"Code.exe\" --user-data-dir \"D:\\a b\\data\" --extensions-dir=D:\\ext --new-window") ==
          run + " -UserDataDir 'D:\\a b\\data' -ExtensionsDir 'D:\\ext'");
    // Single-quoted, a quote doubled: PowerShell takes a $ as written (round 2).
    CHECK(dm::editor_install_command("Code.exe --user-data-dir D:\\s\\$work's") == run + " -UserDataDir 'D:\\s\\$work''s'");
    CHECK(!dm::editor_switch("\"Code.exe\" --user-data-dirx=1", "--user-data-dir"));
    // The completion review: the editor's own CLI named; a whole-quoted argument with a space kept whole; an editor run
    // with --disable-extensions told how, never to reinstall.
    CHECK(dm::editor_install_command("\"Code.exe\" \"--user-data-dir=D:\\Lab Data\"", "C:\\VS Code\\bin\\code.cmd") ==
          run + " -Cli 'C:\\VS Code\\bin\\code.cmd' -UserDataDir 'D:\\Lab Data'");
    CHECK(dm::editor_install_command("\"Code.exe\" --disable-extensions") == "reopen it without --disable-extensions, which stops the reporter running");
    // VS Code Insiders is an editor too, placed by the same records.
    CHECK(dm::instance_kind("Code - Insiders.exe") == "code");
    // A generated workspace of hundreds of roots reads whole.
    std::string many = "[";
    for (int k = 0; k < 600; ++k) many += (k ? ",\"file:///d%3A/r/" : "\"file:///d%3A/r/") + std::to_string(k) + "\"";
    CHECK(dm::parse_string_list(many + "]") && dm::parse_string_list(many + "]")->size() == 600);

    // Live only while its host is a child of the editor named, started no later than the record was written.
    std::vector<dm::ProcInfo> procs = {proc(10, 1, "Code.exe", 900), proc(11, 10, "Code.exe", 50), proc(12, 99, "Code.exe", 50), proc(13, 10, "Code.exe", 50)};
    procs[1].started_at = "2026-11-20T11:00:01Z";
    procs[2].started_at = "2026-11-20T11:00:01Z";
    procs[3].started_at = "2026-11-20T11:00:09Z";
    auto parent = dm::parents_of(procs);
    std::map<int64_t, const dm::ProcInfo*> by_pid;
    for (const auto& p : procs) by_pid[p.pid] = &p;
    auto live = [&](int64_t main, int64_t host) { return dm::editor_window_live(*dm::parse_editor_window(window_record("s", main, host, "[]"), why), by_pid, parent); };
    CHECK(live(10, 11));
    CHECK(!live(10, 12));  // another editor's host
    CHECK(!live(10, 13));  // started after the record: a reused id
    CHECK(!live(10, 77));  // gone

    // An editor's rows: one per root, its memory once, an empty window one row, an unplaceable root said beside the rest.
    dm::Instance launch;
    launch.side = "windows";
    launch.kind = "code";
    launch.name = "Code.exe";
    launch.pid = 10;
    launch.cwd = std::string("C:\\Users\\me");
    launch.private_mb = 900;
    launch.state = "unknown";
    std::vector<dm::EditorWindow> windows = {
        *dm::parse_editor_window(window_record("s2", 10, 11, R"(["file:///e%3A/two/app","vscode-remote://ssh-remote%2Bh/x"])"), why),
        *dm::parse_editor_window(window_record("s1", 10, 11, R"(["file:///d%3A/one/app/src","vscode-remote://wsl%2Bubuntu/home/me/app"])"), why),
        *dm::parse_editor_window(window_record("s3", 10, 11, "[]"), why),
    };
    std::vector<std::pair<std::string, std::string>> problems;
    auto rows = dm::editor_rows(launch, windows, [](const dm::EditorRoot& root) -> dm::RepoAnswer {
        if (root.path == "D:\\one\\app\\src") return {std::string("D:/one/app"), std::nullopt};
        if (root.path == "E:\\two\\app") return {std::string("E:/two/app"), std::nullopt};
        if (root.location == "wsl") return {std::string("/home/me/app"), std::nullopt};
        return {};
    }, problems);
    auto key = [](const char* session, const char* uri) { return std::optional<std::string>(std::string(session) + "#" + dm::root_key(uri)); };
    CHECK(rows.size() == 5);
    CHECK(rows[0].window == key("s1", "file:///d%3A/one/app/src") && rows[0].repo == std::optional<std::string>("D:/one/app") && rows[0].cwd == std::optional<std::string>("D:\\one\\app\\src"));
    CHECK(rows[1].window == key("s1", "vscode-remote://wsl%2Bubuntu/home/me/app") && rows[1].location == std::optional<std::string>("wsl") && rows[1].distro == std::optional<std::string>("ubuntu") &&
          rows[1].side == "windows" && rows[1].repo == std::optional<std::string>("/home/me/app"));
    CHECK(rows[2].window == key("s2", "file:///e%3A/two/app") && rows[2].repo == std::optional<std::string>("E:/two/app"));
    // A root Bosun can't place is still the editor's row, with no folder, so an editor of such roots alone is listed (round 1).
    CHECK(rows[3].window == key("s2", "vscode-remote://ssh-remote%2Bh/x") && !rows[3].cwd && !rows[3].repo);
    CHECK(rows[4].window == std::optional<std::string>("s3#empty") && !rows[4].cwd && !rows[4].repo);
    // A root's identity is its own, not its place in the list: removing the first root leaves the second's unchanged.
    CHECK(dm::root_key("file:///a") != dm::root_key("file:///b") && dm::root_key("file:///a") == dm::root_key("file:///a") && dm::root_key("x").size() == 16);
    int64_t counted = 0;
    for (const auto& row : rows) {
        CHECK(row.shared_mb == std::optional<int64_t>(900) && row.placed_by == std::optional<std::string>("window") && row.pid == 10);
        counted += row.private_mb.value_or(0);
    }
    CHECK(counted == 900);  // the editor's memory counts once
    CHECK(problems.size() == 1 && problems[0].first == "editor-windows" && problems[0].second.find("ssh-remote") != std::string::npos);
    // VS Code writes the distro lowercased; the row takes WSL's own spelling, so it joins that distro's group.
    auto spelled = dm::editor_rows(launch, windows, [](const dm::EditorRoot&) { return dm::RepoAnswer{}; }, problems,
                                   [](const std::string& d) { return d == "ubuntu" ? std::string("Ubuntu") : d; });
    CHECK(spelled[1].distro == std::optional<std::string>("Ubuntu"));
    // Round 1 of D03 T02 §7: the launch folder's lookup is not a window's; an empty window carries no reason of its.
    dm::Instance failedLaunch = launch;
    failedLaunch.repo_why = std::string("C:/x could not be checked for a repo: denied");
    for (const auto& row : dm::editor_rows(failedLaunch, windows, [](const dm::EditorRoot&) { return dm::RepoAnswer{}; }, problems))
        CHECK(!row.repo_why);
}

static void instances() {
    // What an image is: case and .exe don't count; nothing else is an agent or editor.
    CHECK(dm::instance_kind("Claude.EXE") == "claude");
    CHECK(dm::instance_kind("codex") == "codex");
    CHECK(dm::instance_kind("agy.exe") == "agy");
    CHECK(dm::instance_kind("Code.exe") == "code");
    CHECK(dm::instance_kind("Antigravity.exe") == "antigravity");
    CHECK(dm::instance_kind("node.exe").empty());
    CHECK(dm::instance_kind("claude-helper").empty());
    CHECK(dm::instance_kind(".exe").empty());

    // A VS Code window and its twelve helpers are one editor, with their memory summed.
    std::vector<dm::ProcInfo> procs{proc(1, 0, "explorer.exe", 50), proc(10, 1, "Code.exe", 200)};
    for (int64_t k = 0; k < 12; ++k) procs.push_back(proc(11 + k, 10, "Code.exe", 10));
    // A claude with a claude under it is one; a job it started counts in the job, never in it; node under it is its.
    procs.push_back(proc(30, 1, "claude.exe", 300));
    procs.push_back(proc(31, 30, "claude.exe", 40));
    procs.push_back(proc(32, 30, "node.exe", 60));
    procs.push_back(proc(40, 30, "bosun.exe", 5));   // a job's process, holding a slot
    procs.push_back(proc(41, 40, "cl.exe", 900));
    // Antigravity's sidecars, started by its language server through cmd.exe: one editor with it.
    procs.push_back(proc(80, 1, "Antigravity.exe", 1000));
    procs.push_back(proc(81, 80, "language_server.exe", 300));
    procs.push_back(proc(82, 81, "cmd.exe", 2));
    procs.push_back(proc(83, 82, "Antigravity.exe", 35));
    auto parent = dm::parents_of(procs);
    auto roots = dm::instance_roots(procs, parent);
    CHECK(roots == (std::set<int64_t>{10, 30, 80}));
    roots.insert(40);
    auto mb = dm::owned_memory(procs, parent, roots);
    CHECK(mb[80] == std::optional<int64_t>(1000 + 300 + 2 + 35));
    CHECK(mb[10] == std::optional<int64_t>(200 + 12 * 10));
    CHECK(mb[30] == std::optional<int64_t>(300 + 40 + 60));
    CHECK(mb[40] == std::optional<int64_t>(5 + 900));

    // A reused parent id: the "parent" started after its child, so it is no parent, and a loop of such ids ends.
    std::vector<dm::ProcInfo> loop{proc(50, 51, "claude", 1, 200), proc(51, 50, "claude", 1, 300)};
    auto loop_parent = dm::parents_of(loop);
    CHECK(loop_parent[50] == 0);
    CHECK(loop_parent[51] == 50);
    CHECK(dm::instance_roots(loop, loop_parent) == (std::set<int64_t>{50}));
    // A loop with no starts to tell them apart still ends, each its own root's.
    std::vector<dm::ProcInfo> blind{proc(60, 61, "claude", 1), proc(61, 60, "codex", 1)};
    for (auto& p : blind) p.start.reset();
    auto blind_parent = dm::parents_of(blind);
    CHECK(dm::first_root(60, blind_parent, {}) == 0);
    CHECK(dm::instance_roots(blind, blind_parent) == (std::set<int64_t>{60, 61}));
    // Nothing measured is null, never zero.
    std::vector<dm::ProcInfo> unmeasured{proc(70, 0, "codex", 0)};
    unmeasured[0].private_kb.reset();
    CHECK(!dm::owned_memory(unmeasured, dm::parents_of(unmeasured), {70})[70]);
    // Summed before rounding: twelve helpers of 0.75 MB are 9 MB, not twelve zeros.
    std::vector<dm::ProcInfo> small{proc(90, 0, "Code.exe", 0)};
    for (int64_t k = 0; k < 12; ++k) {
        small.push_back(proc(91 + k, 90, "Code.exe", 0));
        small.back().private_kb = 768;
    }
    CHECK(dm::owned_memory(small, dm::parents_of(small), {90})[90] == std::optional<int64_t>(9));

    // Session records: read for their flat fields, lists and objects skipped; anything else refused.
    auto rec = dm::parse_session_record(R"({"pid":87544,"sessionId":"x","procStart":"134392624419037465","peerFeatures":["a",{"b":"]}"}],"status":"busy"})");
    CHECK(rec && rec->pid == 87544 && rec->proc_start == "134392624419037465" && rec->status == std::optional<std::string>("busy"));
    auto linux_rec = dm::parse_session_record(R"({"pid":11007,"procStart":128409})");
    CHECK(linux_rec && linux_rec->proc_start == "128409" && !linux_rec->status);
    CHECK(!dm::parse_session_record(R"({"pid":1,"procStart":"1","peerFeatures":["unclosed"})"));
    CHECK(!dm::parse_session_record(R"({"pid":1,"procStart":"1","x":[}])"));
    CHECK(!dm::parse_session_record(R"({"procStart":"1"})"));
    CHECK(!dm::parse_session_record(R"({"pid":-3,"procStart":"1"})"));
    CHECK(!dm::parse_session_record("not json"));
    // A flat reader that wasn't asked for nested values still refuses them.
    CHECK(!dm::parse_flat_object(R"({"a":[1]})"));
    std::string deep(300, '[');
    CHECK(!dm::parse_session_record(R"({"pid":1,"procStart":"1","x":)" + deep + std::string(300, ']') + "}"));

    // The state, from the evidence in order: slot, review, session; none is unknown.
    auto state = [](const dm::SlotRecord* holder, const dm::ReviewMark* mark, const dm::SessionRecord* session, std::optional<uint64_t> start) {
        dm::Instance i;
        i.pid = 30;
        dm::instance_state(i, holder, mark, session, start);
        return std::make_pair(i.state, i.evidence.value_or("(none)"));
    };
    dm::SessionRecord busy{30, "100", std::string("busy")}, idle{30, "100", std::string("idle")}, odd{30, "100", std::string("waiting")};
    CHECK(state(nullptr, nullptr, &busy, 100) == std::make_pair(std::string("working"), std::string("Claude Code says busy (session record)")));
    CHECK(state(nullptr, nullptr, &idle, 100).first == "idle");
    CHECK(state(nullptr, nullptr, &odd, 100) == std::make_pair(std::string("unknown"), std::string("Claude Code says waiting (session record)")));
    CHECK(state(nullptr, nullptr, &busy, 101).first == "unknown");  // the record names a process that started at another time
    CHECK(state(nullptr, nullptr, &busy, std::nullopt).first == "unknown");
    dm::ReviewMark live{77, "astra", true}, expired{77, "astra", false};
    CHECK(state(nullptr, &live, nullptr, 100) == std::make_pair(std::string("reviewing"), std::string("runs under astra's review (dispatcher pid 77)")));
    CHECK(state(nullptr, &expired, nullptr, 100) == std::make_pair(std::string("unknown"), std::string("(none)")));
    CHECK(state(nullptr, &live, &busy, 100).first == "reviewing");  // a mark over a busy session: the review wins
    dm::SlotRecord running;
    running.kind = "holder";
    running.slot = 1;
    running.command_started_at = "2026-11-16T09:00:00Z";
    dm::SlotRecord waiting = running;
    waiting.command_started_at.reset();
    CHECK(state(&running, &live, &busy, 100) == std::make_pair(std::string("working"), std::string("holds slot 1, its command running since 2026-11-16T09:00:00Z")));
    CHECK(state(&waiting, nullptr, nullptr, 100) ==
          std::make_pair(std::string("idle"), std::string("holds slot 1; its command has not started (waiting for guest memory)")));
    CHECK(state(nullptr, nullptr, nullptr, 100) == std::make_pair(std::string("unknown"), std::string("(none)")));

    // The shared document: every field, nulls and a problem, and a path with a quote and a backslash escaped.
    dm::InstancesDoc doc;
    doc.side = "windows";
    doc.at = "2026-11-16T09:00:00Z";
    dm::Instance a;
    a.side = "windows";
    a.kind = "claude";
    a.name = "claude.exe";
    a.pid = 30;
    a.cwd = std::string("D:\\a\"b");
    a.repo = std::string("D:/a\"b");
    a.private_mb = 400;
    a.started_at = std::string("2026-11-16T08:00:00Z");
    a.state = "working";
    a.evidence = std::string("Claude Code says busy (session record)");
    dm::Instance job;
    job.side = "wsl";
    job.kind = "job";
    job.name = "make";
    job.pid = 40;
    job.slot = 0;
    job.command = std::string("make -j8");
    job.guest_pid = 4321;
    job.guest_distro = std::string("Ubuntu");
    doc.instances = {a, job};
    doc.unreadable = 3;
    doc.problems = {{"sessions", "C:\\Users\\x\\.claude\\sessions can't be read: denied"}};
    CHECK(dm::instances_json(doc) ==
          R"j({"schema_version":1,"side":"windows","distro":null,"at":"2026-11-16T09:00:00Z","instances":[)j"
          R"j({"side":"windows","kind":"claude","name":"claude.exe","pid":30,"cwd":"D:\\a\"b","repo":"D:/a\"b","private_mb":400,)j"
          R"j("started_at":"2026-11-16T08:00:00Z","state":"working","evidence":"Claude Code says busy (session record)","slot":null,"command":null,"guest_pid":null,"guest_distro":null,)j"
          R"j("window":null,"location":null,"distro":null,"placed_by":null,"shared_mb":null,"repo_why":null},)j"
          R"j({"side":"wsl","kind":"job","name":"make","pid":40,"cwd":null,"repo":null,"private_mb":null,"started_at":null,"state":"unknown","evidence":null,)j"
          R"j("slot":0,"command":"make -j8","guest_pid":4321,"guest_distro":"Ubuntu",)j"
          R"j("window":null,"location":null,"distro":null,"placed_by":null,"shared_mb":null,"repo_why":null}],"unreadable":3,"problems":[{"source":"sessions","why":"C:\\Users\\x\\.claude\\sessions can't be read: denied"}]})j");

    // A holder's record carries its WSL job's own process when the shim names it, and one without it still reads.
    dm::SlotRecord holder;
    holder.kind = "holder";
    holder.pid = 9;
    holder.process_start = 5;
    holder.started_at = "2026-11-16T09:00:00Z";
    holder.command = "make";
    holder.side = "wsl";
    holder.slot = 0;
    holder.ns = "default";
    CHECK(dm::record_json(holder).find("guest_pid") == std::string::npos);
    CHECK(dm::parse_record(dm::record_json(holder)) && !dm::parse_record(dm::record_json(holder))->guest_pid);
    holder.guest_pid = 4321;
    holder.guest_distro = std::string("Ubuntu-24.04");
    auto back = dm::parse_record(dm::record_json(holder));
    CHECK(back && back->guest_pid == std::optional<int64_t>(4321) && back->guest_distro == std::optional<std::string>("Ubuntu-24.04"));
    std::string foreign = dm::record_json(holder);
    foreign.replace(foreign.find("Ubuntu-24.04"), 12, "a/b");
    CHECK(!dm::parse_record(foreign));  // no distro is named so
    CHECK(dm::distro_name_ok("Ubuntu") && !dm::distro_name_ok("") && !dm::distro_name_ok("..\\x") && !dm::distro_name_ok(std::string(65, 'a')));
    std::string bad = dm::record_json(holder);
    bad.replace(bad.find("4321"), 4, "\"x\"");
    CHECK(!dm::parse_record(bad));
}

// The decision's evidence (D03 T01 §6): minutes below the floor, the commit ratio from one sample, days with Windows
// samples, the installed memory and an upgrade within the period, and OOM kills in the period in time order.
static void decision() {
    // Minutes, not samples, below the floor given: two samples in one minute are one minute.
    std::vector<dm::Row> low{
        sys("2026-11-07T10:00:05", "win", "available_mb", 2900), sys("2026-11-07T10:00:35", "win", "available_mb", 2800),
        sys("2026-11-07T10:01:05", "win", "available_mb", 5000), sys("2026-11-07T10:02:05", "win", "available_mb", 2000),
    };
    dm::Report r = dm::aggregate(low, 3000);
    CHECK(r.low_minutes == 2);
    CHECK(r.low_samples == 3);
    CHECK(dm::aggregate(low, 2048).low_minutes == 1);

    // The commit ratio from one sample: a limit that grew mid-period does not hide the earlier peak.
    std::vector<dm::Row> commit{
        sys("2026-11-07T10:00:05", "win", "commit_mb", 18000), sys("2026-11-07T10:00:05", "win", "commit_limit_mb", 20000),  // 0.900
        sys("2026-11-07T11:00:05", "win", "commit_mb", 30000), sys("2026-11-07T11:00:05", "win", "commit_limit_mb", 60000),  // 0.500
    };
    dm::Report c = dm::aggregate(commit);
    CHECK(c.peak_commit_permille == 900);  // two maxima would say 30000 of 60000, 0.5
    CHECK(dm::report_json(c, dm::BuildOverlap{}, dm::ReadStatus{}, "t", "s", 7).find("\"peak_commit_ratio\":0.900") != std::string::npos);

    // Days with Windows samples: seven days of WSL rows only are none.
    std::vector<dm::Row> wsl_only;
    for (int d = 1; d <= 7; ++d) wsl_only.push_back(sys(("2026-11-0" + std::to_string(d) + "T10:00:05").c_str(), "wsl", "mem_available_mb", 4000));
    dm::Report w = dm::aggregate(wsl_only);
    CHECK(w.days_with_samples == 0);
    CHECK(w.dates.size() == 7);  // days_covered still counts them

    // An upgrade within the period: the evidence counts only after it, and says when.
    std::vector<dm::Row> upgrade{
        sys("2026-11-01T10:00:05", "win", "total_mb", 32768), sys("2026-11-01T10:00:05", "win", "available_mb", 1000),
        sys("2026-11-01T10:00:05", "win", "commit_mb", 31000), sys("2026-11-01T10:00:05", "win", "commit_limit_mb", 32000),
        sys("2026-11-05T10:00:05", "win", "total_mb", 65536), sys("2026-11-05T10:00:05", "win", "available_mb", 40000),
        sys("2026-11-06T10:00:05", "win", "total_mb", 65536), sys("2026-11-06T10:00:05", "win", "available_mb", 38000),
    };
    dm::Report u = dm::aggregate(upgrade, 2048);
    CHECK(u.total_mb == 65536);
    CHECK(u.capacity_since == "2026-11-05T10:00:05");
    CHECK(u.low_minutes == 0);  // the 1000 MB minute was the old machine's
    CHECK(u.days_with_samples == 2);
    CHECK(!u.peak_commit_permille);
    CHECK(dm::aggregate(low).capacity_since.empty());  // never logged: no change said
    CHECK(!dm::aggregate(low).total_mb);

    // OOM kills in the period, in time order, from the last sample before it.
    auto oom = [](std::vector<std::pair<std::string, int64_t>> samples) {
        std::vector<dm::Row> rows;
        for (auto& [ts, n] : samples) rows.push_back(sys(ts.c_str(), "wsl", "oom_kills", n));
        return dm::aggregate(rows, 2048, "2026-11-07T00:00:00");
    };
    auto unchanged = oom({{"2026-11-06T23:00:00", 5}, {"2026-11-07T01:00:00", 5}, {"2026-11-07T02:00:00", 5}});
    CHECK(unchanged.wsl_oom_kills_in_period == 0);  // a positive counter that did not move: none in the period
    CHECK(unchanged.wsl_oom_baseline_known);
    CHECK(oom({{"2026-11-06T23:00:00", 5}, {"2026-11-07T01:00:00", 8}}).wsl_oom_kills_in_period == 3);
    CHECK(oom({{"2026-11-06T23:00:00", 5}, {"2026-11-07T01:00:00", 6}, {"2026-11-07T02:00:00", 2}}).wsl_oom_kills_in_period == 3);  // 1, then a restart and 2
    // Read out of order (files listed in any order): the same as in order.
    CHECK(oom({{"2026-11-07T02:00:00", 9}, {"2026-11-06T23:00:00", 5}, {"2026-11-07T01:00:00", 7}}).wsl_oom_kills_in_period == 4);
    // The period's edge between two samples: the one before is the baseline, not a kill in the period.
    CHECK(oom({{"2026-11-06T23:59:59", 4}, {"2026-11-07T00:00:00", 4}}).wsl_oom_kills_in_period == 0);
    auto none_before = oom({{"2026-11-07T01:00:00", 5}, {"2026-11-07T02:00:00", 7}});
    CHECK(none_before.wsl_oom_kills_in_period == 2);  // counted from the first sample in the period
    CHECK(!none_before.wsl_oom_baseline_known);
    CHECK(!oom({{"2026-11-06T23:00:00", 5}}).wsl_oom_kills_in_period);  // no sample in the period: null
    CHECK(!oom({}).wsl_oom_kills_in_period);
    // Kills before an upgrade within the period are the smaller machine's: the count starts at the upgrade, from the
    // last sample before it (completion review).
    std::vector<dm::Row> upgraded{
        sys("2026-11-06T23:00:00", "wsl", "oom_kills", 0),
        sys("2026-11-07T10:00:00", "win", "total_mb", 32768), sys("2026-11-07T11:00:00", "wsl", "oom_kills", 4),
        sys("2026-11-08T10:00:00", "win", "total_mb", 65536), sys("2026-11-08T11:00:00", "wsl", "oom_kills", 4),
        sys("2026-11-09T11:00:00", "wsl", "oom_kills", 4),
    };
    dm::Report up = dm::aggregate(upgraded, 2048, "2026-11-07T00:00:00");
    CHECK(up.capacity_since == "2026-11-08T10:00:00");
    CHECK(up.wsl_oom_kills_in_period == 0);
    CHECK(up.wsl_oom_baseline_known);

    // The floor reaches the JSON and the text.
    CHECK(dm::report_json(r, dm::BuildOverlap{}, dm::ReadStatus{}, "t", "s", 7, 3000).find("\"low_mb\":3000") != std::string::npos);
    CHECK(dm::render(r, 3072).find("under 3.0 GB available: 3") != std::string::npos);
}

static const Group groups[] = {
    {"slots", slots, nullptr},
    {"write_budget", write_budget, nullptr},
    {"quoting", quoting, nullptr},
    {"batch", batch, nullptr},
    {"repos", repos, nullptr},
    {"rows", rows, nullptr},
    {"aggregate", aggregate, nullptr},
    {"decision", decision, nullptr},
    {"records", records, nullptr},
    {"jobs", jobs, nullptr},
    {"options", options, nullptr},
    {"status_json", status_json, nullptr},
    {"report_json", report_json, nullptr},
    {"config", config, nullptr},
    {"instances", instances, nullptr},
    {"editor-windows", editor_windows, nullptr},
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

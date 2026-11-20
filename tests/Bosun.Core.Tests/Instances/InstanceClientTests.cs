using System.Text.Json;
using System.Text.Json.Nodes;
using Bosun.Core.Instances;
using Bosun.Core.Memory;
using Xunit;

namespace Bosun.Core.Tests.Instances;

/// <summary>
/// The instance reader (D03 T02 §1): Windows' document and each distro's own report merged by repo (a WSL repo under
/// <c>/mnt/&lt;drive&gt;/</c> joining the Windows one, two distros' same path kept apart, a WSL job's two halves joined
/// within its own distro only), WSL never asked through wsl.exe, a stale report its distro's last word, a side that
/// failed keeping the others' rows with its reason, and a document that isn't the shape refused with why. bosun.exe is
/// the test child's script mode, which logs every call.
/// </summary>
public sealed class InstanceClientTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bosun-instances-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string _logs;

    public InstanceClientTests()
    {
        _logs = Path.Combine(_work, "logs");
        Directory.CreateDirectory(_logs);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // A file the child held a moment ago: the folder is the system's temp, and goes with it.
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "instances", name);

    private static string Child()
    {
        var mine = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return Path.Combine(mine.Parent!.Parent!.FullName, "Bosun.TestChild", mine.Name, "Bosun.TestChild.exe");
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>25 s after the fixtures' WSL reports were written.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 8, 1, 30, TimeSpan.Zero);

    private string Log => Path.Combine(_work, "calls.jsonl");

    private string Write(string name, string text)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A distro's report, as its logger leaves it in the log folder.</summary>
    private void Report(string distro, string fixture) => File.Copy(Fixture(fixture), Path.Combine(_logs, $"instances-wsl-{distro}.json"), overwrite: true);

    private static object Answer(string? file = null, int exit = 0, string? stderr = null) => new Dictionary<string, object?> { ["answer"] = file, ["exit"] = exit, ["stderr"] = stderr };

    /// <summary>A client whose bosun.exe is the fake, answering <c>instances --json</c> with <paramref name="windows"/>.</summary>
    private InstanceClient Fake(object windows, bool installed = true, DateTimeOffset? now = null) =>
        new(installed ? Child() : null, "bosun.exe is not installed at C:\\x\\bosun.exe.", _logs, clock: new Clock(now ?? Now),
            environment: new Dictionary<string, string?>
            {
                ["BOSUN_FAKE_SCRIPT"] = JsonSerializer.Serialize(new Dictionary<string, object> { ["instances --json"] = windows }),
                ["BOSUN_FAKE_LOG"] = Log,
            });

    private string[][] Calls() => File.Exists(Log) ? File.ReadAllLines(Log).Select(l => JsonSerializer.Deserialize<string[]>(l)!).ToArray() : [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Windows_and_every_distro_s_report_merge_by_repo_with_a_WSL_job_s_halves_joined_in_its_own_distro()
    {
        Report("Ubuntu", "wsl-ubuntu.json");
        Report("Debian", "wsl-debian.json");
        var view = await Fake(Answer(Fixture("windows.json"))).ReadAsync(Token);

        Assert.Null(view.WslNotice);
        Assert.Equal(["Windows", "WSL (Debian)", "WSL (Ubuntu)"], view.Sides.Select(s => s.Name));
        Assert.All(view.Sides, s => Assert.True(s.Reading.HasValue && !s.Reading.IsStale, s.Reading.Why));
        Assert.Equal([["instances", "--json"]], Calls()); // WSL is read from its reports, never asked through wsl.exe

        // D:/Bosun holds Windows' claude, codex and cmake job, Ubuntu's claude in /mnt/d/bosun (letter case aside), and the
        // WSL job joined: memory and folder from Ubuntu's report, slot and state from the Windows holder.
        var bosun = Assert.Single(view.Groups, g => g.Repo == "D:/Bosun");
        Assert.Null(bosun.Distro);
        Assert.Equal([("claude", 4024116L), ("claude", 87544L), ("codex", 6060L), ("job", 4321L), ("job", 9100L)],
            bosun.Rows.Select(r => (r.Instance.Kind, r.Instance.Pid)).OrderBy(x => x.Kind).ThenBy(x => x.Pid.ToString()));
        var joined = bosun.Rows.Single(r => r.Instance.Pid == 4321);
        Assert.Equal(("Ubuntu", 1, "working", 900L, "/mnt/d/bosun", "make -j8"),
            (joined.Distro, joined.Instance.Slot, joined.Instance.State, joined.Instance.PrivateMb, joined.Instance.Cwd, joined.Instance.Command));
        Assert.DoesNotContain(view.Groups.SelectMany(g => g.Rows), r => r.Instance.Pid == 9200); // the holder is shown as its half

        // Round 1: Debian's own job with the same Linux pid, listed first, is never joined to Ubuntu's holder; and
        // round 5: holding no slot, it is a waiter, the Queue page's, so not listed here.
        var apps = view.Groups.Where(g => g.Repo == "/home/dev/projects/app").ToList();
        Assert.Equal(["Debian", "Ubuntu"], apps.Select(g => g.Distro).Order());
        Assert.DoesNotContain(view.Groups.SelectMany(g => g.Rows), r => r.Instance.Command == "cargo build");

        // Outside any checkout: one group, last.
        var loose = view.Groups[^1];
        Assert.Null(loose.Repo);
        Assert.Equal([31500L, 3346257L], loose.Rows.Select(r => r.Instance.Pid).Order());

        // Each side's problems and unreadable count stay with its reading.
        var ubuntu = view.Sides.Single(s => s.Distro == "Ubuntu").Reading.Value!;
        Assert.Equal(("sessions", 1L), (Assert.Single(ubuntu.Problems).Source, ubuntu.Unreadable));
    }

    [Fact]
    public async Task A_holder_that_names_no_distro_is_never_joined()
    {
        Report("Ubuntu", "wsl-ubuntu.json");
        var doc = JsonNode.Parse(File.ReadAllText(Fixture("windows.json")))!.AsObject();
        doc["instances"]![3]!["guest_distro"] = null; // a record an older shim wrote
        var view = await Fake(Answer(Write("old.json", doc.ToJsonString()))).ReadAsync(Token);
        var rows = view.Groups.SelectMany(g => g.Rows).ToList();
        Assert.Contains(rows, r => r.Instance.Pid == 9200 && r.Instance.Slot == 1);  // the holder, as Windows saw it
        Assert.DoesNotContain(rows, r => r.Instance.Pid == 4321);                     // its half, unjoined, isn't listed
    }

    [Fact]
    public async Task A_WSL_holder_s_command_start_is_its_record_s_and_null_until_it_starts()
    {
        // Round 5: a holder waiting for guest memory had the shim's start as its uptime. The half here carries one, as an
        // older scanner wrote it, so the merge can't take it.
        var guest = JsonNode.Parse(File.ReadAllText(Fixture("wsl-ubuntu.json")))!.AsObject();
        guest["instances"]![2]!["started_at"] = "2026-11-16T07:59:59Z";
        File.WriteAllText(Path.Combine(_logs, "instances-wsl-Ubuntu.json"), guest.ToJsonString());
        var doc = JsonNode.Parse(File.ReadAllText(Fixture("windows.json")))!.AsObject();
        var holder = doc["instances"]![3]!.AsObject();
        holder["started_at"] = null;
        holder["state"] = "idle";
        holder["evidence"] = "holds slot 1; its command has not started (waiting for guest memory)";
        var view = await Fake(Answer(Write("waiting.json", doc.ToJsonString()))).ReadAsync(Token);
        var joined = view.Groups.SelectMany(g => g.Rows).Single(r => r.Instance.Pid == 4321).Instance;
        Assert.Equal(("idle", (string?)null, 1), (joined.State, joined.StartedAt, joined.Slot));
    }

    [Fact]
    public async Task No_report_keeps_the_Windows_rows_with_a_notice()
    {
        var view = await Fake(Answer(Fixture("windows.json"))).ReadAsync(Token);
        Assert.StartsWith($"No WSL distro has reported its instances to {_logs}, so only Windows is listed.", view.WslNotice);
        Assert.Equal(["Windows"], view.Sides.Select(s => s.Name));
        Assert.Contains(view.Groups, g => g.Repo == "D:/Bosun");
    }

    [Fact]
    public void This_user_s_reports_are_read_from_the_folder_LOCALAPPDATA_names_as_the_loggers_write_them()
    {
        var client = InstanceClient.ForThisUser(name => name == "LOCALAPPDATA" ? @"X:\elsewhere" : null);
        Assert.Equal(Path.Combine(@"X:\elsewhere", "Bosun", "logs"), client.Logs);
    }

    [Fact]
    public async Task A_stale_report_is_its_distro_s_last_word_and_its_rows_are_not_listed_as_running()
    {
        Report("Ubuntu", "wsl-ubuntu.json");
        Report("Debian", "wsl-debian.json");
        var view = await Fake(Answer(Fixture("windows.json")), now: Now.AddMinutes(12)).ReadAsync(Token);
        var ubuntu = view.Sides.Single(s => s.Distro == "Ubuntu").Reading;
        Assert.True(ubuntu.IsStale);
        Assert.Equal("WSL (Ubuntu) last reported 12 minutes ago: it is stopped, or its logger is.", ubuntu.Why);
        Assert.DoesNotContain(view.Groups.SelectMany(g => g.Rows), r => r.Distro is not null);
        Assert.Contains(view.Groups.SelectMany(g => g.Rows), r => r.Instance.Pid == 9200); // the holder still shown, as Windows saw it

        // Kept for a page that says they are old (D03 T02 §2): the stale distros' rows, the holder joined to its half.
        var kept = view.Merged(keepStale: true).SelectMany(g => g.Rows).ToList();
        Assert.Contains(kept, r => r.Distro == "Ubuntu");
        Assert.Contains(kept, r => r.Distro == "Debian");
        Assert.DoesNotContain(kept, r => r.Instance.Pid == 9200 && r.Distro is null);             // joined, not listed apart
        Assert.Same(view.Groups, view.Merged(keepStale: false));
    }

    [Fact]
    public async Task A_side_that_fails_keeps_the_others_rows_and_says_why()
    {
        Report("Debian", "wsl-debian.json");
        File.WriteAllText(Path.Combine(_logs, "instances-wsl-Ubuntu.json"), "{not json");
        var view = await Fake(Answer(Fixture("windows.json"))).ReadAsync(Token);
        var ubuntu = view.Sides.Single(s => s.Distro == "Ubuntu").Reading;
        Assert.Equal(ReadingProblem.Invalid, ubuntu.Problem);
        Assert.Contains("instances-wsl-Ubuntu.json is not an instances report", ubuntu.Why);
        Assert.Contains(view.Groups, g => g.Repo == "D:/Bosun");
        Assert.Contains(view.Groups, g => g.Distro == "Debian");

        // And the reverse: Windows failed, the distro's rows kept.
        var reverse = await Fake(Answer(exit: 2, stderr: "[bosun] the process list can't be taken")).ReadAsync(Token);
        Assert.Equal(ReadingProblem.Failed, reverse.Sides[0].Reading.Problem);
        Assert.Contains("the process list can't be taken", reverse.Sides[0].Reading.Why);
        Assert.All(reverse.Groups, g => Assert.All(g.Rows, r => Assert.Equal("Debian", r.Distro)));
        Assert.NotEmpty(reverse.Groups);

        // No bosun.exe: said, and WSL still read.
        var missing = await Fake(Answer(), installed: false).ReadAsync(Token);
        Assert.Equal((ReadingProblem.NotInstalled, "bosun.exe is not installed at C:\\x\\bosun.exe."), (missing.Sides[0].Reading.Problem, missing.Sides[0].Reading.Why));
        Assert.Contains(missing.Groups, g => g.Distro == "Debian");
    }

    public static TheoryData<string, string> NotTheShape => new()
    {
        { "kind", "is of kind 'vim', which no scanner writes" },
        { "state", "state is 'busy', which no scanner writes" },
        { "missing", "missing required properties" },
        { "side", "wrote the wsl document, not the windows one" },
        { "source", "names the source 'disk', which no scanner reads" },
        { "foreign", "is a wsl claude in the windows document" },
        { "distro", "names the distro 'a/b'" },
        // Round 1: a report's own distro name is bounded too, never 128 KiB copied into every notice.
        { "long-distro", "it names the distro '" + new string('u', 64) + "...'" },
        { "text", "is not its document" },
        // D03 T02 §4: a WSL window names its distro and a Linux folder, and only an editor is placed by a window.
        { "window-distro", "is in WSL and names no distro it could be in" },
        { "window-folder", @"is in WSL with the folder 'D:\x'" },
        { "window-kind", "which no window places" },
        { "window-location", "location is 'mac'" },
        // D03 T02 §6: a drive mount is a Linux folder and a drive's root.
        { "drives", @"it lists a drive mount that is not a Linux folder and a Windows folder ('windir' for 'D:\')" },
    };

    /// <summary>Makes a row an editor's window row, as the Windows scanner writes one (D03 T02 §4).</summary>
    private static void Window(JsonObject row, string location, string? distro, string? cwd, string window = "s1#0")
    {
        row["kind"] = "code";
        row["placed_by"] = "window";
        row["window"] = window;
        row["location"] = location;
        row["distro"] = distro;
        row["cwd"] = cwd;
        row["shared_mb"] = 4096;
    }

    [Fact]
    public async Task An_editor_s_windows_go_in_each_repo_they_hold_a_WSL_window_in_its_own_distro_s()
    {
        // D03 T02 §4: one Windows editor with a window on D:/Bosun and WSL windows on the same guest path in two distros.
        Report("Ubuntu", "wsl-ubuntu.json");
        Report("Debian", "wsl-debian.json");
        var doc = JsonNode.Parse(File.ReadAllText(Fixture("windows.json")))!.AsObject();
        var rows = doc["instances"]!.AsArray();
        JsonObject Editor(string window, string location, string? distro, string cwd, string repo)
        {
            var row = rows[1]!.DeepClone().AsObject();
            Window(row, location, distro, cwd, window);
            row["pid"] = 777;
            row["repo"] = repo;
            return row;
        }
        rows.Add(Editor("s1#0", "windows", null, @"D:\Bosun\src", "D:/Bosun"));
        rows.Add(Editor("s2#0", "wsl", "Ubuntu", "/home/dev/projects/app", "/home/dev/projects/app"));
        rows.Add(Editor("s3#0", "wsl", "Debian", "/home/dev/projects/app", "/home/dev/projects/app"));
        var view = await Fake(Answer(Write("editor.json", doc.ToJsonString()))).ReadAsync(Token);
        Assert.True(view.Sides[0].Reading.HasValue, view.Sides[0].Reading.Why);
        var editor = view.Groups.SelectMany(g => g.Rows.Select(r => (g.Repo, g.Distro, Window: r.Instance.Window, r.Instance.Pid))).Where(x => x.Pid == 777)
            .OrderBy(x => x.Window, StringComparer.Ordinal).Select(x => (x.Repo, x.Distro, x.Window)).ToList();
        Assert.Equal([("D:/Bosun", null, "s1#0"), ("/home/dev/projects/app", "Ubuntu", "s2#0"), ("/home/dev/projects/app", "Debian", "s3#0")], editor);
        // Each WSL window shares its distro's group with that distro's own instances, never the other's.
        var ubuntuApp = view.Groups.Single(g => g is { Repo: "/home/dev/projects/app", Distro: "Ubuntu" });
        Assert.Contains(ubuntuApp.Rows, r => r.Instance.Window == "s2#0");
        Assert.DoesNotContain(ubuntuApp.Rows, r => r.Instance.Window == "s3#0");
    }

    /// <summary>A report as a distro's logger writes it, from the fixture with its drives and extra rows set.</summary>
    private static InstancesDoc Wsl(string fixture, JsonArray? drives, params JsonObject[] extra)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Fixture(fixture)))!.AsObject();
        if (drives is null) doc.Remove("drives");
        else doc["drives"] = drives;
        foreach (var row in extra) doc["instances"]!.AsArray().Add(row);
        return JsonSerializer.Deserialize(doc.ToJsonString(), InstanceJson.Default.InstancesDoc)!;
    }

    private static JsonArray Drives(params (string Mount, string Windows)[] drives) =>
        [.. drives.Select(d => (JsonNode)new JsonObject { ["mount"] = d.Mount, ["windows"] = d.Windows })];

    private static JsonObject Row(string kind, long pid, string side, string? cwd, string? repo) => new()
    {
        ["side"] = side, ["kind"] = kind, ["name"] = kind, ["pid"] = pid, ["cwd"] = cwd, ["repo"] = repo, ["private_mb"] = 10,
        ["started_at"] = null, ["state"] = "unknown", ["evidence"] = null, ["slot"] = null, ["command"] = null, ["guest_pid"] = null, ["guest_distro"] = null,
    };

    [Fact]
    public void Each_row_takes_its_own_distro_s_drive_mounts_and_an_unknown_one_keeps_the_guest_path()
    {
        // D03 T02 §6. Ubuntu has D: at /mnt/d; Debian moved its drives, D: at /windir/d. A Windows editor has a window on
        // /mnt/d/Bosun in each distro, and Windows saw an unjoined holder in Debian with that repo.
        var ubuntu = Wsl("wsl-ubuntu.json", Drives(("/mnt/c", "C:\\"), ("/mnt/d", "D:\\")));
        var debian = Wsl("wsl-debian.json", Drives(("/windir/d", "D:\\")), Row("claude", 7001, "wsl", "/windir/d/Bosun", "/windir/d/Bosun"), Row("claude", 7002, "wsl", "/mnt/d/Bosun", "/mnt/d/Bosun"));
        var windowsDoc = JsonNode.Parse(File.ReadAllText(Fixture("windows.json")))!.AsObject();
        JsonObject Editor(string window, string distro)
        {
            var row = Row("code", 777, "windows", "/mnt/d/Bosun", "/mnt/d/Bosun");
            row["placed_by"] = "window";
            row["window"] = window;
            row["location"] = "wsl";
            row["distro"] = distro;
            row["shared_mb"] = 10;
            return row;
        }
        var holder = Row("job", 9300, "wsl", null, "/mnt/d/Bosun");
        holder["slot"] = 2;
        holder["guest_pid"] = 99999;
        holder["guest_distro"] = "Debian";
        foreach (var row in new[] { Editor("u#1", "Ubuntu"), Editor("d#1", "Debian"), holder }) windowsDoc["instances"]!.AsArray().Add(row);
        var windows = JsonSerializer.Deserialize(windowsDoc.ToJsonString(), InstanceJson.Default.InstancesDoc)!;

        SideReading Win() => new("windows", null, Reading<InstancesDoc>.Of(windows, Now));
        SideReading Fresh(string distro, InstancesDoc doc) => new("wsl", distro, Reading<InstancesDoc>.Of(doc, Now));
        (string? Repo, string? Distro) Group(IReadOnlyList<RepoGroup> groups, Func<InstanceRow, bool> which)
        {
            var group = groups.Single(g => g.Rows.Any(which));
            return (group.Repo, group.Distro);
        }

        var groups = InstanceClient.Merge([Win(), Fresh("Ubuntu", ubuntu), Fresh("Debian", debian)]);
        Assert.Equal(("D:/Bosun", null), Group(groups, r => r.Instance.Window == "u#1"));
        Assert.Equal(("/mnt/d/Bosun", "Debian"), Group(groups, r => r.Instance.Window == "d#1"));     // a guest folder in Debian
        Assert.Equal(("/mnt/d/Bosun", "Debian"), Group(groups, r => r.Instance.Pid == 9300));          // the unjoined holder: its distro's
        Assert.Equal(("D:/Bosun", null), Group(groups, r => r.Instance.Pid == 7001));                 // Debian's own D: folder
        Assert.Equal(("/mnt/d/Bosun", "Debian"), Group(groups, r => r.Instance.Pid == 7002));

        // Debian's report missing: its editor window and holder have no mounts, so they stay Debian's guest folders.
        groups = InstanceClient.Merge([Win(), Fresh("Ubuntu", ubuntu)]);
        Assert.Equal(("/mnt/d/Bosun", "Debian"), Group(groups, r => r.Instance.Window == "d#1"));
        Assert.Null(groups.SelectMany(g => g.Rows).Single(r => r.Instance.Pid == 9300).Mounts);

        // Debian's report stale: it speaks for its own rows (kept for the page), never for the live editor's window.
        var stale = new SideReading("wsl", "Debian", Reading<InstancesDoc>.Stale(debian, Now, TimeSpan.FromMinutes(12), "WSL (Debian) last reported 12 minutes ago."));
        groups = InstanceClient.Merge([Win(), Fresh("Ubuntu", ubuntu), stale], keepStale: true);
        Assert.Equal(("/mnt/d/Bosun", "Debian"), Group(groups, r => r.Instance.Window == "d#1"));
        Assert.Null(groups.SelectMany(g => g.Rows).Single(r => r.Instance.Window == "d#1").Mounts);
        Assert.Equal(("D:/Bosun", null), Group(groups, r => r.Instance.Pid == 7001));

        // Debian's report older than drive mounts: not known, so even /mnt/d stays Debian's own.
        var legacy = Wsl("wsl-debian.json", null, Row("claude", 7002, "wsl", "/mnt/d/Bosun", "/mnt/d/Bosun"));
        groups = InstanceClient.Merge([Win(), Fresh("Ubuntu", ubuntu), Fresh("Debian", legacy)]);
        Assert.Equal(("/mnt/d/Bosun", "Debian"), Group(groups, r => r.Instance.Pid == 7002));
        Assert.Equal(("/mnt/d/Bosun", "Debian"), Group(groups, r => r.Instance.Window == "d#1"));
    }

    [Fact]
    public void Rows_whose_repo_could_not_be_told_are_a_group_of_their_own_apart_from_rows_outside_any_checkout()
    {
        // D03 T02 §7.
        var doc = JsonNode.Parse(File.ReadAllText(Fixture("windows.json")))!.AsObject();
        JsonObject Agent(long pid, string cwd, string? why)
        {
            var row = Row("claude", pid, "windows", cwd, null);
            row["repo_why"] = why;
            return row;
        }
        foreach (var row in new[] { Agent(801, @"D:\a\shut\work", @"D:/a/shut could not be checked for a repo: Access is denied."),
                     Agent(802, @"E:\b\shut", "E:/b/shut did not answer within the scan's time"), Agent(803, @"C:\loose", null) })
            doc["instances"]!.AsArray().Add(row);
        var windows = JsonSerializer.Deserialize(doc.ToJsonString(), InstanceJson.Default.InstancesDoc)!;
        var groups = InstanceClient.Merge([new SideReading("windows", null, Reading<InstancesDoc>.Of(windows, Now))]);
        var unknown = Assert.Single(groups, g => g.Unknown);
        Assert.Equal([801L, 802L], unknown.Rows.Select(r => r.Instance.Pid).Order());
        Assert.Null(unknown.Repo);
        var none = Assert.Single(groups, g => g is { Repo: null, Unknown: false });
        Assert.Contains(none.Rows, r => r.Instance.Pid == 803);
        Assert.DoesNotContain(none.Rows, r => r.Instance.RepoWhy is not null);
        // A reason beside a repo is not a row any scanner writes.
        var both = doc.DeepClone().AsObject();
        var bad = Agent(804, @"D:\x", "x");
        bad["repo"] = "D:/x";
        both["instances"]!.AsArray().Add(bad);
        Assert.NotNull(JsonSerializer.Deserialize(both.ToJsonString(), InstanceJson.Default.InstancesDoc)!.Fault());
    }

    [Theory]
    [MemberData(nameof(NotTheShape))]
    public async Task A_document_that_isn_t_the_shape_is_refused_with_why(string wrong, string said)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Fixture("windows.json")))!.AsObject();
        var first = doc["instances"]![1]!.AsObject();
        switch (wrong)
        {
            case "kind": first["kind"] = "vim"; break;
            case "state": first["state"] = "busy"; break;
            case "missing": first.Remove("cwd"); break;
            case "side": doc["side"] = "wsl"; break;
            case "source": doc["problems"] = new JsonArray(new JsonObject { ["source"] = "disk", ["why"] = "x" }); break;
            case "foreign": first["side"] = "wsl"; break;
            case "distro": doc["instances"]![3]!["guest_distro"] = "a/b"; break;
            case "long-distro": doc["distro"] = new string('u', 128 * 1024); break;
            case "window-distro": Window(first, "wsl", null, "/home/me/app"); break;
            case "window-folder": Window(first, "wsl", "Ubuntu", @"D:\x"); break;
            case "window-kind": Window(first, "windows", null, @"D:\x"); first["kind"] = "claude"; break;
            case "window-location": Window(first, "mac", null, @"D:\x"); break;
            case "drives": doc["drives"] = new JsonArray(new JsonObject { ["mount"] = "windir", ["windows"] = "D:\\" }); break;
        }
        var text = wrong == "text" ? "{not json" : doc.ToJsonString();
        var view = await Fake(Answer(Write("doc.json", text))).ReadAsync(Token);
        var reading = view.Sides[0].Reading;
        Assert.Equal(ReadingProblem.Invalid, reading.Problem);
        Assert.Contains(said, reading.Why);
        Assert.Empty(view.Groups);
    }

    [Theory]
    [InlineData("{\"schema_version\":\"1\"}")]
    [InlineData("{\"schema_version\":true}")]
    [InlineData("{\"schema_version\":null}")]
    [InlineData("{\"schema_version\":1.5}")]
    [InlineData("[1]")]
    public async Task A_report_whose_version_isn_t_a_whole_number_is_refused_and_the_rest_still_read(string text)
    {
        // Round 4: a string version threw out of the whole read.
        File.WriteAllText(Path.Combine(_logs, "instances-wsl-Ubuntu.json"), text);
        Report("Debian", "wsl-debian.json");
        var view = await Fake(Answer(Fixture("windows.json"))).ReadAsync(Token);
        var ubuntu = view.Sides.Single(s => s.Distro == "Ubuntu").Reading;
        Assert.Equal((ReadingProblem.Invalid, true), (ubuntu.Problem, ubuntu.Why!.EndsWith("has no schema_version.", StringComparison.Ordinal)));
        Assert.Contains(view.Groups, g => g.Repo == "D:/Bosun");
        Assert.Contains(view.Groups, g => g.Distro == "Debian");
    }

    [Fact]
    public async Task Another_schema_version_is_unsupported_on_either_side()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Fixture("windows.json")))!.AsObject();
        doc["schema_version"] = 2;
        var guest = JsonNode.Parse(File.ReadAllText(Fixture("wsl-debian.json")))!.AsObject();
        guest["schema_version"] = 2;
        File.WriteAllText(Path.Combine(_logs, "instances-wsl-Debian.json"), guest.ToJsonString());
        var view = await Fake(Answer(Write("v2.json", doc.ToJsonString()))).ReadAsync(Token);
        Assert.Equal(ReadingProblem.Unsupported, view.Sides[0].Reading.Problem);
        Assert.Equal(ReadingProblem.Unsupported, view.Sides[1].Reading.Problem);
    }
}

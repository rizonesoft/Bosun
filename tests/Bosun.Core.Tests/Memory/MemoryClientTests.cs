using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bosun.Core;
using Bosun.Core.Memory;
using Xunit;

namespace Bosun.Core.Tests.Memory;

/// <summary>
/// The memory readers (D03 T01 §1): each document reads typed, as its writer writes it, missing measurements kept
/// missing; every failure a page can meet is a reading with its reason, reached from the real failure; a snapshot is
/// read as a file replaced whole must be read, and is stale past 90 seconds, per side. The commands run against a fake
/// bosun (tests/Bosun.TestChild's answer mode) that checks the exact arguments it was given.
/// </summary>
public sealed class MemoryClientTests : IDisposable
{
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "bosun-memory-" + Guid.NewGuid().ToString("N")[..10]);

    public MemoryClientTests() => Directory.CreateDirectory(_logs);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logs, recursive: true);
        }
        catch (IOException)
        {
            // A file a test held open a moment ago: the folder is the system's temp, and goes with it.
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "memory", name);

    private static string Child()
    {
        var mine = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return Path.Combine(mine.Parent!.Parent!.FullName, "Bosun.TestChild", mine.Name, "Bosun.TestChild.exe");
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static T Read<T>(string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class =>
        JsonSerializer.Deserialize(File.ReadAllText(Fixture(file)), type)!;

    /// <summary>A client whose bosun is the fake, answering <paramref name="answer"/> when given exactly <paramref name="expect"/>.</summary>
    private MemoryClient Fake(string[] expect, string? answer = null, Dictionary<string, string?>? more = null, TimeSpan? timeout = null)
    {
        var environment = new Dictionary<string, string?> { ["BOSUN_FAKE_EXPECT"] = JsonSerializer.Serialize(expect), ["BOSUN_FAKE_ANSWER"] = answer };
        foreach (var (key, value) in more ?? []) environment[key] = value;
        return new MemoryClient(Child(), "", _logs, environment: environment)
        {
            StatusTimeout = timeout ?? TimeSpan.FromSeconds(10),
            ReportTimeout = timeout ?? TimeSpan.FromSeconds(60),
        };
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_logs, name);
        File.WriteAllText(path, text);
        return path;
    }

    // ---- the documents ----

    [Fact]
    public void Each_document_reads_typed_with_what_is_missing_kept_missing_and_every_value_beside_it_kept()
    {
        var status = Read("status.json", MemoryJson.Default.StatusDoc);
        Assert.Equal((2, 2488L, 32601L, 6144L), (status.Slots.Count, status.Memory.AvailableMb, status.Memory.TotalMb, status.Memory.MinAvailMb));
        Assert.Null(status.Guest);
        Assert.Null(status.Logger.Wsl);

        var busy = Read("status-busy.json", MemoryJson.Default.StatusDoc);
        Assert.Equal(("running", 214L, 5L), (busy.Slots[0].Holder!.Phase, busy.Slots[0].Holder!.RunningS, busy.Slots[0].Holder!.WaitedS));
        Assert.Null(busy.Slots[1].Busy); // a slot whose lock couldn't be told
        Assert.Equal(("waiter", (string?)null, "memory", 6144L), (busy.Waiters[0].Kind, busy.Waiters[0].Repo, busy.Waiters[0].WaitingFor, busy.Waiters[0].MinAvailMb));
        Assert.Null(busy.Memory.AvailableMb); // unmeasured, and the total beside it kept
        Assert.Equal(32601L, busy.Memory.TotalMb);
        Assert.Equal(4096L, busy.Guest!.MinGuestMb);
        Assert.False(busy.Records.Readable);

        var report = Read("report-7d.json", MemoryJson.Default.ReportDoc);
        Assert.True(report.Read.Complete);
        Assert.Equal(7, report.DaysRequested);
        Assert.Contains(report.Series, p => p.WinAvailableMb is null && p.WslAvailableMb is not null); // a minute only WSL wrote
        Assert.NotEmpty(report.Windows.WorstRepos);

        var incomplete = Read("report-incomplete.json", MemoryJson.Default.ReportDoc);
        Assert.Equal((false, 1L, 7L), (incomplete.Read.Complete, incomplete.Read.UnreadableFiles, incomplete.Read.RejectedRows)); // holes, said
        Assert.Null(incomplete.Windows.MinAvailableMb);
        Assert.Null(incomplete.FirstSample);
        Assert.Null(incomplete.Builds.At);

        var windows = Read("snapshot-windows.json", MemoryJson.Default.SnapshotDoc);
        Assert.NotNull(windows.Counter("commit_mb"));
        Assert.NotNull(windows.WslVm);
        var wsl = Read("snapshot-wsl.json", MemoryJson.Default.SnapshotDoc);
        Assert.Equal((512L, 0L, 0L), (wsl.Counter("swap_used_mb"), wsl.Counter("psi_full_avg10_x100"), wsl.Counter("oom_kills")));
        Assert.Null(wsl.Counter("commit_mb")); // not this side's
        Assert.Null(wsl.WslVm);
        var partial = Read("snapshot-partial.json", MemoryJson.Default.SnapshotDoc);
        Assert.Null(partial.Counter("commit_mb"));
        Assert.Equal(2488L, partial.Counter("available_mb"));
        Assert.False(partial.HistoryWritten);
    }

    [Fact]
    public void A_document_missing_a_field_or_holding_null_where_a_value_is_required_is_refused()
    {
        var status = JsonNode.Parse(File.ReadAllText(Fixture("status.json")))!.AsObject();
        status.Remove("ns");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(status.ToJsonString(), MemoryJson.Default.StatusDoc));
        var snapshot = JsonNode.Parse(File.ReadAllText(Fixture("snapshot-wsl.json")))!.AsObject();
        snapshot["side"] = null;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(snapshot.ToJsonString(), MemoryJson.Default.SnapshotDoc));
    }

    // ---- the reasons ----

    [Fact]
    public void A_reading_with_no_value_carries_its_problem_and_its_words()
    {
        foreach (var problem in Enum.GetValues<ReadingProblem>())
        {
            var reading = Reading<StatusDoc>.Fail(problem, "  why it is so  ");
            Assert.Equal((problem, "why it is so", false, (StatusDoc?)null), (reading.Problem!.Value, reading.Why, reading.HasValue, reading.Value));
        }
        Assert.Throws<ArgumentException>(() => Reading<StatusDoc>.Fail(ReadingProblem.Failed, " "));
    }

    [Fact]
    public void Bosun_is_found_by_its_variable_first_then_where_it_is_installed_and_a_missing_one_says_where_it_was_looked_for()
    {
        Dictionary<string, string?> env = new() { ["LOCALAPPDATA"] = @"C:\Users\person\AppData\Local" };
        var installed = @"C:\Users\person\AppData\Local\Bosun\bin\bosun.exe";
        Assert.Equal(installed, BosunTool.Locate(out _, k => env.GetValueOrDefault(k), p => p == installed));
        env[BosunTool.Variable] = @"D:\Bosun\build\Release\bosun.exe";
        Assert.Equal(@"D:\Bosun\build\Release\bosun.exe", BosunTool.Locate(out _, k => env.GetValueOrDefault(k), _ => true));
        Assert.Null(BosunTool.Locate(out var why, k => env.GetValueOrDefault(k), _ => false));
        Assert.Contains(@"D:\Bosun\build\Release\bosun.exe", why);
        env.Remove(BosunTool.Variable);
        Assert.Null(BosunTool.Locate(out why, k => env.GetValueOrDefault(k), _ => false));
        Assert.Contains(installed, why);
    }

    // ---- the commands ----

    [Fact]
    public async Task Status_and_report_read_through_the_tool_each_asking_with_its_own_arguments()
    {
        var status = await Fake(["status", "--json"], Fixture("status.json")).StatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.HasValue, status.Why);
        Assert.Equal("Bosun", status.Value!.Ns);
        foreach (var days in new[] { 1, 7, 14 })
        {
            var report = await Fake(["report", "--json", "--days", days.ToString()], Fixture("report-7d.json")).ReportAsync(days, TestContext.Current.CancellationToken);
            Assert.True(report.HasValue, $"{days} days: {report.Why}");
        }
        // Asked with the wrong arguments, the fake says what it got, and the reading says so.
        var wrong = await Fake(["report", "--json", "--days", "7"], Fixture("report-7d.json")).ReportAsync(14, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingProblem.Failed, wrong.Problem);
        Assert.Contains("\"14\"", wrong.Why);
    }

    [Fact]
    public async Task A_14_day_report_of_a_point_a_minute_reads_whole()
    {
        var report = JsonNode.Parse(File.ReadAllText(Fixture("report-7d.json")))!.AsObject();
        var series = new JsonArray();
        var start = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        for (var minute = 0; minute < 14 * 24 * 60; minute++)
            series.Add(new JsonObject
            {
                ["minute"] = start.AddMinutes(minute).ToString("yyyy-MM-dd'T'HH:mm"), ["win_available_mb"] = 4000 + minute % 900,
                ["wsl_available_mb"] = minute % 7 == 0 ? null : 6000, ["repos"] = 12, ["repo_private_mb"] = 10000 + minute % 300,
            });
        report["series"] = series;
        report["days_requested"] = 14;
        var file = Write("report-14d.json", report.ToJsonString());
        Assert.True(new FileInfo(file).Length > 1024 * 1024, "the 14-day report is more than the runner's default 1 MB");
        var read = await Fake(["report", "--json", "--days", "14"], file).ReportAsync(14, TestContext.Current.CancellationToken);
        Assert.True(read.HasValue, read.Why);
        Assert.Equal(20160, read.Value!.Series.Count);
    }

    [Fact]
    public async Task Each_way_the_tool_can_fail_is_a_reading_with_why_and_a_cancelled_call_is_thrown_to_its_caller()
    {
        var token = TestContext.Current.CancellationToken;
        var two = Write("status-2.json", File.ReadAllText(Fixture("status.json")).Replace("\"schema_version\":1", "\"schema_version\":2"));
        var unsupported = await Fake(["status", "--json"], two).StatusAsync(token);
        Assert.Equal(ReadingProblem.Unsupported, unsupported.Problem);
        Assert.Contains("schema version 2", unsupported.Why);

        var failed = await Fake(["status", "--json"], null, new() { ["BOSUN_FAKE_EXIT"] = "1", ["BOSUN_FAKE_STDERR"] = "[bosun] bad options" }).StatusAsync(token);
        Assert.Equal(ReadingProblem.Failed, failed.Problem);
        Assert.Contains("exit code 1: [bosun] bad options", failed.Why);

        var garbled = await Fake(["status", "--json"], Write("garbled.json", "not json")).StatusAsync(token);
        Assert.Equal(ReadingProblem.Invalid, garbled.Problem);

        var cantStart = await new MemoryClient(Path.Combine(_logs, "no-such-bosun.exe"), "", _logs).StatusAsync(token);
        Assert.Equal(ReadingProblem.Failed, cantStart.Problem);
        Assert.Contains("could not be started", cantStart.Why);

        var slow = await Fake(["status", "--json"], Fixture("status.json"), new() { ["BOSUN_FAKE_SLEEP_MS"] = "10000" }, TimeSpan.FromMilliseconds(500)).StatusAsync(token);
        Assert.Equal(ReadingProblem.Failed, slow.Problem);
        Assert.Contains("did not answer within", slow.Why);

        var notInstalled = await new MemoryClient(null, "bosun.exe is not installed at X.", _logs).StatusAsync(token);
        Assert.Equal((ReadingProblem.NotInstalled, "bosun.exe is not installed at X."), (notInstalled.Problem!.Value, notInstalled.Why));

        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(token);
        leaving.CancelAfter(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Fake(["status", "--json"], Fixture("status.json"), new() { ["BOSUN_FAKE_SLEEP_MS"] = "10000" }).StatusAsync(leaving.Token));
    }

    // ---- the snapshots ----

    private MemoryClient Snapshots(DateTimeOffset now) => new(null, "", _logs, clock: new Clock(now));

    private static DateTimeOffset AtOf(string file) =>
        DateTimeOffset.Parse(JsonNode.Parse(File.ReadAllText(file))!["at"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public async Task With_WSL_stopped_its_snapshot_is_stale_with_its_age_and_values_and_Windows_is_live()
    {
        var token = TestContext.Current.CancellationToken;
        File.Copy(Fixture("snapshot-windows.json"), Path.Combine(_logs, "current-windows.json"));
        var windowsAt = AtOf(Path.Combine(_logs, "current-windows.json"));
        var wsl = JsonNode.Parse(File.ReadAllText(Fixture("snapshot-wsl.json")))!.AsObject();
        wsl["at"] = windowsAt.AddMinutes(-10).AddSeconds(30).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        Write("current-wsl.json", wsl.ToJsonString());

        var client = Snapshots(windowsAt.AddSeconds(30));
        var windows = await client.SnapshotAsync("windows", token);
        Assert.Equal((true, false, TimeSpan.FromSeconds(30)), (windows.HasValue, windows.IsStale, windows.Age!.Value));
        var stopped = await client.SnapshotAsync("wsl", token);
        Assert.True(stopped.IsStale);
        Assert.Equal(TimeSpan.FromMinutes(10), stopped.Age);
        Assert.Equal("The WSL logger last wrote 10 minutes ago.", stopped.Why);
        Assert.Equal(512L, stopped.Value!.Counter("swap_used_mb")); // the last data, kept

        Assert.False((await Snapshots(windowsAt.AddSeconds(90)).SnapshotAsync("windows", token)).IsStale); // 90 s is the limit
        Assert.True((await Snapshots(windowsAt.AddSeconds(91)).SnapshotAsync("windows", token)).IsStale);
        var behind = await Snapshots(windowsAt.AddMinutes(-5)).SnapshotAsync("windows", token); // a clock behind the logger
        Assert.Equal((false, TimeSpan.Zero), (behind.IsStale, behind.Age!.Value));
    }

    [Fact]
    public async Task A_snapshot_held_in_a_replace_is_read_once_let_go_and_one_held_past_a_second_is_unreadable()
    {
        var token = TestContext.Current.CancellationToken;
        var file = Path.Combine(_logs, "current-windows.json");
        File.Copy(Fixture("snapshot-windows.json"), file);
        var client = Snapshots(AtOf(file));

        var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // shares nothing: reads are refused
        var reading = client.SnapshotAsync("windows", token);
        await Task.Delay(300, token);
        Assert.False(reading.IsCompleted, "the read did not wait while the file was held");
        held.Dispose();
        Assert.True((await reading).HasValue);

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var refused = await client.SnapshotAsync("windows", token);
            Assert.Equal(ReadingProblem.Unreadable, refused.Problem);
            Assert.Contains("more than a second", refused.Why);
        }
    }

    [Fact]
    public void The_logger_s_replace_by_rename_is_never_blocked_while_the_client_holds_the_file()
    {
        var file = Path.Combine(_logs, "current-windows.json");
        File.Copy(Fixture("snapshot-windows.json"), file);
        var next = Write("current-windows.json.tmp", File.ReadAllText(Fixture("snapshot-partial.json")));
        using (var reading = MemoryClient.OpenShared(file))
        {
            PosixReplace(next, file); // as the logger replaces it (src/app.cpp, posix_replace)
            Assert.True(reading.ReadByte() >= 0); // the reader goes on reading the old one
        }
        Assert.Contains("\"history_written\":false", File.ReadAllText(file));
    }

    /// <summary>
    /// A rename over the target with POSIX semantics, as bosun.exe's logger replaces its snapshot (src/app.cpp,
    /// posix_replace): it goes through while a reader holds the old file shared for delete.
    /// </summary>
    private static void PosixReplace(string from, string to)
    {
        // DELETE | SYNCHRONIZE, every sharing, OPEN_EXISTING: the access posix_replace asks for.
        using var handle = CreateFileW(from, 0x00010000 | 0x00100000, 0x7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        var target = Encoding.Unicode.GetBytes(Path.GetFullPath(to));
        // FILE_RENAME_INFO: Flags (DWORD, at 0), padding, RootDirectory (handle, at 8), FileNameLength (DWORD, at 16), FileName (at 20).
        var info = new byte[20 + target.Length + 2];
        BitConverter.TryWriteBytes(info.AsSpan(0, 4), 0x1u | 0x2u); // REPLACE_IF_EXISTS | POSIX_SEMANTICS
        BitConverter.TryWriteBytes(info.AsSpan(16, 4), target.Length);
        target.CopyTo(info, 20);
        if (!SetFileInformationByHandle(handle, 22 /* FileRenameInfoEx */, info, info.Length))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, byte[] info, int size);

    [Fact]
    public async Task A_snapshot_that_is_denied_too_large_not_json_unversioned_of_another_version_or_missing_says_which()
    {
        var token = TestContext.Current.CancellationToken;
        var client = Snapshots(DateTimeOffset.UtcNow);
        async Task<Reading<SnapshotDoc>> With(string text)
        {
            Write("current-windows.json", text);
            return await client.SnapshotAsync("windows", token);
        }

        Assert.Equal(ReadingProblem.Missing, (await client.SnapshotAsync("wsl", token)).Problem);
        Directory.CreateDirectory(Path.Combine(_logs, "current-wsl.json")); // a folder where the file goes: denied to a reader
        Assert.Equal(ReadingProblem.Unreadable, (await client.SnapshotAsync("wsl", token)).Problem);

        var big = await With(new string(' ', 2 * 1024 * 1024) + File.ReadAllText(Fixture("snapshot-windows.json")));
        Assert.Equal(ReadingProblem.Invalid, big.Problem);
        Assert.Contains("more than the", big.Why);
        Assert.Equal(ReadingProblem.Invalid, (await With("{ not json")).Problem);
        var unversioned = await With(File.ReadAllText(Fixture("snapshot-windows.json")).Replace("\"schema_version\":1,", ""));
        Assert.Equal(ReadingProblem.Invalid, unversioned.Problem);
        Assert.Contains("no schema_version", unversioned.Why);
        var two = await With(File.ReadAllText(Fixture("snapshot-windows.json")).Replace("\"schema_version\":1", "\"schema_version\":2"));
        Assert.Equal(ReadingProblem.Unsupported, two.Problem);

        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SnapshotAsync("windows", gone.Token));
    }
}

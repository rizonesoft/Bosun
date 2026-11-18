using System.Text;
using System.Text.Json;
using Bosun.Core.Memory;
using Bosun.Core.Queue;
using Xunit;

namespace Bosun.Core.Tests.Queue;

/// <summary>
/// The Queue page's reads and saves (D03 T02 §3): <c>bosun config get|set|unset --json</c> against a fake bosun
/// (tests/Bosun.TestChild's answer mode, which checks its exact arguments), and the job logs from a folder of the test's
/// own, written as <c>bosun run</c> writes them.
/// </summary>
public sealed class QueueClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bosun-queue-" + Guid.NewGuid().ToString("N")[..10]);
    private string Logs => Path.Combine(_dir, "logs");

    public QueueClientTests() => Directory.CreateDirectory(Logs);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A file a test held open a moment ago: the folder is the system's temp, and goes with it.
        }
    }

    private static string Child()
    {
        var mine = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return Path.Combine(mine.Parent!.Parent!.FullName, "Bosun.TestChild", mine.Name, "Bosun.TestChild.exe");
    }

    private string Answer(string text)
    {
        var path = Path.Combine(_dir, "answer-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, text);
        return path;
    }

    private QueueClient Fake(string[] expect, string answer, int exit = 0) =>
        new(Child(), "", Logs, environment: new Dictionary<string, string?>
        {
            ["BOSUN_FAKE_EXPECT"] = JsonSerializer.Serialize(expect),
            ["BOSUN_FAKE_ANSWER"] = Answer(answer),
            ["BOSUN_FAKE_EXIT"] = exit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    // A config document in bosun's exact shape (captured from build/Release/bosun.exe config get --json).
    private static string Config(string file = "\"readable\":true,\"valid\":true,\"error\":null,\"refused\":[]",
        string slots = "\"value\":2,\"source\":\"default\",\"saved\":null,\"min\":1,\"max\":64,\"default\":2,\"variable\":\"BOSUN_SLOTS\",\"variable_error\":null") =>
        "{\"schema_version\":1,\"path\":\"C:\\\\Users\\\\a\\\\AppData\\\\Local\\\\Bosun\\\\config.json\",\"exists\":true," + file + ",\"limits\":{" +
        "\"slots\":{" + slots + "}," +
        "\"min_avail_mb\":{\"value\":6144,\"source\":\"default\",\"saved\":null,\"min\":0,\"max\":1099511627776,\"default\":6144,\"variable\":\"BOSUN_MIN_AVAIL_MB\",\"variable_error\":null}," +
        "\"max_wait_s\":{\"value\":900,\"source\":\"default\",\"saved\":null,\"min\":0,\"max\":604800,\"default\":900,\"variable\":\"BOSUN_MAX_WAIT\",\"variable_error\":null}," +
        "\"min_guest_mb\":{\"value\":3072,\"source\":\"default\",\"saved\":null,\"min\":0,\"max\":1099511627776,\"default\":3072,\"variable\":\"BOSUN_MIN_GUEST_MB\",\"variable_error\":null}}}\n";

    [Fact]
    public async Task The_limits_are_read_whole_with_each_source_range_and_variable()
    {
        var reading = await Fake(["config", "get", "--json"], Config()).LimitsAsync(TestContext.Current.CancellationToken);
        var doc = Assert.IsType<ConfigDoc>(reading.Value);
        Assert.True(doc.Readable && doc.Valid && doc.Error is null && doc.Refused.Count == 0);
        Assert.Equal(["max_wait_s", "min_avail_mb", "min_guest_mb", "slots"], doc.Limits.Keys.Order());
        var slots = doc.Limits["slots"];
        Assert.Equal((2L, "default", (long?)null, 1L, 64L, 2L, "BOSUN_SLOTS", (string?)null), (slots.Value, slots.Source, slots.Saved, slots.Min, slots.Max, slots.Default, slots.Variable, slots.VariableError));
        Assert.Equal(1099511627776L, doc.Limits["min_avail_mb"].Max);
    }

    [Fact]
    public async Task An_unreadable_invalid_or_refused_file_and_a_wrong_variable_each_read_as_themselves()
    {
        var token = TestContext.Current.CancellationToken;
        var unreadable = (await Fake(["config", "get", "--json"], Config("\"readable\":false,\"valid\":false,\"error\":\"Access is denied.\",\"refused\":[]")).LimitsAsync(token)).Value!;
        Assert.False(unreadable.Readable);
        Assert.Equal("Access is denied.", unreadable.Error);
        var invalid = (await Fake(["config", "get", "--json"], Config("\"readable\":true,\"valid\":false,\"error\":\"not JSON at line 1\",\"refused\":[]")).LimitsAsync(token)).Value!;
        Assert.True(invalid.Readable && !invalid.Valid);
        var refused = (await Fake(["config", "get", "--json"], Config("\"readable\":true,\"valid\":true,\"error\":null,\"refused\":[{\"key\":\"slots\",\"error\":\"slots must be 1 to 64\"}]")).LimitsAsync(token)).Value!;
        Assert.Equal(("slots", "slots must be 1 to 64"), (refused.Refused[0].Key, refused.Refused[0].Error));
        var variable = (await Fake(["config", "get", "--json"], Config(slots: "\"value\":3,\"source\":\"environment\",\"saved\":2,\"min\":1,\"max\":64,\"default\":2,\"variable\":\"BOSUN_SLOTS\",\"variable_error\":null")).LimitsAsync(token)).Value!;
        Assert.Equal(("environment", 3L, (long?)2), (variable.Limits["slots"].Source, variable.Limits["slots"].Value, variable.Limits["slots"].Saved));
        var ignored = (await Fake(["config", "get", "--json"], Config(slots: "\"value\":2,\"source\":\"file\",\"saved\":2,\"min\":1,\"max\":64,\"default\":2,\"variable\":\"BOSUN_SLOTS\",\"variable_error\":\"BOSUN_SLOTS=x is not a number\"")).LimitsAsync(token)).Value!;
        Assert.Equal("BOSUN_SLOTS=x is not a number", ignored.Limits["slots"].VariableError);
    }

    [Fact]
    public async Task Each_save_exit_reads_as_its_own_outcome_with_the_tools_words()
    {
        var token = TestContext.Current.CancellationToken;
        var saved = (await Fake(["config", "set", "slots", "3", "--json"], "{\"schema_version\":1,\"ok\":true,\"key\":\"slots\",\"saved\":3}\n").SetLimitAsync("slots", 3, token)).Value!;
        Assert.Equal((LimitSaveOutcome.Saved, (long?)3), (saved.Outcome, saved.Saved));
        var refused = (await Fake(["config", "set", "slots", "99", "--json"], "{\"schema_version\":1,\"ok\":false,\"key\":\"slots\",\"error\":\"slots must be 1 to 64\"}\n", 64).SetLimitAsync("slots", 99, token)).Value!;
        Assert.Equal((LimitSaveOutcome.Refused, "slots must be 1 to 64"), (refused.Outcome, refused.Message));
        var busy = (await Fake(["config", "set", "slots", "3", "--json"], "{\"schema_version\":1,\"ok\":false,\"key\":\"slots\",\"error\":\"another config save is still running, so nothing was changed; try again\"}\n", 75).SetLimitAsync("slots", 3, token)).Value!;
        Assert.Equal(LimitSaveOutcome.Busy, busy.Outcome);
        var damaged = (await Fake(["config", "set", "slots", "3", "--json"], "{\"schema_version\":1,\"ok\":false,\"key\":\"slots\",\"error\":\"C:\\\\x\\\\config.json is damaged (bad), so it isn't overwritten; fix or remove it\"}\n", 74).SetLimitAsync("slots", 3, token)).Value!;
        Assert.Equal(LimitSaveOutcome.Damaged, damaged.Outcome);
        Assert.Contains("is damaged", damaged.Message, StringComparison.Ordinal);
        var unset = (await Fake(["config", "unset", "max_wait_s", "--json"], "{\"schema_version\":1,\"ok\":true,\"key\":\"max_wait_s\",\"saved\":null}\n").UnsetLimitAsync("max_wait_s", token)).Value!;
        Assert.Equal((LimitSaveOutcome.Saved, (long?)null), (unset.Outcome, unset.Saved));
        // Any other exit, or a get that does not exit 0, is a failure of the call, with the tool's words.
        var broken = await Fake(["config", "get", "--json"], "{}", 1).LimitsAsync(token);
        Assert.Equal(ReadingProblem.Failed, broken.Problem);
        var missing = await new QueueClient(null, "bosun.exe is not installed", Logs).SetLimitAsync("slots", 2, token);
        Assert.Equal((ReadingProblem.NotInstalled, "bosun.exe is not installed"), (missing.Problem, missing.Why));
    }

    private static long Ms(string at) => DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static string Job(string endedAt, int i, long? endedMs = null, long? exit = 0) =>
        "{\"schema_version\":1,\"started_at\":\"" + endedAt + "\",\"ended_at\":\"" + endedAt + "\",\"pid\":" + (1000 + i) + ",\"command\":\"job " + i + "\",\"repo\":null,\"side\":\"windows\",\"ns\":\"Bosun\",\"slot\":0,\"waited_s\":0.5,\"ran_s\":3,\"exit_code\":" +
        (exit is { } e ? e.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null") + ",\"timed_out_into_queue\":false,\"ended_ms\":" + (endedMs is { } ms ? ms.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null") + "}";

    private void Jobs(string day, IEnumerable<string> lines, string tail = "\n") =>
        File.WriteAllText(Path.Combine(Logs, $"jobs-{day}.jsonl"), string.Join("\n", lines) + tail);

    [Fact]
    public async Task History_is_the_newest_records_across_the_days_newest_first_a_torn_line_passed_over()
    {
        Jobs("2026-11-16", Enumerable.Range(0, 120).Select(i => Job("2026-11-16T10:00:00Z", i, Ms("2026-11-16T10:00:00Z") + i)));
        Jobs("2026-11-17", Enumerable.Range(120, 120).Select(i => Job("2026-11-17T10:00:00Z", i, Ms("2026-11-17T10:00:00Z") + i)));
        // Today's file: forty records, a hand edit, and a last line still being appended (no newline yet).
        Jobs("2026-11-18", Enumerable.Range(240, 40).Select(i => Job("2026-11-18T10:00:00Z", i, Ms("2026-11-18T10:00:00Z") + i)).Append("not a record"),
            "\n{\"schema_version\":1,\"started_at\":\"2026-11-18T");
        var history = (await new QueueClient(null, "", Logs).HistoryAsync(200, TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(200, history.Jobs.Count);
        Assert.Equal("job 279", history.Jobs[0].Command);                  // newest first
        Assert.Equal("job 80", history.Jobs[199].Command);                 // 40 today, 120 yesterday, 40 the day before
        Assert.Equal(1, history.PassedOver);                               // the hand edit; the line being written is not read yet
        Assert.Empty(history.Unreadable);
    }

    [Fact]
    public async Task One_file_of_more_than_the_count_and_two_jobs_ending_in_one_second_read_in_order()
    {
        // 250 records in one day; the two last end in the same second and are told apart by their milliseconds, and a
        // line an older bosun wrote has no ended_ms and sorts by its seconds.
        var lines = Enumerable.Range(0, 248).Select(i => Job("2026-11-18T09:00:00Z", i, Ms("2026-11-18T09:00:00Z") + i)).ToList();
        lines.Add(Job("2026-11-18T11:00:00Z", 249, Ms("2026-11-18T11:00:00Z") + 900));
        lines.Add(Job("2026-11-18T11:00:00Z", 248, Ms("2026-11-18T11:00:00Z") + 100));
        lines.Add(Job("2026-11-18T10:30:00Z", 300, null));
        Jobs("2026-11-18", lines);
        var history = (await new QueueClient(null, "", Logs).HistoryAsync(200, TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(200, history.Jobs.Count);
        Assert.Equal(["job 249", "job 248", "job 300"], history.Jobs.Take(3).Select(j => j.Command));
    }

    [Fact]
    public async Task A_missing_folder_is_no_history_and_an_unreadable_file_is_said_with_the_others_kept()
    {
        var token = TestContext.Current.CancellationToken;
        var none = await new QueueClient(null, "", Path.Combine(_dir, "nowhere")).HistoryAsync(200, token);
        Assert.Equal((0, 0), (none.Value!.Jobs.Count, none.Value.PassedOver));
        Jobs("2026-11-17", [Job("2026-11-17T10:00:00Z", 1, Ms("2026-11-17T10:00:00Z"))]);
        Jobs("2026-11-18", [Job("2026-11-18T10:00:00Z", 2, Ms("2026-11-18T10:00:00Z"))]);
        // Held open with no sharing, as a file another program has locked.
        using (new FileStream(Path.Combine(Logs, "jobs-2026-11-18.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var history = (await new QueueClient(null, "", Logs).HistoryAsync(200, token)).Value!;
            Assert.Equal(["job 1"], history.Jobs.Select(j => j.Command));
            Assert.Single(history.Unreadable);
            Assert.StartsWith("jobs-2026-11-18.jsonl: ", history.Unreadable[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_log_larger_than_the_tail_read_gives_its_newest_jobs_and_no_more_than_the_tail()
    {
        // Round 1: a whole daily log was read before the count was kept to, so a large or damaged one filled memory.
        var path = Path.Combine(Logs, "jobs-2026-11-18.jsonl");
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            // Junk of more than the tail, then 300 jobs at the end, as a log grown past its bound.
            var junk = new string('x', 1024);
            for (var i = 0; i < (QueueClient.MaxLogTailBytes / 1024) + 64; i++) writer.Write(junk + "\n");
            for (var i = 0; i < 300; i++) writer.Write(Job("2026-11-18T10:00:00Z", i, Ms("2026-11-18T10:00:00Z") + i) + "\n");
        }
        var history = (await new QueueClient(null, "", Logs).HistoryAsync(200, TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(200, history.Jobs.Count);
        Assert.Equal("job 299", history.Jobs[0].Command);
        // Only the tail was read: of the junk, at most the tail's worth less the jobs, never the whole file.
        Assert.True(history.PassedOver < QueueClient.MaxLogTailBytes / 1024, $"{history.PassedOver} lines passed over, more than the tail holds");
    }

    [Fact]
    public async Task A_log_folder_that_cannot_be_listed_is_said_never_shown_as_an_empty_history()
    {
        // Round 2: a file where the folder goes read as "no job has finished yet".
        var notAFolder = Path.Combine(_dir, "logs-file");
        File.WriteAllText(notAFolder, "not a folder");
        var reading = await new QueueClient(null, "", notAFolder).HistoryAsync(200, TestContext.Current.CancellationToken);
        Assert.Equal(ReadingProblem.Unreadable, reading.Problem);
        Assert.Contains("can't be listed", reading.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_bosun_run_is_appending_to_is_read_up_to_its_last_whole_line()
    {
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(Logs, "jobs-2026-11-18.jsonl");
        // Opened for appending as bosun run opens it (FILE_APPEND_DATA, shared for reading), and written to while read.
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var line = Encoding.UTF8.GetBytes(Job("2026-11-18T10:00:00Z", 1, Ms("2026-11-18T10:00:00Z")) + "\n");
        writer.Write(line);
        writer.Write(Encoding.UTF8.GetBytes("{\"schema_version\":1,\"started"));
        writer.Flush();
        var history = (await new QueueClient(null, "", Logs).HistoryAsync(200, token)).Value!;
        Assert.Equal(["job 1"], history.Jobs.Select(j => j.Command));
        Assert.Equal(0, history.PassedOver);
    }
}

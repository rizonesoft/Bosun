using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Bosun.Core.Tests;

/// <summary>
/// The process runner (D02 T02 §3), against a real child: <c>tests/Bosun.TestChild</c>, a program that does to its
/// caller what real tools do (odd arguments, floods, silence, grandchildren), and against the real <c>bosun.exe</c>.
/// </summary>
public class ProcessRunnerTests
{
    private static readonly ProcessRunner Runner = new();

    /// <summary>The built test child: beside this project's own output folder, as the solution's layout puts it.</summary>
    private static string Child()
    {
        var mine = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var path = Path.Combine(mine.Parent!.Parent!.FullName, "Bosun.TestChild", mine.Name, "Bosun.TestChild.exe");
        Assert.True(File.Exists(path), $"the test child isn't built: {path} is missing (build Bosun.slnx)");
        return path;
    }

    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Bosun.slnx")) && File.Exists(Path.Combine(dir.FullName, "design", "tokens", "colors.json")))
                return dir.FullName;
        throw new InvalidOperationException("no checkout above " + AppContext.BaseDirectory);
    }

    private static ProcessRequest Ask(params string[] arguments) => new(Child(), arguments);

    private static Task<ProcessResult> Run(params string[] arguments) => Runner.RunAsync(Ask(arguments), TestContext.Current.CancellationToken);

    // ---- arguments, output, input ----

    [Fact]
    public async Task Arguments_arrive_exactly_as_given()
    {
        string[] sent =
        [
            "plain", "two words", "say \"hi\"", @"C:\folder with space\", @"ends\\", @"slashes\\"" before a quote", "", "a&b|c>d", "%PATH%", "^caret",
            "'single'", "tab\there", "line\nbreak", @"\\server\share", "ünïcödé 日本語 🙂", " leading and trailing ", "\"", @"\",
        ];
        var result = await Run(["args", .. sent]);

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        using var read = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(sent, read.RootElement.EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task A_flood_on_both_streams_is_cut_at_the_cap_and_never_hangs()
    {
        const int cap = 100_000;
        var request = Ask("out", (5 * 1024 * 1024).ToString(), (5 * 1024 * 1024).ToString()) with { MaxOutputBytes = cap };
        var result = await Runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputTruncated && result.ErrorTruncated);
        Assert.Equal(new string('o', cap) + "\n[cut at 100000 bytes]", result.StandardOutput);
        Assert.Equal(new string('e', cap) + "\n[cut at 100000 bytes]", result.StandardError);
    }

    [Fact]
    public void A_megabyte_of_each_stream_is_kept_unless_the_request_says_otherwise() => Assert.Equal(1024 * 1024, Ask("args").MaxOutputBytes);

    [Theory]
    [InlineData(1000, false)]
    [InlineData(999, true)]
    [InlineData(0, true)]
    public async Task Output_is_cut_only_when_it_is_longer_than_the_cap(int cap, bool cut)
    {
        var result = await Runner.RunAsync(Ask("out", "1000", "0") with { MaxOutputBytes = cap }, TestContext.Current.CancellationToken);

        Assert.Equal(cut, result.OutputTruncated);
        Assert.False(result.ErrorTruncated);
        Assert.Equal(cut ? new string('o', cap) + (cap > 0 ? "\n" : "") + $"[cut at {cap} bytes]" : new string('o', 1000), result.StandardOutput);
        Assert.Equal("", result.StandardError);
    }

    [Theory]
    [InlineData("aé", 2, "a")] // é is two bytes: the cap falls inside it
    [InlineData("a🙂b", 2, "a")] // 🙂 is four bytes: one of them kept
    [InlineData("a🙂b", 4, "a")] // three of them kept
    [InlineData("a🙂b", 5, "a🙂")] // all of it kept
    [InlineData("日本", 3, "日")] // the cap falls between two characters
    public async Task A_character_the_cap_falls_inside_is_dropped_whole(string text, int cap, string kept)
    {
        var result = await Runner.RunAsync(Ask("print", text) with { MaxOutputBytes = cap }, TestContext.Current.CancellationToken);

        Assert.True(result.OutputTruncated);
        Assert.Equal($"{kept}\n[cut at {cap} bytes]", result.StandardOutput);
        Assert.DoesNotContain('\uFFFD', result.StandardOutput);
    }

    [Fact]
    public async Task Output_is_read_as_UTF8_and_both_streams_are_kept_apart()
    {
        var result = await Run("print", "naïve 日本 🙂\n", "a warning\n", "0");

        Assert.Equal("naïve 日本 🙂\n", result.StandardOutput);
        Assert.Equal("a warning\n", result.StandardError);
    }

    [Fact]
    public async Task The_exit_code_comes_back()
    {
        var result = await Run("exit", "3");

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task Input_the_working_folder_and_the_environment_reach_the_program()
    {
        var token = TestContext.Current.CancellationToken;
        var folder = Directory.CreateTempSubdirectory("bosun runner ").FullName;
        try
        {
            var echoed = await Runner.RunAsync(Ask("stdin") with { StandardInput = "given on stdin: ünï 🙂" }, token);
            Assert.Equal("given on stdin: ünï 🙂", echoed.StandardOutput);
            Assert.True(echoed.InputDelivered);

            var nothing = await Runner.RunAsync(Ask("stdin"), token); // stdin is closed at once: the child reads its end, and ends
            Assert.Equal((ProcessOutcome.Exited, ""), (nothing.Outcome, nothing.StandardOutput));

            var where = await Runner.RunAsync(Ask("cwd") with { WorkingDirectory = folder }, token);
            Assert.Equal(folder, where.StandardOutput);

            var changes = new Dictionary<string, string?> { ["BOSUN_RUNNER_TEST"] = "sét for this call", ["TEMP"] = null };
            Assert.Equal("sét for this call", (await Runner.RunAsync(Ask("env", "BOSUN_RUNNER_TEST") with { Environment = changes }, token)).StandardOutput);
            Assert.Equal("", (await Runner.RunAsync(Ask("env", "TEMP") with { Environment = changes }, token)).StandardOutput);
            Assert.Equal(Environment.GetEnvironmentVariable("SystemRoot"), (await Runner.RunAsync(Ask("env", "SystemRoot") with { Environment = changes }, token)).StandardOutput);
            Assert.Null(Environment.GetEnvironmentVariable("BOSUN_RUNNER_TEST")); // the caller's own environment is untouched
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Calls_running_side_by_side_each_read_their_own_child()
    {
        var calls = Enumerable.Range(0, 8).Select(i => Run("print", $"call {i}", $"warning {i}")).ToArray();
        var results = await Task.WhenAll(calls);

        for (var i = 0; i < results.Length; i++)
            Assert.Equal(($"call {i}", $"warning {i}", (int?)0), (results[i].StandardOutput, results[i].StandardError, results[i].ExitCode));
    }

    [Fact]
    public async Task A_bare_name_is_found_on_PATH()
    {
        var result = await Runner.RunAsync(new ProcessRequest("cmd", ["/d", "/c", "exit 7"]), TestContext.Current.CancellationToken);

        Assert.Equal((ProcessOutcome.Exited, (int?)7), (result.Outcome, result.ExitCode));
    }

    // ---- what can't be started ----

    [Fact]
    public async Task A_program_that_cant_be_started_is_NotStarted_with_the_reason()
    {
        var token = TestContext.Current.CancellationToken;
        var folder = Directory.CreateTempSubdirectory("bosun-runner-").FullName;
        try
        {
            var batch = Path.Combine(folder, "tool.cmd");
            File.WriteAllText(batch, "@exit 0");
            var notAProgram = Path.Combine(folder, "notes.exe");
            File.WriteAllText(notAProgram, "this is text, not a program");

            (ProcessRequest Request, string Says)[] cases =
            [
                (new("bosun-no-such-program", []), "isn't in any folder on PATH"),
                (new(Path.Combine(folder, "missing.exe"), []), "doesn't exist"),
                (new(batch, []), "batch file"),
                (new(notAProgram, []), "can't start"),
                (Ask("exit", "0") with { WorkingDirectory = Path.Combine(folder, "no such folder") }, "can't start"),
                (new("", []), "Program is empty"),
                (new("   ", []), "Program is empty"),
                (Ask("exit", "0") with { Timeout = TimeSpan.Zero }, "Timeout must be more than zero"),
                (Ask("exit", "0") with { Timeout = TimeSpan.FromSeconds(-1) }, "Timeout must be more than zero"),
                (Ask("exit", "0") with { Timeout = TimeSpan.FromDays(30) }, "Timeout must be at most"),
                (Ask("exit", "0") with { MaxOutputBytes = -1 }, "MaxOutputBytes must be zero or more"),
                (Ask("args", "a\0b"), "Arguments[1] holds a null character"),
                (Ask("exit", "0") with { Environment = new Dictionary<string, string?> { ["A=B"] = "x" } }, "variable name that can't be used"),
                (Ask("args", new string('x', 40_000)), "at most 32,766"),
            ];
            foreach (var (request, says) in cases)
            {
                var result = await Runner.RunAsync(request, token);
                Assert.Equal(ProcessOutcome.NotStarted, result.Outcome);
                Assert.Contains(says, result.Reason);
                Assert.Null(result.ExitCode);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_call_cancelled_before_it_starts_runs_nothing()
    {
        using var beat = new Beat();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await Runner.RunAsync(Ask("tree", beat.File, "100"), cancelled.Token);

        Assert.Equal(ProcessOutcome.Cancelled, result.Outcome);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(beat.File), "nothing was started, so nothing wrote");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_start_that_is_slow_is_inside_the_limit_and_the_cancel_and_its_command_never_takes_effect(bool cancelIt, bool childExists)
    {
        // Round 1: the start ran before the limit began and couldn't be cancelled, so a PATH folder on a dead network
        // share held the caller for as long as Windows took. Completion review: and once the call had returned, the
        // late start still let its command run for a moment, so a save could land after the caller was told it was
        // cancelled. Here the start takes 1.5 s, either before the child is created or with the child created and
        // not yet let go; the command writes a file the instant it runs.
        using var beat = new Beat();
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new ProcessRunner((request, gate) =>
        {
            try
            {
                if (childExists) gate.Created = () => Thread.Sleep(1500);
                else Thread.Sleep(1500);
                var child = ChildProcess.Start(request, gate);
                finished.SetResult(null);
                return child;
            }
            catch (Exception e)
            {
                finished.SetResult(e);
                throw;
            }
        });
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (cancelIt) cancel.CancelAfter(300);
        var request = Ask("beat", beat.File) with { Timeout = cancelIt ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(300) };

        var result = await slow.RunAsync(request, cancel.Token);

        Assert.Equal(cancelIt ? ProcessOutcome.Cancelled : ProcessOutcome.TimedOut, result.Outcome);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(1.2), $"returned at its limit, not when the start finished ({result.Elapsed})");
        Assert.IsType<OperationCanceledException>(await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(beat.File), "the command ran after the call had returned");
    }

    [Fact]
    public async Task A_start_gate_is_decided_once()
    {
        var released = new StartGate();
        Assert.True(released.Release());
        Assert.False(released.Abandon()); // the child is running: the caller ends it instead
        released.ThrowIfAbandoned();

        var abandoned = new StartGate();
        Assert.True(abandoned.Abandon());
        Assert.False(abandoned.Release());
        Assert.Throws<OperationCanceledException>(abandoned.ThrowIfAbandoned);

        // A child created and never let go leaves nothing behind: the next call still works.
        var result = await Run("exit", "4");
        Assert.Equal(4, result.ExitCode);
    }

    // ---- input the child doesn't take ----

    [Fact]
    public async Task Input_a_child_never_takes_doesnt_hold_the_call_or_lose_its_result()
    {
        var token = TestContext.Current.CancellationToken;
        var input = new string('i', 4 * 1024 * 1024);

        var gone = await Runner.RunAsync(Ask("exit", "5") with { StandardInput = input }, token);
        Assert.Equal((ProcessOutcome.Exited, (int?)5, false), (gone.Outcome, gone.ExitCode, gone.InputDelivered));

        var echoed = await Runner.RunAsync(Ask("stdin") with { StandardInput = input, MaxOutputBytes = 8 * 1024 * 1024 }, token);
        Assert.Equal((ProcessOutcome.Exited, true, false), (echoed.Outcome, echoed.InputDelivered, echoed.OutputTruncated));
        Assert.True(echoed.StandardOutput == input, "all of the input came back");

        // A child that never reads: the write is stuck on a full pipe until the time limit ends the child.
        var deaf = await Runner.RunAsync(Ask("sleep", "30000") with { StandardInput = input, Timeout = TimeSpan.FromMilliseconds(300) }, token);
        Assert.Equal((ProcessOutcome.TimedOut, false), (deaf.Outcome, deaf.InputDelivered));
        Assert.Null(deaf.ExitCode);
        Assert.True(deaf.Elapsed < TimeSpan.FromSeconds(2.3), $"ended at its limit, not after {deaf.Elapsed}");
    }

    // ---- the time limit, cancelling, and what the child started ----

    [Fact]
    public async Task Cancelling_ends_the_child_and_what_it_started_within_a_second()
    {
        using var beat = new Beat();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = Runner.RunAsync(Ask("tree", beat.File, "30000"), cancel.Token);
        await beat.HasRun();

        var clock = Stopwatch.StartNew();
        cancel.Cancel();
        var result = await call;

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"returned {clock.Elapsed} after the cancel");
        Assert.Equal(ProcessOutcome.Cancelled, result.Outcome);
        Assert.Null(result.ExitCode);
        await beat.IsGoneAndSilent();
    }

    [Fact]
    public async Task The_time_limit_ends_the_child_and_what_it_started()
    {
        using var beat = new Beat();
        var limit = TimeSpan.FromSeconds(3);

        var result = await Runner.RunAsync(Ask("tree", beat.File, "30000") with { Timeout = limit }, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.InRange(result.Elapsed, limit, limit + TimeSpan.FromSeconds(2));
        await beat.HasRun(); // it ran, so its being gone means it was ended
        await beat.IsGoneAndSilent();
    }

    [Fact]
    public async Task What_a_child_left_running_when_it_exited_ends_with_the_call()
    {
        using var beat = new Beat();

        // `orphan` exits only once its grandchild has written, leaving it with no parent: a walk down the tree from
        // the child would not find it.
        var result = await Run("orphan", beat.File);

        Assert.Equal((ProcessOutcome.Exited, (int?)0), (result.Outcome, result.ExitCode));
        await beat.HasRun();
        await beat.IsGoneAndSilent();
    }

    // ---- JSON ----

    private const string Good = """{"schema_version":1,"name":"queue"}""";

    private static Task<JsonResult<Sample>> Json(int version, ProcessRequest request, CancellationToken? token = null) =>
        Runner.RunJsonAsync(request, TestJson.Default.Sample, version, token ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_supported_version_reads_the_value_and_keeps_what_the_tool_warned()
    {
        var plain = Assert.IsType<JsonResult<Sample>.Ok>(await Json(1, Ask("print", Good + "\n")));
        Assert.Equal("queue", plain.Value.Name);
        Assert.Equal("", plain.Process.StandardError);

        var warned = Assert.IsType<JsonResult<Sample>.Ok>(await Json(1, Ask("print", Good, "[bosun] ignoring a damaged config.json\n", "0")));
        Assert.Equal("queue", warned.Value.Name);
        Assert.Equal("[bosun] ignoring a damaged config.json\n", warned.Process.StandardError);
    }

    [Theory]
    [InlineData("""{"schema_version":2,"name":"queue"}""", 1, 2)] // a newer tool
    [InlineData("""{"schema_version":2,"name":"queue","added":true}""", 3, 2)] // an older tool
    [InlineData("""{"schema_version":2}""", 1, 2)] // a newer shape is never read with defaults where fields went
    [InlineData("""{"schema_version":0,"name":"queue"}""", 1, 0)]
    public async Task Another_schema_version_is_Unsupported_never_a_value(string printed, int supported, long found)
    {
        var result = Assert.IsType<JsonResult<Sample>.Unsupported>(await Json(supported, Ask("print", printed)));

        Assert.Equal((found, supported), (result.Found, result.Supported));
        Assert.Equal(printed, result.Process.StandardOutput);
    }

    [Theory]
    [InlineData("the queue is fine", "")] // not JSON
    [InlineData("", "")] // nothing at all
    [InlineData("[1,2]", "not an object: an array")]
    [InlineData("\"queue\"", "not an object: a string")]
    [InlineData("""{"name":"queue"}""", "no schema_version")]
    [InlineData("""{"schema_version":"1","name":"queue"}""", "not a whole number: a string")]
    [InlineData("""{"schema_version":1.5,"name":"queue"}""", "not a whole number: a number")]
    [InlineData("""{"schema_version":null,"name":"queue"}""", "not a whole number: null")]
    [InlineData("""{"schema_version":1}""", "name")] // the type's required field is missing
    [InlineData("""{"schema_version":1,"name":5}""", "")] // and here it is the wrong kind
    [InlineData("""{"schema_version":1,"name":"queue"} {"schema_version":1,"name":"again"}""", "")] // two objects
    public async Task Output_that_isnt_the_object_asked_for_is_Invalid_with_a_reason(string printed, string says)
    {
        var result = Assert.IsType<JsonResult<Sample>.Invalid>(await Json(1, Ask("print", printed)));

        Assert.NotEqual("", result.Reason);
        Assert.Contains(says, result.Reason);
    }

    [Fact]
    public async Task Output_cut_at_the_cap_is_Invalid_not_read()
    {
        var result = Assert.IsType<JsonResult<Sample>.Invalid>(await Json(1, Ask("print", Good) with { MaxOutputBytes = 20 }));

        Assert.Contains("longer than the 20 bytes kept", result.Reason);
    }

    [Fact]
    public async Task A_call_that_didnt_succeed_is_Failed_with_what_happened()
    {
        var refused = Assert.IsType<JsonResult<Sample>.Failed>(await Json(1, Ask("print", Good, "[bosun] slots must be 1 to 64\n", "64")));
        Assert.Equal((ProcessOutcome.Exited, (int?)64, "[bosun] slots must be 1 to 64\n"), (refused.Process.Outcome, refused.Process.ExitCode, refused.Process.StandardError));

        var slow = Assert.IsType<JsonResult<Sample>.Failed>(await Json(1, Ask("sleep", "30000") with { Timeout = TimeSpan.FromMilliseconds(200) }));
        Assert.Equal(ProcessOutcome.TimedOut, slow.Process.Outcome);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(200);
        var cancelled = Assert.IsType<JsonResult<Sample>.Failed>(await Json(1, Ask("sleep", "30000"), cancel.Token));
        Assert.Equal(ProcessOutcome.Cancelled, cancelled.Process.Outcome);

        var missing = Assert.IsType<JsonResult<Sample>.Failed>(await Json(1, new ProcessRequest("bosun-no-such-program", [])));
        Assert.Equal(ProcessOutcome.NotStarted, missing.Process.Outcome);
        Assert.Contains("PATH", missing.Process.Reason);
    }

    [Fact]
    public async Task The_real_bosun_saves_a_limit_and_reads_it_back()
    {
        var token = TestContext.Current.CancellationToken;
        var bosun = Path.Combine(Repo(), "build", "Release", "bosun.exe");
        Assert.True(File.Exists(bosun), $"{bosun} is missing: build it with pwsh scripts/build.ps1");
        var home = Directory.CreateTempSubdirectory("bosun-runner-home-").FullName;
        try
        {
            ProcessRequest Config(params string[] arguments) => new(bosun, ["config", .. arguments]) { Environment = new Dictionary<string, string?> { ["LOCALAPPDATA"] = home, ["BOSUN_SLOTS"] = null } };

            var saved = Assert.IsType<JsonResult<ConfigReply>.Ok>(await Runner.RunJsonAsync(Config("set", "slots", "3", "--json"), TestJson.Default.ConfigReply, 1, token));
            Assert.Equal((true, "slots", (long?)3), (saved.Value.Ok, saved.Value.Key, saved.Value.Saved));
            Assert.True(File.Exists(Path.Combine(home, "Bosun", "config.json")), "saved under the home this call named, not the machine's own");

            var view = Assert.IsType<JsonResult<ConfigView>.Ok>(await Runner.RunJsonAsync(Config("get", "--json"), TestJson.Default.ConfigView, 1, token));
            Assert.Equal(((long?)3, "file"), (view.Value.Limits["slots"].Saved, view.Value.Limits["slots"].Source));

            var refused = Assert.IsType<JsonResult<ConfigReply>.Failed>(await Runner.RunJsonAsync(Config("set", "slots", "0", "--json"), TestJson.Default.ConfigReply, 1, token));
            Assert.Equal(64, refused.Process.ExitCode);
            Assert.Contains("\"schema_version\":1", refused.Process.StandardOutput);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// A grandchild's file, and its end. The grandchild writes its process id, then a line every 100 ms for at most ten
    /// seconds; whatever the runner did, <see cref="Dispose"/> ends it, so no test leaves one running.
    /// </summary>
    private sealed class Beat : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("bosun-runner-beat-").FullName;

        public string File => Path.Combine(_folder, "beat.txt");

        private string[] Lines()
        {
            try
            {
                using var reader = new StreamReader(new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            catch (IOException)
            {
                return []; // not written yet
            }
        }

        private Process? Grandchild()
        {
            if (Lines() is not [var first, ..] || !int.TryParse(first, out var id)) return null;
            try
            {
                var process = Process.GetProcessById(id);
                return process.ProcessName == "Bosun.TestChild" && !process.HasExited ? process : null;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return null; // no such process any more
            }
        }

        /// <summary>The grandchild wrote its id and at least one line: it ran, so its silence later means something.</summary>
        public async Task HasRun()
        {
            for (var tries = 0; tries < 200 && Lines().Length < 2; tries++) await Task.Delay(50);
            Assert.True(Lines().Length >= 2, "the grandchild never ran, so nothing can be said about its ending");
        }

        public async Task IsGoneAndSilent()
        {
            for (var tries = 0; tries < 20 && Grandchild() is not null; tries++) await Task.Delay(50);
            Assert.True(Grandchild() is null, "the grandchild is still running after the call returned");
            var before = Lines().Length;
            // Its own end comes after 100 lines (and its id): fewer means it was ended, not left to finish.
            Assert.True(before < 101, "the grandchild ran to its own end, so the call never ended it");
            await Task.Delay(400);
            Assert.Equal(before, Lines().Length);
        }

        public void Dispose()
        {
            // The test's own ending of the grandchild, independent of the runner under test.
            if (Grandchild() is { } left)
            {
                left.Kill();
                left.WaitForExit(5000);
            }
            Directory.Delete(_folder, recursive: true);
        }
    }
}

public sealed record Sample
{
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record ConfigReply
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("saved")]
    public long? Saved { get; init; }
}

public sealed record ConfigLimit
{
    [JsonPropertyName("saved")]
    public long? Saved { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

public sealed record ConfigView
{
    [JsonPropertyName("limits")]
    public required Dictionary<string, ConfigLimit> Limits { get; init; }
}

[JsonSerializable(typeof(Sample))]
[JsonSerializable(typeof(ConfigReply))]
[JsonSerializable(typeof(ConfigView))]
internal sealed partial class TestJson : JsonSerializerContext;

using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Startup;
using Xunit;

namespace Bosun.Panel.Tests;

/// <summary>
/// Crash reports and the handlers that write them (D02 T02 §1): a report holds what a developer needs and nothing
/// that names the person or the machine, a crash loop can't fill the disk, and a crash on the UI thread ends the
/// panel after telling the person, through the same wiring the application uses.
/// </summary>
public class CrashTests
{
    private static readonly ProcessRunner Runner = new();

    private static Exception Thrown(Action act)
    {
        try
        {
            act();
        }
        catch (Exception e)
        {
            return e; // thrown for real, so it has a stack
        }
        throw new InvalidOperationException("nothing was thrown");
    }

    private static string[] Reports(string crashFolder) =>
        Directory.Exists(crashFolder) ? [.. Directory.GetFiles(crashFolder).Where(file => CrashReports.Owns(Path.GetFileName(file))).Order(StringComparer.Ordinal)] : [];

    // ---- a report ----

    [Fact]
    public void A_report_holds_the_time_the_versions_the_source_and_the_exception_with_its_inner_one()
    {
        using var folder = new TempFolder();
        var crash = folder.File("crash");
        var exception = Thrown(() =>
        {
            try
            {
                throw new FileNotFoundException("the inner reason");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("the outer reason", inner);
            }
        });

        var before = DateTime.UtcNow.AddSeconds(-1);
        var report = CrashReports.Write(crash, exception, CrashHandlers.UiThread);

        Assert.True(report.Saved, report.Failure);
        Assert.Null(report.Failure);
        Assert.Equal(crash, Path.GetDirectoryName(report.Path));
        Assert.Matches(@"^crash-\d{8}-\d{6}-" + Environment.ProcessId + @"-\d+\.txt$", Path.GetFileName(report.Path)!);
        Assert.Equal([report.Path!], Directory.GetFiles(crash)); // and no temporary file beside it

        var lines = File.ReadAllLines(report.Path!);
        Assert.Equal("Bosun Panel crash report", lines[0]);
        string Field(string name) => lines.Single(line => line.StartsWith(name + ": ", StringComparison.Ordinal))[(name.Length + 2)..];
        var time = DateTime.ParseExact(Field("Time (UTC)"), "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.InRange(time, before, DateTime.UtcNow.AddSeconds(1));
        Assert.NotEqual("unknown", Field("Version"));
        Assert.Contains("Windows", Field("Windows"));
        Assert.StartsWith(".NET", Field(".NET"));
        Assert.Equal("UI thread", Field("Caught by"));

        var text = string.Join("\n", lines);
        Assert.Contains("System.InvalidOperationException: the outer reason", text);
        Assert.Contains("System.IO.FileNotFoundException: the inner reason", text);
        Assert.Contains(nameof(A_report_holds_the_time_the_versions_the_source_and_the_exception_with_its_inner_one), text); // a stack frame
    }

    [Fact]
    public void A_report_that_cant_be_written_gives_the_reason_and_throws_nothing()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("crash"), "a file where the crash folder should be");

        var report = CrashReports.Write(folder.File("crash"), new InvalidOperationException("boom"), CrashHandlers.OtherThread);

        Assert.False(report.Saved);
        Assert.Null(report.Path);
        Assert.False(string.IsNullOrWhiteSpace(report.Failure));
        Assert.Equal("a file where the crash folder should be", File.ReadAllText(folder.File("crash")));
    }

    [Fact]
    public void A_report_never_names_the_person_the_machine_or_the_environment()
    {
        using var folder = new TempFolder();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var marker = "marker-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("BOSUN_TEST_SECRET", marker);
        try
        {
            var message = $@"Could not find file '{profile}\AppData\Local\Bosun\panel.json' for {Environment.UserName} on {Environment.MachineName} ({Environment.UserName.ToUpperInvariant()})";
            var report = CrashReports.Write(folder.Path, Thrown(() => throw new FileNotFoundException(message)), CrashHandlers.UiThread);

            var text = File.ReadAllText(report.Path!);
            Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.CommandLine, text, StringComparison.Ordinal);
            // Replaced, not dropped: the report still says what kind of thing stood there.
            Assert.Contains(@"Could not find file '%USERPROFILE%\AppData\Local\Bosun\panel.json' for <user> on <machine> (<user>)", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOSUN_TEST_SECRET", null);
        }
    }

    [Fact]
    public void A_name_is_replaced_as_a_word_of_its_own_and_an_empty_one_replaces_nothing()
    {
        var privacy = new CrashPrivacy(@"C:\Users\al", "al", "BOX");
        Assert.Equal(@"internal call by <user> in %USERPROFILE%\x on <machine>, not Xbox or balance",
            CrashReports.Redact(@"internal call by Al in c:\users\AL\x on box, not Xbox or balance", privacy));
        Assert.Equal("nothing to hide", CrashReports.Redact("nothing to hide", new CrashPrivacy("", "", " ")));
        // A name with characters a pattern would read as its own is still only a name.
        Assert.Equal("by <user>.", CrashReports.Redact("by a.b+c.", new CrashPrivacy("", "a.b+c", "")));
    }

    [Fact]
    public void A_report_is_at_most_256_kb_and_says_when_it_was_cut()
    {
        using var folder = new TempFolder();
        var report = CrashReports.Write(folder.Path, new InvalidOperationException(new string('é', 1024 * 1024)), CrashHandlers.OtherThread);

        var bytes = File.ReadAllBytes(report.Path!);
        Assert.InRange(bytes.Length, CrashReports.MaxBytes - 4, CrashReports.MaxBytes);
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); // cut between characters, never inside one
        Assert.EndsWith("\n" + CrashReports.CutLine + "\n", text, StringComparison.Ordinal);
        Assert.StartsWith("Bosun Panel crash report\n", text, StringComparison.Ordinal);

        // A short one is not cut.
        var small = CrashReports.Write(folder.Path, new InvalidOperationException("short"), CrashHandlers.OtherThread);
        Assert.DoesNotContain(CrashReports.CutLine, File.ReadAllText(small.Path!), StringComparison.Ordinal);
    }

    [Fact]
    public void The_folder_keeps_the_newest_20_reports_and_nothing_else_in_it_is_touched()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("notes.txt"), "a person's notes");
        File.WriteAllText(folder.File("crash-notes.txt"), "named like a report, but not one");

        var written = Enumerable.Range(1, 25).Select(n => CrashReports.Write(folder.Path, new InvalidOperationException("crash " + n), CrashHandlers.OtherThread).Path!).ToList();

        Assert.Equal(25, written.Distinct().Count());
        Assert.Equal(written.Skip(5).Order(StringComparer.Ordinal), Reports(folder.Path));
        Assert.Contains("crash 25", File.ReadAllText(written[^1]), StringComparison.Ordinal);
        Assert.Equal("a person's notes", File.ReadAllText(folder.File("notes.txt")));
        Assert.Equal("named like a report, but not one", File.ReadAllText(folder.File("crash-notes.txt")));
    }

    [Fact]
    public void A_half_written_report_left_by_an_interrupted_write_is_removed_once_it_is_old_and_a_fresh_one_is_left()
    {
        // The completion review: a process ended between writing the temporary file and moving it into place left a
        // .tmp that nothing ever removed, outside the 20 the folder keeps.
        using var folder = new TempFolder();
        var abandoned = folder.File("crash-20260101-000000-4242-1.txt.tmp");
        var inProgress = folder.File("crash-20260101-000000-4242-2.txt.tmp");
        var someoneElses = folder.File("notes.tmp");
        foreach (var file in new[] { abandoned, inProgress, someoneElses }) File.WriteAllText(file, "half a report");
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - CrashReports.AbandonedAfter - TimeSpan.FromMinutes(1));
        File.SetLastWriteTimeUtc(someoneElses, DateTime.UtcNow.AddDays(-30));

        var report = CrashReports.Write(folder.Path, new InvalidOperationException("the next crash"), CrashHandlers.OtherThread);

        Assert.True(report.Saved, report.Failure);
        Assert.False(File.Exists(abandoned), "the abandoned half-written report is gone");
        Assert.True(File.Exists(inProgress), "a write that may be another panel's, in progress, is left alone");
        Assert.True(File.Exists(someoneElses), "a .tmp that is not a report's is not ours to remove");
    }

    // ---- the handlers ----

    [Fact]
    public void An_exception_on_the_ui_thread_is_reported_told_and_exits_1_and_nothing_is_handled_after_detach()
    {
        using var folder = new TempFolder();
        var notices = new List<CrashReport>();
        var exits = new List<int>();

        Sta.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var handlers = CrashHandlers.Attach(dispatcher, folder.Path, notices.Add, exits.Add);

            dispatcher.BeginInvoke(() => throw new InvalidOperationException("thrown on the UI thread"));
            Assert.True(StartupSupport.Pump(() => exits.Count > 0), "the handler never asked for an exit");

            Assert.Equal([1], exits);
            var notice = Assert.Single(notices);
            var report = Assert.Single(Reports(folder.Path));
            Assert.Equal(report, notice.Path);
            var text = File.ReadAllText(report);
            Assert.Contains("Caught by: UI thread", text, StringComparison.Ordinal);
            Assert.Contains("thrown on the UI thread", text, StringComparison.Ordinal);

            handlers.Detach();
            dispatcher.BeginInvoke(() => throw new InvalidOperationException("thrown after detach"));
            // Nothing handles it now, so it comes out of the dispatcher as itself.
            var escaped = Assert.Throws<InvalidOperationException>(() => StartupSupport.Pump(() => false, TimeSpan.FromSeconds(2)));
            Assert.Equal("thrown after detach", escaped.Message);
            Assert.Single(Reports(folder.Path));
            Assert.Equal([1], exits);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LetATaskFault(string marker) => _ = Task.Run(() => throw new InvalidOperationException(marker));

    [Fact]
    public void An_unobserved_task_exception_is_reported_and_the_panel_carries_on()
    {
        using var folder = new TempFolder();
        var notices = new List<CrashReport>();
        var exits = new List<int>();
        var marker = "unobserved " + Guid.NewGuid().ToString("N");

        Sta.Run(() =>
        {
            using var handlers = CrashHandlers.Attach(Dispatcher.CurrentDispatcher, folder.Path, notices.Add, exits.Add);
            LetATaskFault(marker);

            // The event is raised when the faulted task is collected: collect until the report is there.
            string? report = null;
            for (var tries = 0; tries < 100 && report is null; tries++)
            {
                Thread.Sleep(50);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                report = Reports(folder.Path).FirstOrDefault(path => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal));
            }
            Assert.True(report is not null, "no report was written for the unobserved task");
            Assert.Contains("Caught by: Task", File.ReadAllText(report), StringComparison.Ordinal);
        });
        Assert.Empty(notices);
        Assert.Empty(exits);
    }

    [Fact]
    public void An_exception_on_another_thread_is_reported_by_the_domains_handler()
    {
        using var folder = new TempFolder();
        Sta.Run(() =>
        {
            using var handlers = CrashHandlers.Attach(Dispatcher.CurrentDispatcher, folder.Path, _ => { }, _ => { });
            handlers.OnDomainException(this, new UnhandledExceptionEventArgs(new InvalidOperationException("thrown on a thread"), isTerminating: true));
            handlers.OnDomainException(this, new UnhandledExceptionEventArgs("not an exception at all", isTerminating: true));
        });

        var texts = Reports(folder.Path).Select(File.ReadAllText).ToList();
        Assert.Equal(2, texts.Count);
        Assert.All(texts, text => Assert.Contains("Caught by: Thread", text, StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("thrown on a thread", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("not an exception at all", StringComparison.Ordinal));
    }

    // ---- a crash, through the application's wiring ----

    /// <summary>Begins a panel as App does, throws on its UI thread, and returns what the host was asked.</summary>
    private static RecordingHost CrashOnTheUiThread(string dataFolder, string name, Action<RecordingHost> arrange)
    {
        var host = new RecordingHost();
        arrange(host);
        Sta.Run(() =>
        {
            using var startup = new PanelStartup(host.Host(name, dataFolder));
            Assert.True(startup.Begin());
            Dispatcher.CurrentDispatcher.BeginInvoke(() => throw new InvalidOperationException("thrown on the UI thread"));
            Assert.True(StartupSupport.Pump(() => host.Exits.Count > 0), "the panel never asked to exit");
        });
        return host;
    }

    [Fact]
    public void After_a_crash_answering_no_exits_1_without_starting_the_panel_again()
    {
        using var data = new TempFolder();
        var host = CrashOnTheUiThread(data.Path, StartupSupport.Name(), h => h.StartAgain = false);

        Assert.Equal([1], host.Exits);
        Assert.Equal(0, host.Restarts);
        Assert.Empty(host.Told);
        var report = Assert.Single(Reports(PanelPaths.Crash(data.Path)));
        var question = Assert.Single(host.Asked);
        Assert.Contains(report, question, StringComparison.Ordinal); // the notice names the report
        Assert.Contains("Start Bosun Panel again?", question, StringComparison.Ordinal);
        Assert.Equal(PanelStartup.CrashNotice(new CrashReport(report, null)), question);
    }

    [Fact]
    public void After_a_crash_answering_yes_gives_the_instance_up_then_starts_one_panel_and_exits_1()
    {
        using var data = new TempFolder();
        var name = StartupSupport.Name();
        InstanceRole? roleOfANewPanel = null;
        var host = CrashOnTheUiThread(data.Path, name, h =>
        {
            h.StartAgain = true;
            // What the new process would find: the name must be free by now, or it would only ask this one forward.
            h.OnRestart = () =>
            {
                using var next = SingleInstance.Start(name, TimeSpan.FromMilliseconds(300));
                roleOfANewPanel = next.Role;
            };
        });

        Assert.Equal(1, host.Restarts);
        Assert.Equal(InstanceRole.First, roleOfANewPanel);
        Assert.Equal([1], host.Exits);
        Assert.Empty(host.Told);
        Assert.Single(Reports(PanelPaths.Crash(data.Path)));
    }

    [Fact]
    public void A_crash_whose_report_cant_be_saved_still_tells_the_person_with_the_reason()
    {
        using var data = new TempFolder();
        File.WriteAllText(PanelPaths.Crash(data.Path), "a file where the crash folder should be");
        var host = CrashOnTheUiThread(data.Path, StartupSupport.Name(), h => h.StartAgain = false);

        var question = Assert.Single(host.Asked);
        Assert.Contains("No report could be saved: ", question, StringComparison.Ordinal);
        Assert.DoesNotContain("A report was saved", question, StringComparison.Ordinal);
        Assert.DoesNotContain(".txt", question, StringComparison.Ordinal);
        Assert.Contains("Start Bosun Panel again?", question, StringComparison.Ordinal);
        Assert.Equal([1], host.Exits);
        Assert.Equal(0, host.Restarts);
    }

    [Fact]
    public void A_restart_that_cant_be_started_is_told_once_and_the_panel_still_exits_1()
    {
        using var data = new TempFolder();
        var host = CrashOnTheUiThread(data.Path, StartupSupport.Name(), h =>
        {
            h.StartAgain = true;
            h.OnRestart = () => throw new FileNotFoundException("the panel's program is gone");
        });

        Assert.Equal(1, host.Restarts); // tried once, never again
        Assert.Equal("Bosun Panel could not be started again: the panel's program is gone", Assert.Single(host.Told));
        Assert.Equal([1], host.Exits);
        Assert.Single(host.Asked);
        Assert.Single(Reports(PanelPaths.Crash(data.Path))); // the failed restart is not a second crash
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("C:\\data\0folder")]
    public void A_data_folder_variable_that_cant_be_a_path_is_said_so_and_the_launch_exits_4_with_the_handlers_attached(string variable)
    {
        // The completion review: the application read the variable before its crash handlers existed, and a value
        // that can't be a path threw there, ending the start with no word from Bosun.
        Assert.False(PanelPaths.TryData(variable, out var fallback, out var refusal));
        Assert.Equal(PanelPaths.Default(), fallback);
        Assert.Contains(PanelPaths.Variable, refusal, StringComparison.Ordinal);
        Assert.Equal(PanelPaths.Default(), PanelPaths.Data(variable)); // and the plain read never throws

        var host = new RecordingHost();
        Sta.Run(() =>
        {
            using var startup = new PanelStartup(host.Host(StartupSupport.Name(), variable));
            Assert.Equal(PanelPaths.Default(), startup.DataFolder);
            Assert.False(startup.Begin());
            Assert.Null(startup.Role); // no instance was taken
        });
        Assert.Equal([PanelStartup.ExitBadDataFolder], host.Exits);
        Assert.Equal(4, host.Exits[0]);
        Assert.Equal(refusal, Assert.Single(host.Told));
        Assert.Equal(0, host.WindowsMade);
    }

    [Fact]
    public void The_data_folder_is_the_variables_when_set_and_local_application_data_when_not()
    {
        using var folder = new TempFolder();
        Assert.Equal(folder.Path, PanelPaths.Data(folder.Path));
        Assert.Equal(Path.Combine(folder.Path, "crash"), PanelPaths.Crash(PanelPaths.Data(folder.Path)));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, "Bosun"), PanelPaths.Data(null));
        Assert.Equal(Path.Combine(local, "Bosun"), PanelPaths.Data(""));
        Assert.Equal(Path.Combine(local, "Bosun", "crash"), PanelPaths.Crash(PanelPaths.Data(null)));
    }

    // ---- a real process that dies ----

    [Fact]
    public async Task A_real_process_that_dies_on_a_background_thread_leaves_its_report()
    {
        using var data = new TempFolder();
        var request = new ProcessRequest(StartupSupport.Built("Bosun.Panel.TestChild", "Bosun.Panel.TestChild.exe"), ["crash-thread"])
        {
            Environment = new Dictionary<string, string?> { [InstanceName.Variable] = StartupSupport.Name(), [PanelPaths.Variable] = data.Path },
            Timeout = TimeSpan.FromSeconds(30), // the runner ends it, and everything it started, at the limit
        };
        var result = await Runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(66, result.ExitCode); // it did not outlive its own exception
        var report = Assert.Single(Reports(PanelPaths.Crash(data.Path)));
        var text = File.ReadAllText(report);
        Assert.Contains("Caught by: Thread", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: thrown on a background thread by the test child", text, StringComparison.Ordinal);
    }
}

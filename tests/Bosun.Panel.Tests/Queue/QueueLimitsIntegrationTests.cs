using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Core.Queue;
using Bosun.Panel.Queue;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Queue;

/// <summary>
/// The page's own Save governs real jobs (D03 T02 §3): the Queue page over <see cref="QueueClient"/> and the built
/// <c>build/Release/bosun.exe</c> saves the limits, and jobs started with <c>bosun run</c> then hold two slots at once,
/// and after the page saves one slot, one at a time. Everything runs in a home of the test's own
/// (<c>LOCALAPPDATA</c>, where <c>config.json</c> lives, one per user) and a slot namespace of its own, with the limit
/// variables cleared, so the operator's limits and jobs are never touched; every wait is bounded, and every process
/// tree it starts is ended if the test fails.
/// </summary>
public sealed class QueueLimitsIntegrationTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "bosun-queue-limits-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string _ns = "BosunPanelLimits" + Environment.ProcessId;
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            // It ended between the check and the kill: there is nothing left of it to end.
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        // A job log still held open for a moment by a job's end: the temp folder is left for the system to clear, and
        // the test's result stands.
        catch (IOException)
        {
        }
    }

    private static string Built(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Bosun.slnx")) && File.Exists(Path.Combine(dir.FullName, "design", "tokens", "colors.json")))
            {
                var exe = Path.Combine(dir.FullName, "build", "Release", name);
                Assert.True(File.Exists(exe), $"the native tools aren't built: {exe} is missing (pwsh scripts\\build.ps1)");
                return exe;
            }
        throw new InvalidOperationException("no checkout above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// What every bosun this test starts is given: its own home, and none of the limit variables or the slot marker a
    /// suite run through <c>bosun run</c> hands down (a job with the marker passes straight through without queueing).
    /// </summary>
    private Dictionary<string, string?> Environment_() => new()
    {
        ["LOCALAPPDATA"] = _home,
        ["BOSUN_SLOT"] = null,
        ["BOSUN_SLOTS"] = null,
        ["BOSUN_MIN_AVAIL_MB"] = null,
        ["BOSUN_MAX_WAIT"] = null,
        ["BOSUN_MIN_GUEST_MB"] = null,
    };

    private Process Job(string bosun, string sleeper, string marks, int ms)
    {
        var start = new ProcessStartInfo(bosun) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "run", "--ns", _ns, "--", sleeper, marks, ms.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        foreach (var (name, value) in Environment_())
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        var process = Process.Start(start) ?? throw new InvalidOperationException("bosun run did not start");
        _started.Add(process);
        // Read so a full pipe never holds the job.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    /// <summary>How many of the sleepers' runs overlapped at most, from their start and end marks.</summary>
    private static int MaxOverlap(string marks)
    {
        var events = File.ReadAllLines(marks).Where(l => l.Length > 0).Select(l => l.Split(' '))
            .Select(p => (At: ulong.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), Change: p[0] == "start" ? 1 : -1))
            .OrderBy(e => e.At).ThenBy(e => e.Change).ToList();
        var (now, most) = (0, 0);
        foreach (var e in events)
        {
            now += e.Change;
            most = Math.Max(most, now);
        }
        return most;
    }

    /// <summary>Pumps this thread's dispatcher until <paramref name="done"/>, at most <paramref name="limit"/>.</summary>
    private static void Until(Func<bool> done, TimeSpan limit, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < limit, $"{what} did not happen within {limit.TotalSeconds:0} s");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Each job ended, and ended well (round 3: a job failing at once after admission let the overlap count pass on
    /// the other alone), and its marks file holds a start and an end for each.
    /// </summary>
    private static void WaitAll(IReadOnlyList<Process> jobs, string marks)
    {
        foreach (var job in jobs)
        {
            Assert.True(job.WaitForExit(TimeSpan.FromSeconds(60)), "a bosun run job did not end within 60 s");
            Assert.Equal(0, job.ExitCode);
        }
        var lines = File.ReadAllLines(marks).Where(l => l.Length > 0).Select(l => l.Split(' ')[0]).ToList();
        Assert.Equal((jobs.Count, jobs.Count), (lines.Count(l => l == "start"), lines.Count(l => l == "end")));
    }

    /// <summary>Presses the page's Save, as the button does, and waits for it.</summary>
    private static void Save(QueuePage page)
    {
        var save = ((Bosun.Panel.Mvvm.AsyncCommand)page.Save).ExecuteAsync();
        Until(() => save.IsCompleted, TimeSpan.FromSeconds(60), "the save");
        Until(() => !page.IsSaving, TimeSpan.FromSeconds(5), "the save's end");
    }

    [Fact]
    public void The_page_s_Save_takes_the_queue_from_two_usable_slots_to_one()
    {
        var bosun = Built("bosun.exe");
        var sleeper = Built("test_sleeper.exe");
        Directory.CreateDirectory(_home);
        var client = new QueueClient(bosun, "", Path.Combine(_home, "Bosun", "logs"), environment: Environment_());
        PageRenderer.InPage("Dark", () => new Grid(), _ =>
        {
            var scheduler = new RefreshScheduler(driver: new QueuePageTests.Driver());
            var log = Shells.Log();
            var page = new QueuePage(client, scheduler, log);
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["queue"] = page }), log);
            shell.Show(shell.Sections.Single(s => s.Key == "queue"));
            Until(() => page.FormReady && page.State == PageState.Data, TimeSpan.FromSeconds(60), "the first reading");
            Assert.Equal("2", page.Field("slots").Text);                  // a home of its own: the defaults

            // Memory never holds a job here: the floor for the second slot is 0, saved through the page.
            page.Field("min_avail_mb").Text = "0";
            Save(page);
            Assert.Equal(("Saved: 0", null), (page.Field("min_avail_mb").Source, page.Field("min_avail_mb").Error));

            var two = Path.Combine(_home, "two.txt");
            var first = new[] { Job(bosun, sleeper, two, 2500), Job(bosun, sleeper, two, 2500) };
            WaitAll(first, two);
            Assert.Equal(2, MaxOverlap(two));                              // two slots: both at once

            page.Field("slots").Text = "1";
            Save(page);
            Assert.Equal(("1", "Saved: 1", null), (page.Field("slots").Text, page.Field("slots").Source, page.Field("slots").Error));
            Assert.Contains(log.Entries, e => e.Severity == LogSeverity.Info && e.Message == "Save queue limits: slots 1");

            var one = Path.Combine(_home, "one.txt");
            var second = new[] { Job(bosun, sleeper, one, 2500), Job(bosun, sleeper, one, 2500) };
            WaitAll(second, one);
            Assert.Equal(1, MaxOverlap(one));                              // one slot: the second job waited for it

            // The page's history reads the four jobs back from the test's own job log, one of the last two having waited.
            page.RefreshNow!.Execute(null);
            Until(() => page.History.Count >= 4, TimeSpan.FromSeconds(30), "the four jobs in the history");
            Assert.Contains(page.History.Take(2), line => line.WaitedSeconds >= 1);
            return 0;
        });
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Bosun.Desktop;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// The desktop lock (D02 T02 §11, scripts/lib/DesktopLock.cs), each case on a lock name of its own, so none meets the
/// run's own hold or the operator's. Helpers are Bosun.TestChild processes in job objects of their own
/// (<see cref="PanelProcess"/>), ended with the test however it ends.
/// </summary>
public class DesktopLockTests
{
    private static bool Within(ManualResetEventSlim signal, TimeSpan limit) => signal.Wait(limit, TestContext.Current.CancellationToken);

    private static string Name() => "test-" + Guid.NewGuid().ToString("N")[..12];

    private static string Folder()
    {
        var folder = Path.Combine(PanelProcess.ScratchRoot(), "desktop-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A child holding the lock until it finds a file <c>release</c> in its folder.</summary>
    private static PanelProcess Holding(string name, string folder)
    {
        var child = PanelProcess.StartProgram(PanelProcess.TestChildExe, "hold-desktop", name, folder);
        Ui.Until(() => File.Exists(Path.Combine(child.DataFolder, "held")), Ui.Short, "the child to hold the lock");
        return child;
    }

    /// <summary>Whether another process finds the lock taken at this moment: "taken" or "free".</summary>
    internal static string Probe(string name)
    {
        using var child = PanelProcess.StartProgram(PanelProcess.TestChildExe, "probe-desktop", name);
        var answer = Path.Combine(child.DataFolder, "probe");
        Ui.Until(() => File.Exists(answer) && new FileInfo(answer).Length > 0, Ui.Short, "the probe's answer");
        return File.ReadAllText(answer);
    }

    internal sealed record Recorded(int Pid, ulong ProcessStart, string Command);

    internal static Recorded Record(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        return new Recorded(root.GetProperty("pid").GetInt32(), root.GetProperty("process_start").GetUInt64(), root.GetProperty("command").GetString()!);
    }

    /// <summary>A contender on a thread of its own, which takes, holds and releases the lock there (a mutex is its thread's).</summary>
    private sealed class Contender : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _release = new();

        public Contender(string name, string folder, TimeSpan wait)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    using (DesktopLock.Acquire(name, folder, wait, said => { Heard = said; Waiting.Set(); }))
                    {
                        Acquired.Set();
                        _release.Wait();
                    }
                }
                catch (Exception e)
                {
                    Failure = e;
                    Acquired.Set();
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public ManualResetEventSlim Waiting { get; } = new();

        public ManualResetEventSlim Acquired { get; } = new();

        public string? Heard { get; private set; }

        public Exception? Failure { get; private set; }

        public void Dispose()
        {
            _release.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(10)), "the contender's thread ended");
        }
    }

    [Fact]
    public void A_waiting_contender_names_the_holder_then_takes_the_lock_on_release()
    {
        var (name, folder) = (Name(), Folder());
        using var holder = Holding(name, folder);
        using var contender = new Contender(name, folder, TimeSpan.FromSeconds(20));
        Assert.True(Within(contender.Waiting, Ui.Short), "the contender said whom it waits for");
        Assert.Contains($"pid {holder.Id} (", contender.Heard);
        Assert.Contains("hold-desktop", contender.Heard);
        Assert.False(Within(contender.Acquired, TimeSpan.FromMilliseconds(500)), "the contender waits while the holder holds");
        var released = Stopwatch.StartNew();
        File.WriteAllText(Path.Combine(holder.DataFolder, "release"), "");
        Assert.True(Within(contender.Acquired, TimeSpan.FromSeconds(15)), "the same contender took the lock within its 20 s");
        Assert.Null(contender.Failure);
        Assert.True(released.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void A_wait_that_runs_out_names_the_holder_s_pid_command_and_start()
    {
        var (name, folder) = (Name(), Folder());
        using var holder = Holding(name, folder);
        var started = DateTime.FromFileTimeUtc((long)DesktopLock.StartOf(holder.Id)!.Value).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var waited = Stopwatch.StartNew();
        var refused = Assert.Throws<DesktopLockException>(() => DesktopLock.Acquire(name, folder, TimeSpan.FromSeconds(1)));
        Assert.True(waited.Elapsed >= TimeSpan.FromSeconds(0.95), $"it waited its 1 s first ({waited.Elapsed})");
        Assert.True(refused.TimedOut);
        Assert.Contains($"pid {holder.Id} (", refused.Message);
        Assert.Contains("hold-desktop", refused.Message);
        Assert.Contains("started " + started, refused.Message);
        Assert.Contains(DesktopLock.WaitVariable, refused.Message);
    }

    [Fact]
    public void A_wait_of_zero_refuses_at_once_and_says_nothing_while_waiting()
    {
        var (name, folder) = (Name(), Folder());
        using var holder = Holding(name, folder);
        string? heard = null;
        var waited = Stopwatch.StartNew();
        Assert.Throws<DesktopLockException>(() => DesktopLock.Acquire(name, folder, TimeSpan.Zero, said => heard = said));
        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Null(heard);
    }

    [Fact]
    public void A_holder_killed_holding_the_lock_hands_it_to_the_contender_waiting_through_the_kill()
    {
        var (name, folder) = (Name(), Folder());
        var holder = Holding(name, folder);
        using var contender = new Contender(name, folder, TimeSpan.FromSeconds(20));
        try
        {
            Assert.True(Within(contender.Waiting, Ui.Short), "the contender waits with its handle open");
        }
        finally
        {
            holder.Dispose();  // ends the child's job: the mutex is abandoned, never released
        }
        Assert.True(Within(contender.Acquired, Ui.Short), "the waiting contender took the abandoned lock");
        Assert.Null(contender.Failure);
        using var me = Process.GetCurrentProcess();
        Assert.Equal(me.Id, Record(DesktopLock.RecordPath(name, folder)).Pid);
        Assert.Equal("taken", Probe(name));
        contender.Dispose();
        Assert.Equal("free", Probe(name));
    }

    [Fact]
    public void A_missing_malformed_or_stale_record_names_no_holder()
    {
        var (name, folder) = (Name(), Folder());
        var path = DesktopLock.RecordPath(name, folder);
        Assert.Equal(DesktopLock.Unnamed, DesktopLock.Describe(name, folder));
        File.WriteAllText(path, "{");
        Assert.Equal(DesktopLock.Unnamed, DesktopLock.Describe(name, folder));
        File.WriteAllText(path, "[1]");
        Assert.Equal(DesktopLock.Unnamed, DesktopLock.Describe(name, folder));
        using var me = Process.GetCurrentProcess();
        var mine = (ulong)me.StartTime.ToFileTimeUtc();
        File.WriteAllText(path, $"{{\"schema_version\":1,\"pid\":{me.Id},\"process_start\":{mine + 1},\"command\":\"x\"}}");
        Assert.Equal(DesktopLock.Unnamed, DesktopLock.Describe(name, folder));  // a reused pid: its start differs
        int gone;
        ulong goneStart;
        using (var child = PanelProcess.StartProgram(PanelProcess.TestChildExe, "sleep", "30000"))
        {
            gone = child.Id;
            goneStart = DesktopLock.StartOf(gone)!.Value;
        }
        File.WriteAllText(path, $"{{\"schema_version\":1,\"pid\":{gone},\"process_start\":{goneStart},\"command\":\"x\"}}");
        Assert.Equal(DesktopLock.Unnamed, DesktopLock.Describe(name, folder));  // a holder that has ended
        File.WriteAllText(path, $"{{\"schema_version\":1,\"pid\":{me.Id},\"process_start\":{mine},\"command\":\"me\"}}");
        Assert.StartsWith($"pid {me.Id} (me), started ", DesktopLock.Describe(name, folder));
    }

    [Fact]
    public void A_record_that_can_t_be_written_changes_nothing_about_holding_and_releasing()
    {
        var name = Name();
        var notAFolder = Path.Combine(Folder(), "file");
        File.WriteAllText(notAFolder, "");
        var said = new StringWriter();
        using (DesktopLock.Acquire(name, notAFolder, TimeSpan.Zero, errors: said))
        {
            Assert.Contains("couldn't be written", said.ToString());
            Assert.Equal("taken", Probe(name));
        }
        Assert.Equal("free", Probe(name));
    }

    [Fact]
    public void The_smoke_refuses_a_hold_whose_record_can_t_be_written_and_lets_the_lock_go()
    {
        // Its captures would otherwise refuse the inherited hold and wait on their own parent (round 1, P2).
        var name = Name();
        var notAFolder = Path.Combine(Folder(), "file");
        File.WriteAllText(notAFolder, "");
        var refused = Assert.Throws<DesktopLockException>(() => DesktopFixture.Hold(name, notAFolder, TimeSpan.Zero));
        Assert.Contains("couldn't be written", refused.Message);
        Assert.Equal("free", Probe(name));
    }

    [Fact]
    public void A_record_names_its_holder_by_label_never_by_command_line()
    {
        // A command line can carry a secret, which a waiting process would print (round 1, P1).
        var (name, folder) = (Name(), Folder());
        using (DesktopLock.Acquire(name, folder, TimeSpan.Zero, label: "a label\nwith a second line"))
        {
            var recorded = Record(DesktopLock.RecordPath(name, folder)).Command;
            Assert.Equal("a label with a second line", recorded);
        }
        using (DesktopLock.Acquire(name, folder, TimeSpan.Zero))
        {
            using var me = Process.GetCurrentProcess();
            Assert.Equal(me.ProcessName, Record(DesktopLock.RecordPath(name, folder)).Command);
        }
        Assert.Equal(160, DesktopLock.Shorten(new string('x', 400)).Length);
    }

    [Fact]
    public void A_release_removes_the_record_and_leaves_a_successor_s_alone()
    {
        var (name, folder) = (Name(), Folder());
        var path = DesktopLock.RecordPath(name, folder);
        var holder = DesktopLock.Acquire(name, folder, TimeSpan.Zero);
        Assert.True(File.Exists(path));
        holder.Dispose();
        Assert.False(File.Exists(path));
        var again = DesktopLock.Acquire(name, folder, TimeSpan.Zero);
        // A record that isn't this holder's (a successor wrote it) stays.
        File.WriteAllText(path, "{\"schema_version\":1,\"pid\":4,\"process_start\":1,\"command\":\"successor\"}");
        again.Dispose();
        Assert.Equal("successor", Record(path).Command);
    }

    [Fact]
    public void A_hold_taken_on_one_thread_is_released_from_another()
    {
        var (name, folder) = (Name(), Folder());
        DesktopHold? hold = null;
        var taking = new Thread(() => hold = new DesktopHold(name, folder, TimeSpan.Zero));
        taking.Start();
        taking.Join();
        Assert.Equal("taken", Probe(name));
        hold!.Dispose();
        Assert.Equal("free", Probe(name));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("x")]
    [InlineData("86401")]
    [InlineData("99999999999")]
    [InlineData(" 5")]
    public void A_wait_that_isn_t_whole_seconds_up_to_a_day_is_refused(string value)
    {
        var refused = Assert.Throws<DesktopLockException>(() => DesktopLock.ParseWait(value));
        Assert.Contains(DesktopLock.WaitVariable, refused.Message);
        Assert.False(refused.TimedOut);
    }

    [Fact]
    public void A_wait_is_zero_to_a_day_and_unset_is_fifteen_minutes()
    {
        Assert.Equal(TimeSpan.Zero, DesktopLock.ParseWait("0"));
        Assert.Equal(TimeSpan.FromSeconds(86400), DesktopLock.ParseWait("86400"));
        Assert.Equal(TimeSpan.FromSeconds(900), DesktopLock.ParseWait(null));
        Assert.Equal(TimeSpan.FromSeconds(900), DesktopLock.ParseWait(""));
    }

    [Theory]
    [InlineData("Bosun.Desktop")]
    [InlineData("bosun desktop")]
    [InlineData("a/b")]
    public void A_lock_name_outside_lower_case_digits_and_hyphens_is_refused(string value)
    {
        Assert.Contains(DesktopLock.LockVariable, Assert.Throws<DesktopLockException>(() => DesktopLock.ParseName(value)).Message);
    }

    [Fact]
    public void A_lock_name_is_at_most_64_characters_and_unset_is_the_default()
    {
        Assert.Throws<DesktopLockException>(() => DesktopLock.ParseName(new string('a', 65)));
        Assert.Equal(new string('a', 64), DesktopLock.ParseName(new string('a', 64)));
        Assert.Equal(DesktopLock.DefaultName, DesktopLock.ParseName(null));
        Assert.Equal(DesktopLock.DefaultName, DesktopLock.ParseName(""));
    }
}

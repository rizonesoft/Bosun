using System.Diagnostics;
using System.IO;
using Bosun.Desktop;
using Xunit;

// One test at a time. Every test here owns the desktop while it runs: it asks for the foreground, moves the pointer
// and presses keys, and two at once would take each other's window or pointer.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

// One run at a time, too (D02 T02 §11): the run holds the desktop lock from before its first test to after its last,
// so a second smoke run or a capture-window.ps1 capture waits for it rather than taking its windows.
[assembly: AssemblyFixture(typeof(Bosun.Panel.E2E.DesktopFixture))]

// A default test never leaves a panel in front, which would take the operator's keyboard (D02 T02 §12).
[assembly: Bosun.Panel.E2E.ForegroundKept]

namespace Bosun.Panel.E2E;

/// <summary>
/// The run's hold on the desktop lock. A wait that runs out, or a setting the lock refuses, throws here, so the run
/// fails with that one message and no test body runs. The children the run starts inherit the hold through
/// <c>BOSUN_DESKTOP_HELD</c>, so the capture <see cref="CaptureTests"/> runs doesn't wait on its own parent.
/// </summary>
public sealed class DesktopFixture : IDisposable
{
    private readonly DesktopHold? _hold;
    private readonly string? _inherited;

    public DesktopFixture()
    {
        Settings = DesktopSettings.FromEnvironment();
        _inherited = Environment.GetEnvironmentVariable(DesktopLock.HeldVariable);
        // Run under a holder of the lock (scripts/smoke-input.ps1 holds it across every class, D02 T02 §12): its hold is
        // this run's, and waiting on it would wait on its own parent.
        if (DesktopLock.InheritsHold(Settings.Name, DesktopLock.RecordFolder()))
        {
            Inherited = true;
            return;
        }
        _hold = Hold(Settings.Name, DesktopLock.RecordFolder(), Settings.Wait);
        Environment.SetEnvironmentVariable(DesktopLock.HeldVariable, _hold.Holder.HeldValue);
    }

    /// <summary>Whether this run works under a parent's hold of the lock rather than one of its own.</summary>
    public bool Inherited { get; }

    /// <summary>
    /// The run's hold, refused when its record couldn't be written: the captures the smoke runs take its hold only
    /// from a record naming it, and without one they would wait on their own parent until their bound, failing far
    /// from the cause.
    /// </summary>
    internal static DesktopHold Hold(string name, string folder, TimeSpan wait)
    {
        var hold = new DesktopHold(name, folder, wait, line => Console.Error.WriteLine("smoke: " + line), label: "Bosun.Panel.E2E smoke");
        if (hold.Holder.Published) return hold;
        var path = hold.Holder.RecordPath;
        hold.Dispose();
        throw new DesktopLockException($"the desktop lock was free, but its record {path} couldn't be written, and the smoke's captures need it to work under the run's hold");
    }

    public DesktopSettings Settings { get; }

    /// <summary>The run's own hold; none when it works under a parent's (<see cref="Inherited"/>).</summary>
    public DesktopHolder? Holder => _hold?.Holder;

    public void Dispose()
    {
        if (_hold is null) return;
        Environment.SetEnvironmentVariable(DesktopLock.HeldVariable, _inherited);
        _hold.Dispose();
    }
}

public class TestAssemblyTests(DesktopFixture desktop)
{
    [Fact]
    public void The_smoke_runs_one_test_at_a_time()
    {
        var parallel = Assert.Single(typeof(TestAssemblyTests).Assembly.GetCustomAttributes(typeof(Xunit.v3.ParallelizationAttribute), false));
        Assert.Equal(Xunit.Sdk.ParallelMode.None, ((Xunit.v3.ParallelizationAttribute)parallel).GetMode());
    }

    [Fact]
    public void The_run_holds_the_desktop_lock_and_names_itself()
    {
        // Another process finds the lock taken, and the record names this test process with its start time.
        Assert.Equal("taken", DesktopLockTests.Probe(desktop.Settings.Name));
        using var me = Process.GetCurrentProcess();
        var record = DesktopLockTests.Record(DesktopLock.RecordPath(desktop.Settings.Name, DesktopLock.RecordFolder()));
        if (desktop.Inherited)
        {
            // Under the real-input runner's hold (D02 T02 §12): the record names the runner, as the hold handed down does.
            Assert.Equal(DesktopLock.HeldValue(desktop.Settings.Name, record.Pid, record.ProcessStart), Environment.GetEnvironmentVariable(DesktopLock.HeldVariable));
            Assert.NotEqual(me.Id, record.Pid);
            return;
        }
        Assert.Equal(me.Id, record.Pid);
        Assert.Equal((ulong)me.StartTime.ToFileTimeUtc(), record.ProcessStart);
        Assert.Equal(DesktopLock.HeldValue(desktop.Settings.Name, me.Id, record.ProcessStart), Environment.GetEnvironmentVariable(DesktopLock.HeldVariable));
    }

    [Fact]
    public void The_README_names_the_record_path_the_run_writes()
    {
        var readme = File.ReadAllText(Path.Combine(KeyboardTests.Repo(), "tests", "Bosun.Panel.E2E", "README.md"));
        const string documented = @"%LOCALAPPDATA%\Bosun\run\<session>\desktop\bosun-desktop.json";
        Assert.Contains("`" + documented + "`", readme);
        using var me = Process.GetCurrentProcess();
        var expanded = documented.Replace("%LOCALAPPDATA%", Environment.GetEnvironmentVariable("LOCALAPPDATA")).Replace("<session>", me.SessionId.ToString());
        Assert.Equal(expanded, DesktopLock.RecordPath(DesktopLock.DefaultName, DesktopLock.RecordFolder()));
        if (desktop.Settings.Name == DesktopLock.DefaultName && desktop.Holder is { } holder) Assert.Equal(expanded, holder.RecordPath);
    }
}

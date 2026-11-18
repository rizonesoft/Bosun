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

namespace Bosun.Panel.E2E;

/// <summary>
/// The run's hold on the desktop lock. A wait that runs out, or a setting the lock refuses, throws here, so the run
/// fails with that one message and no test body runs. The children the run starts inherit the hold through
/// <c>BOSUN_DESKTOP_HELD</c>, so the capture <see cref="CaptureTests"/> runs doesn't wait on its own parent.
/// </summary>
public sealed class DesktopFixture : IDisposable
{
    private readonly DesktopHold _hold;
    private readonly string? _inherited;

    public DesktopFixture()
    {
        Settings = DesktopSettings.FromEnvironment();
        _hold = new DesktopHold(Settings.Name, DesktopLock.RecordFolder(), Settings.Wait, line => Console.Error.WriteLine("smoke: " + line));
        _inherited = Environment.GetEnvironmentVariable(DesktopLock.HeldVariable);
        Environment.SetEnvironmentVariable(DesktopLock.HeldVariable, _hold.Holder.HeldValue);
    }

    public DesktopSettings Settings { get; }

    public DesktopHolder Holder => _hold.Holder;

    public void Dispose()
    {
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
        var record = DesktopLockTests.Record(desktop.Holder.RecordPath);
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
        if (desktop.Settings.Name == DesktopLock.DefaultName) Assert.Equal(expanded, desktop.Holder.RecordPath);
    }
}

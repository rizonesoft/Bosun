using Bosun.Core.Memory;
using Xunit;

namespace Bosun.Core.Tests.Memory;

/// <summary>
/// What every reader of a snapshot shares (D03 T01 §5): whether its figures can be, and whether the WSL VM runs by the
/// newer of the two fresh samples.
/// </summary>
public sealed class SnapshotJudgementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static SnapshotDoc Doc(string side, string at, long? available = 1000, long? total = 4000, bool? vmRunning = null) => new()
    {
        SchemaVersion = 1, Side = side, At = at, IntervalS = 30,
        Memory = new Dictionary<string, long?> { ["available_mb"] = available, ["total_mb"] = total },
        WslVm = vmRunning is { } running ? new WslVmDoc { Running = running, Processes = 1, PrivateMb = 100, RssMb = 100 } : null,
        Repos = [], Processes = [], HistoryWritten = true,
    };

    private static Reading<SnapshotDoc> Fresh(SnapshotDoc doc) => Reading<SnapshotDoc>.Of(doc, Now, TimeSpan.FromSeconds(5));

    private static Reading<SnapshotDoc> Stale(SnapshotDoc doc) => Reading<SnapshotDoc>.Stale(doc, Now, TimeSpan.FromMinutes(5), "old");

    [Theory]
    [InlineData(1000L, 4000L, MemoryFigure.Valid)]
    [InlineData(4000L, 4000L, MemoryFigure.Valid)]
    [InlineData(4001L, 4000L, MemoryFigure.Impossible)]
    [InlineData(-1L, 4000L, MemoryFigure.NotMeasured)]
    [InlineData(1000L, 0L, MemoryFigure.NotMeasured)]
    [InlineData(null, 4000L, MemoryFigure.NotMeasured)]
    [InlineData(1000L, null, MemoryFigure.NotMeasured)]
    public void Figures_are_valid_not_measured_or_impossible(long? available, long? total, MemoryFigure expected)
    {
        Assert.Equal(expected, Doc("windows", "2026-11-18T09:00:00Z", available, total).Figures(out _, out _));
    }

    [Fact]
    public void The_VM_runs_by_the_newer_of_two_fresh_samples_and_a_stale_one_says_nothing()
    {
        var stoppedEarly = Fresh(Doc("windows", "2026-11-18T08:59:40Z", vmRunning: false));
        var stoppedLate = Fresh(Doc("windows", "2026-11-18T08:59:55Z", vmRunning: false));
        var guest = Fresh(Doc("wsl", "2026-11-18T08:59:50Z"));
        Assert.False(WslVmState.Running(stoppedLate, guest));                        // shut down after the guest wrote
        Assert.True(WslVmState.Running(stoppedEarly, guest));                        // restarted since Windows saw it stopped
        Assert.False(WslVmState.Running(Fresh(Doc("windows", "2026-11-18T08:59:50Z", vmRunning: false)), guest)); // the same second: Windows' own sight of the VM
        Assert.True(WslVmState.Running(Stale(Doc("windows", "2026-11-18T08:55:00Z", vmRunning: false)), guest));
        Assert.False(WslVmState.Running(stoppedLate, Stale(Doc("wsl", "2026-11-18T08:50:00Z"))));
        Assert.True(WslVmState.Running(Fresh(Doc("windows", "2026-11-18T09:00:00Z", vmRunning: true)), null));
        Assert.Null(WslVmState.Running(Stale(Doc("windows", "2026-11-18T08:55:00Z", vmRunning: false)), Stale(Doc("wsl", "2026-11-18T08:50:00Z"))));
        Assert.Null(WslVmState.Running(null, null));
    }
}

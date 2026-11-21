using Bosun.Core;
using Bosun.Core.Instances;
using Bosun.Core.Memory;

namespace Bosun.Panel.Tests.Instances;

/// <summary>Instances as the scanners write them, for the page's tests (D03 T02 §2).</summary>
internal static class InstanceFixtures
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    internal static InstanceDoc I(string kind, long pid, string side = "windows", string? cwd = null, string? repo = null, long? mb = 1024,
        string? started = "2026-11-16T08:00:00Z", string state = "working", string? evidence = null, int? slot = null, string? command = null,
        long? guestPid = null, string? guestDistro = null, string? name = null) => new()
    {
        Side = side, Kind = kind, Name = name ?? kind, Pid = pid, Cwd = cwd, Repo = repo, PrivateMb = mb, StartedAt = started, State = state,
        Evidence = evidence, Slot = slot, Command = command, GuestPid = guestPid, GuestDistro = guestDistro,
    };

    /// <summary>One open window's root of an editor (D03 T02 §4): its window identity, where it is, and the editor's shared memory.</summary>
    internal static InstanceDoc EditorWindow(long pid, string window, string? cwd, string? repo, long? mb, long shared = 4096, string location = "windows", string? distro = null) =>
        I("code", pid, cwd: cwd, repo: repo, mb: mb, state: "unknown") with { Window = window, PlacedBy = "window", Location = location, Distro = distro, SharedMb = shared };

    /// <summary>The drives a current WSL logger reports (D03 T02 §6): C: and D: at WSL's default mounts.</summary>
    internal static readonly IReadOnlyList<DriveMount> DefaultDrives = [new() { Mount = "/mnt/c", Windows = "C:\\" }, new() { Mount = "/mnt/d", Windows = "D:\\" }];

    /// <param name="olderLogger">A WSL report from a logger older than drive mounts (no <c>drives</c>).</param>
    internal static InstancesDoc Doc(string side, string? distro, IReadOnlyList<InstanceDoc> instances, IReadOnlyList<SourceProblemDoc>? problems = null,
        long unreadable = 0, bool olderLogger = false) => new()
    {
        SchemaVersion = 1, Side = side, Distro = distro, At = "2026-11-16T09:00:00Z", Instances = instances, Unreadable = unreadable, Problems = problems ?? [],
        Drives = side == "wsl" && !olderLogger && !(problems ?? []).Any(p => p.Source == "automount") ? DefaultDrives : null,
    };

    internal static SideReading Win(params InstanceDoc[] instances) => new("windows", null, Reading<InstancesDoc>.Of(Doc("windows", null, instances), Now));

    internal static SideReading Wsl(string distro, params InstanceDoc[] instances) => new("wsl", distro, Reading<InstancesDoc>.Of(Doc("wsl", distro, instances), Now));

    internal static SideReading StaleWsl(string distro, TimeSpan age, params InstanceDoc[] instances) => new("wsl", distro,
        Reading<InstancesDoc>.Stale(Doc("wsl", distro, instances), Now, age, $"WSL ({distro}) last reported {MemoryClient.Words(age)} ago: it is stopped, or its logger is."));

    internal static SideReading Failed(string side, string? distro, string why) => new(side, distro, Reading<InstancesDoc>.Fail(ReadingProblem.Failed, why));

    internal static InstancesView View(string? notice, params SideReading[] sides) => new(InstanceClient.Merge(sides), sides, notice);

    /// <summary>Both sides: an agent and an editor in D:/src/Bosun, a WSL agent in that same folder through /mnt/d, a job holding slot 1, and one outside a repo.</summary>
    internal static InstancesView Both() => View(null,
        Win(
            I("claude", 100, cwd: @"D:\src\Bosun", repo: "D:/src/Bosun", mb: 2048, started: "2026-11-16T06:48:00Z", state: "working", evidence: "Claude Code says busy (session record)"),
            I("code", 101, cwd: @"D:\src\Bosun", repo: "D:/src/Bosun", mb: 10240, started: "2026-11-16T08:55:00Z", state: "unknown"),
            I("codex", 102, cwd: @"C:\Users\me", repo: null, mb: null, started: null, state: "idle"),
            I("job", 9200, side: "wsl", cwd: null, repo: null, mb: 300, started: "2026-11-16T08:58:00Z", state: "working", slot: 1,
                evidence: "holds slot 1, its command running since 2026-11-16T08:58:00Z", command: "make test", guestPid: 4242, guestDistro: "Ubuntu")),
        Wsl("Ubuntu",
            I("claude", 300, side: "wsl", cwd: "/mnt/d/src/Bosun", repo: "/mnt/d/src/Bosun", mb: 512, started: "2026-11-16T08:30:00Z", state: "reviewing", evidence: "runs under astra's review (dispatcher pid 77)"),
            I("job", 4242, side: "wsl", cwd: "/home/me/app", repo: "/home/me/app", mb: 900, started: "2026-11-16T08:58:00Z", state: "unknown", command: "make test", name: "bosun")));
}

/// <summary>A source the test sets: the view it answers, which it may hold, and how often it was asked.</summary>
internal sealed class FakeInstances : IInstanceSource
{
    public InstancesView Answer = InstanceFixtures.Both();
    public TaskCompletionSource? Hold;
    public int Reads;

    public async Task<InstancesView> ReadAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        var answer = Answer;
        if (Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);
        return answer;
    }
}

/// <summary>A launcher that keeps what it was asked to start, or throws.</summary>
internal sealed class FakeLauncher : Bosun.Panel.Instances.ILauncher
{
    public readonly List<System.Diagnostics.ProcessStartInfo> Started = [];
    public Exception? Throw;

    public void Start(System.Diagnostics.ProcessStartInfo start)
    {
        if (Throw is { } thrown) throw thrown;
        Started.Add(start);
    }
}

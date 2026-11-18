using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Core.Memory;
using Bosun.Panel.Overview;
using Bosun.Panel.Queue;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Instances;
using Bosun.Panel.Tests.Queue;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Settings;
using Bosun.Panel.Tests.Shell;
using Bosun.Panel.Tray;
using Bosun.Panel.Tests.Tray;

namespace Bosun.Panel.Tests.Overview;

/// <summary>A memory source the Overview's tests set per side: each side's reading, either of which it may hold, and the reads of each.</summary>
internal sealed class FakeMemory : IMemorySource
{
    public Reading<SnapshotDoc> Windows = Reading<SnapshotDoc>.Of(OverviewFixtures.WindowsSnapshot(), OverviewFixtures.Now, TimeSpan.FromSeconds(5));
    public Reading<SnapshotDoc> Guest = Reading<SnapshotDoc>.Of(OverviewFixtures.GuestSnapshot(), OverviewFixtures.Now, TimeSpan.FromSeconds(5));
    public bool NeverAnswers;

    /// <summary>A side's read held until the test sets it: its answer is taken as the read starts, as a held file read would.</summary>
    public TaskCompletionSource? HoldWindows, HoldGuest;

    public int WindowsReads, GuestReads, Reports;

    public async Task<Reading<SnapshotDoc>> SnapshotAsync(string side, CancellationToken cancellationToken = default)
    {
        if (side == "windows") WindowsReads++;
        else GuestReads++;
        var answer = side == "windows" ? Windows : Guest;
        if (NeverAnswers) await Task.Delay(Timeout.Infinite, cancellationToken);
        if ((side == "windows" ? HoldWindows : HoldGuest) is { } hold) await hold.Task;
        return answer;
    }

    public Task<Reading<ReportDoc>> ReportAsync(int days, long? lowMb = null, CancellationToken cancellationToken = default)
    {
        Reports++; // the Overview reads no report: the Memory page does
        return Task.FromResult(Reading<ReportDoc>.Fail(ReadingProblem.Failed, "not read by the Overview"));
    }
}

/// <summary>Snapshots for the Overview's cards: Windows and the guest with figures of their own, so neither can stand in for the other.</summary>
internal static class OverviewFixtures
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    internal static SnapshotDoc WindowsSnapshot(long? available = 20480, long? total = 32768, bool vmRunning = true, string at = "2026-11-18T09:00:00Z") =>
        JsonSerializer.Deserialize($$"""{"schema_version":1,"side":"windows","at":"{{at}}","interval_s":30,"memory":{"available_mb":{{N(available)}},"total_mb":{{N(total)}}},"wsl_vm":{"running":{{(vmRunning ? "true" : "false")}},"processes":1,"private_mb":12083,"rss_mb":9000},"repos":[],"processes":[],"history_written":true}""",
            MemoryJson.Default.SnapshotDoc)!;

    internal static SnapshotDoc GuestSnapshot(long? available = 6144, long? total = 14336, string at = "2026-11-18T09:00:00Z") =>
        JsonSerializer.Deserialize($$"""{"schema_version":1,"side":"wsl","at":"{{at}}","interval_s":30,"memory":{"available_mb":{{N(available)}},"total_mb":{{N(total)}}},"wsl_vm":null,"repos":[],"processes":[],"history_written":true}""",
            MemoryJson.Default.SnapshotDoc)!;

    private static string N(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    internal static Reading<SnapshotDoc> Fresh(SnapshotDoc doc) => Reading<SnapshotDoc>.Of(doc, Now, TimeSpan.FromSeconds(5));

    internal static Reading<SnapshotDoc> Stale(SnapshotDoc doc, TimeSpan age, string side) =>
        Reading<SnapshotDoc>.Stale(doc, Now, age, $"The {side} logger last wrote {MemoryClient.Words(age)} ago.");

    internal static Reading<SnapshotDoc> Missing(string side) =>
        Reading<SnapshotDoc>.Fail(ReadingProblem.Missing, $@"C:\Users\me\AppData\Local\Bosun\logs\current-{side}.json is not there: its logger has not written it.");
}

/// <summary>
/// The panel's own shell as <see cref="WindowStart.NewShell"/> makes it, over fake sources and settings that are only
/// memory, on a scheduler whose clock the test moves: the Overview, Memory, Instances and Queue pages all built, and
/// the Overview shown first. With <see cref="WithTray"/>, the real tray over a fake icon, fed by the Overview's Needs you.
/// </summary>
internal sealed class OverviewRig
{
    private DateTime _clock = OverviewFixtures.Now.UtcDateTime;

    public FakeMemory Memory { get; }
    public FakeQueueSource Queue { get; }
    public FakeInstances Instances { get; }
    public RefreshScheduler Scheduler { get; }
    public PanelSettingsStore Store { get; } = new(SettingsFixture.FilePath, new MemoryFiles());
    public ShellViewModel Shell { get; }
    public OverviewPage Page { get; }
    public FakeTrayIcon Icon { get; } = new();
    public TrayController? Tray { get; private set; }
    public PressureCheck? Check { get; private set; }

    public OverviewRig(FakeMemory? memory = null, FakeQueueSource? queue = null, FakeInstances? instances = null, bool windowShown = true)
    {
        Memory = memory ?? new FakeMemory();
        Queue = queue ?? new FakeQueueSource();
        Instances = instances ?? new FakeInstances();
        Scheduler = new RefreshScheduler(() => _clock, new QueuePageTests.Driver());
        Shell = WindowStart.NewShell(Dispatcher.CurrentDispatcher, Store, new StartupEntry(new FakeRunKey(), SettingsFixture.Program), () => { },
            windowShown: windowShown, refreshes: Scheduler, memory: Memory, instances: Instances, queue: Queue);
        Page = (OverviewPage)Section("overview").Page;
        Settle();
    }

    public Section Section(string key) => Shell.Sections.Single(s => s.Key == key);

    public PageBase PageOf(string key) => Section(key).Page;

    /// <summary>The clock moves on, and what is due runs.</summary>
    public void Advance(TimeSpan by)
    {
        _clock += by;
        Scheduler.Tick();
        Settle();
    }

    public static void Settle() => QueuePageTests.Settle();

    public static T Wait<T>(Task<T> task) => QueuePageTests.Wait(task);

    public static void Wait(Task task) => QueuePageTests.Wait(task);

    public void GoTo(string key)
    {
        Wait(Shell.GoToAsync(Section(key)));
        Settle();
    }

    /// <summary>
    /// The tray as the application starts it (App.xaml.cs): over this shell, its Needs you state from the Overview's
    /// Needs you, and its pressure check feeding the pressure rows, reading what <paramref name="read"/> gives.
    /// </summary>
    public OverviewRig WithTray(Func<Reading<SnapshotDoc>> read)
    {
        var notifier = new Notifier(Store, Shell.Log, () => OverviewFixtures.Now.LocalDateTime);
        Tray = new TrayController(Shell, Icon, notifier, Store, _ => { }, needs: Page.Needs, refreshes: Scheduler);
        Tray.Start(new FakeWindow(), inTray: false);
        Check = Tray.StartPressureCheck(_ => Task.FromResult(read()), check => Page.Needs.Providers.OfType<PressureNeeds>().Single().Watch(check));
        Settle();
        return this;
    }

    /// <summary>A row of another page's own in Needs you, as a provider beside the pressure check gives one.</summary>
    public OverviewRig WithRow()
    {
        var provider = new FakeProvider();
        Page.Needs.Add(provider);
        provider.Say(FakeProvider.Row("stuck", what: "A build job has waited 2 hours for a slot"));
        Settle();
        return this;
    }

    /// <summary>One run of the real pressure check.</summary>
    public void RunCheck()
    {
        Wait(Check!.RunAsync(CancellationToken.None));
        Settle();
    }

    /// <summary>The rail badge's count and spoken words for a page, as the shell's view gives them.</summary>
    public (int Count, string Said) Badge(string key)
    {
        var section = Section(key);
        return (section.Page.Badge, RailRowName.Of(section.Title, section.Page.Waiting, section.Page.NeedsYou));
    }

    /// <summary>
    /// The shell on the Overview for a render: made by <paramref name="make"/> on the drawing thread, and ready once it
    /// has read (or, held loading, once the skeleton's delay has passed).
    /// </summary>
    public static (System.Windows.FrameworkElement View, Func<System.Windows.FrameworkElement, Task> Ready) Rendered(Func<OverviewRig> make, bool loading = false)
    {
        var rig = make();
        return (new ShellView { DataContext = rig.Shell }, async _ =>
        {
            if (loading) await Task.Delay(Bosun.Panel.Controls.Skeleton.DefaultDelay + TimeSpan.FromMilliseconds(150));
            for (var i = 0; i < 50 && !loading && rig.Page.State == PageState.Loading; i++) await Dispatcher.Yield(DispatcherPriority.Background);
            for (var i = 0; i < 5; i++) await Dispatcher.Yield(DispatcherPriority.Background);
        });
    }

    /// <summary>Runs a test on an STA thread with this thread's dispatcher, in a page of the theme so templates resolve.</summary>
    public static void On(Action test) => PageRenderer.InPage("Dark", () => new Grid(), _ =>
    {
        test();
        return 0;
    });
}

/// <summary>A provider the tests set: its rows and whether it has looked.</summary>
internal sealed class FakeProvider : INeedsYouProvider
{
    private IReadOnlyList<NeedsYouRow> _rows = [];
    private bool _looked = true;

    public IReadOnlyList<NeedsYouRow> Rows => _rows;

    public bool HasLooked => _looked;

    public event Action? Changed;

    public void Say(params NeedsYouRow[] rows)
    {
        _rows = rows;
        Changed?.Invoke();
    }

    public void Looked(bool looked)
    {
        _looked = looked;
        Changed?.Invoke();
    }

    public static NeedsYouRow Row(string key, string section = "queue", NeedsSeverity severity = NeedsSeverity.Notice, int minute = 0, string? what = null) =>
        new(key, "Stuck", what ?? $"Row {key}", section == "queue" ? "Queue" : section, section, severity, OverviewFixtures.Now.AddMinutes(minute));
}

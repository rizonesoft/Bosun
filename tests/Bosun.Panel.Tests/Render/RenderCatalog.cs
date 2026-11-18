using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Bosun.Panel.Tests.Render;

/// <summary>
/// Every view a reviewer can look at, one row per view and state (D02 T02 §9). A surface section adds its views here
/// as it lands; <see cref="WriteAll"/> draws each row in both themes into <c>build/renders/</c>.
/// </summary>
public static partial class RenderCatalog
{
    public static readonly IReadOnlyList<RenderCase> Cases =
    [
        new("sample", "normal", () => new SampleView(new SampleData("Queue", ["build Bosun", "test Recto", "build Steward"], null))) { Shows = "test Recto" },
        new("sample", "empty", () => new SampleView(new SampleData("Queue", [], "Nothing here yet"))) { Shows = "Nothing here yet" },
        // The control styles of Theme/Controls.xaml (D02 T01 §4), each in its states, and a table.
        new("controls", "gallery", ControlsGallery.Gallery) { Shows = "Stop job" },
        new("controls", "table", ControlsGallery.Table) { Shows = "Memory (MB)" },
        // The shell (D02 T02 §4): the whole frame as the panel starts, a page in each of its four states, and the
        // question asked when a page with unsaved changes is being left.
        new("shell", "overview", () => new Bosun.Panel.Shell.ShellView { DataContext = Shell.Shells.Panel() }) { Shows = "Panel ready" },
        // The backdrop (D02 T03 §2), on a clear host: Mica leaves the title bar and rail clear for Windows' backdrop,
        // the page's sheet still solid. Solid is the shell as it starts, drawn above as shell.overview.
        new("shell", "backdrop-mica", () => Backdropped(Bosun.Panel.Shell.BackdropKind.Mica)) { Shows = "Panel ready", ClearHost = true },
        // The status pill in each kind, and the segmented control with a choice and a disabled segment (D02 T03 §4).
        new("controls", "status-pills", StatusPills) { Shows = "Not available" },
        new("controls", "segmented", Segmented) { Shows = "Today" },
        // The chosen segment with the keyboard on it: the inside focus ring over it, where WPF's adorner layer draws it.
        new("controls", "segmented-focus", SegmentedFocus) { Shows = "Dark", Ready = PlaceRing },
        // D02 T04 §1: every keyboard target with its own focus visual, one ring each.
        new("controls", "focus", Focused) { Shows = "primary button", Ready = ShowFocus },
        // D02 T04 §2: a field in each of its states, its text at the combo box's inset, its error on its border.
        new("controls", "field-states", FieldStates) { Shows = "invalid and focused", Ready = ShowFieldFocus },
        // D02 T03 §5: the gauge in each state and with no value, sparklines with their words, stacked bars.
        new("controls", "gauges", Gauges) { Shows = "Not available" },
        new("controls", "sparklines", Sparklines) { Shows = "12.4 GB, rising" },
        new("controls", "stacked-bars", StackedBars) { Shows = "Windows 12.8 GB" },
        // D02 T03 §6: a tile in each slot state, and the waiting jobs with their reasons and what can't be read.
        new("controls", "queue-slots", QueueSlots) { Shows = "Waiting for guest memory" },
        new("controls", "queue-waiting", QueueWaiting) { Shows = "Waiting for a free slot" },
        // Count badges (D02 T03 §3): the selected row with a two-figure count, two others with one figure each.
        new("shell", "badges", Badged) { Shows = "12" },
        // D02 T03 §8: the command palette over the shell, with results and its selection, a row that can't run with its
        // reason (the Settings page's Save while nothing is edited), and nothing matching.
        new("shell", "palette", () => Paletted("s")) { Shows = "There are no changes to save" },
        new("shell", "palette-nothing", () => Paletted("zzz")) { Shows = "Nothing matches" },
        // D02 T03 §9: a done toast and a long failure cut at three lines; then three failures with two more counted.
        new("shell", "toasts", () => Toasted(false)) { Shows = "Save settings" },
        new("shell", "toasts-full", () => Toasted(true)) { Shows = "2 more in the Activity pane" },
        // D02 T04 §7: a long result opened in full, and three opened at the least window size with every Dismiss on it.
        new("shell", "toasts-expanded", () => Expanded(1)) { Shows = "Show less" },
        new("shell", "toasts-expanded-least", () => Expanded(3)) { Shows = "Show less", Width = 960, Height = 600 },
        new("page", "loading", () => Loading(new Shell.TestPage())) { Shows = "Loading Queue" },
        // The loading state's three shapes (D02 T03 §7, design/SPEC.md §19), as each page declares its own.
        new("skeleton", "card", () => Loading(new Shell.TestPage("Overview", "Memory, the queue and dev instances at a glance."))) { Shows = "Loading Overview" },
        new("skeleton", "table", () => Loading(new Shell.TestPage("Instances", "Every dev instance on both sides.") { LoadingShape = Bosun.Panel.Controls.SkeletonShape.Table })) { Shows = "Loading Instances" },
        new("skeleton", "gauge", () => Loading(new Shell.TestPage("Memory", "Live headroom.") { LoadingShape = Bosun.Panel.Controls.SkeletonShape.Gauge })) { Shows = "Loading Memory" },
        // Data no longer current, with how old it is (a page reading WSL, with WSL stopped), and values that can't be read.
        new("page", "stale", Stale) { Shows = "WSL is stopped. Showing what was read 12 minutes ago." },
        new("value", "not-available", NotAvailableValues) { Shows = "It could not be read." },
        // What a contrast theme changes beyond colour (D02 T03 §10): the focus ring, a drop-down's highlighted and
        // selected items, and the Settings page saying why its theme choice waits.
        new("controls", "states", States) { Shows = "Highlighted" },
        new("settings", "contrast", SettingsInContrast) { Shows = "Windows' contrast theme is in use" },
        // The pressure alert threshold (D03 T01 §4): a value that can't be one, refused beside the field.
        new("settings", "threshold-error", () => Settings(_ => { }, f => f.Page.PressureFloorText = "lots")) { Shows = "Enter a whole number of MB" },
        new("page", "data", () => Framed(new Shell.TestPage().Data())) { Shows = "two slots, one waiting" },
        new("page", "empty", () => Framed(new Shell.TestPage().Empty())) { Shows = "Nothing is waiting." },
        new("page", "failed", () => Framed(new Shell.TestPage().Failed())) { Shows = "bosun: the queue file is damaged at line 3" },
        // A page that has a refresh: the Refresh button in its header (D02 T02 §8).
        new("page", "refresh", () => Framed(new Shell.TestPage().Data().Polling(new Bosun.Core.RefreshScheduler(), Shell.Shells.Log()))) { Shows = "Refresh" },
        new("dialog", "leave", LeaveQuestion) { Shows = "Save your changes to Queue?" },
        // The Settings page (D02 T02 §5): as it opens, after a damaged file was moved aside, when the file can't be
        // read at all, and after a save that only half took.
        new("settings", "general", () => Settings(_ => { })) { Shows = "Start with Windows, in the tray" },
        // D03 T01 §2: the Memory page with live data, and with WSL and the report unreadable and a counter unmeasured.
        MemoryCase("data", new Memory.MemoryPageTests.Source { Report = HistoryReport() }, "32.0 GB has been enough so far.", tall: true),  // its Decision card too
        // D03 T01 §3: the history, a gap in it breaking the line; its table; no history; a history that can't be read.
        MemoryCase("history-table", new Memory.MemoryPageTests.Source { Report = HistoryReport() }, "Lowest available", page => page.ShowAsTable.Execute(null)),
        MemoryCase("history-none", new Memory.MemoryPageTests.Source(), "No history in this range yet"),
        // D02 T04 §5: the week and the fortnight, their dated ticks, at the panel's width and at its narrowest.
        MemoryCase("history-week", new Memory.MemoryPageTests.Source { Report = HistoryReport(), Fortnight = HistoryReport() }, "7 days",
            page => page.Range = Bosun.Panel.Memory.HistoryRange.Week),
        MemoryCase("history-fortnight", new Memory.MemoryPageTests.Source { Report = HistoryReport(), Fortnight = HistoryReport() }, "14 days",
            page => page.Range = Bosun.Panel.Memory.HistoryRange.Fortnight),
        MemoryCase("history-failed", new Memory.MemoryPageTests.Source
        {
            Report = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Fail(Bosun.Core.Memory.ReadingProblem.Failed, "bosun report ran past 60 s."),
        }, "The history could not be read"),
        // D03 T01 §6: the Decision card's verdicts (enough is in "data"), too few days, and a fortnight that can't be read.
        MemoryCase("decision-more", new Memory.MemoryPageTests.Source
        {
            Report = HistoryReport(),
            Fortnight = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Of(Memory.MemoryPageTests.ReportOf(
                evidence: Memory.MemoryPageTests.Evidence.Replace("\"low_minutes\":0", "\"low_minutes\":42").Replace("\"peak_commit_ratio\":0.6", "\"peak_commit_ratio\":0.93")), Memory.MemoryPageTests.Now),
        }, "More memory would help", tall: true),
        MemoryCase("decision-not-yet", new Memory.MemoryPageTests.Source
        {
            Report = HistoryReport(),
            Fortnight = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Of(Memory.MemoryPageTests.ReportOf(
                evidence: Memory.MemoryPageTests.Evidence.Replace("\"days_with_samples\":14", "\"days_with_samples\":3")), Memory.MemoryPageTests.Now),
        }, "Not yet: 3 of the 7 days", tall: true),
        MemoryCase("decision-failed", new Memory.MemoryPageTests.Source
        {
            Report = HistoryReport(),
            Fortnight = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Fail(Bosun.Core.Memory.ReadingProblem.Failed, "bosun report ran past 60 s."),
        }, "could not be read for the decision", tall: true),
        // The card's other states (the completion review): reading, withheld for rows rejected, no Windows samples yet,
        // and a later failure keeping the verdict with its age.
        MemoryCase("decision-reading", new Memory.MemoryPageTests.Source { Report = HistoryReport(), HoldFortnight = new TaskCompletionSource() },
            Bosun.Panel.Memory.MemoryPage.ReadingDecision, tall: true),
        MemoryCase("decision-withheld", new Memory.MemoryPageTests.Source
        {
            Report = HistoryReport(),
            Fortnight = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Of(Memory.MemoryPageTests.ReportOf(complete: false), Memory.MemoryPageTests.Now),
        }, "1 file could not be read and 7 rows were rejected", tall: true),
        MemoryCase("decision-no-samples", new Memory.MemoryPageTests.Source
        {
            Report = HistoryReport(),
            Fortnight = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Of(Memory.MemoryPageTests.ReportOf(
                evidence: Memory.MemoryPageTests.Evidence.Replace("\"days_with_samples\":14", "\"days_with_samples\":0")), Memory.MemoryPageTests.Now),
        }, "Not yet: 0 of the 7 days", tall: true),
        KeptDecision(),
        MemoryCase("unreadable", new Memory.MemoryPageTests.Source
        {
            Windows = Bosun.Core.Memory.Reading<Bosun.Core.Memory.SnapshotDoc>.Of(Memory.MemoryPageTests.Snapshot(memory: "{\"available_mb\":null,\"total_mb\":32768,\"commit_mb\":null}"), Memory.MemoryPageTests.Now),
            Wsl = Bosun.Core.Memory.Reading<Bosun.Core.Memory.SnapshotDoc>.Fail(Bosun.Core.Memory.ReadingProblem.Failed, "The WSL logger's file could not be read."),
            Report = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Fail(Bosun.Core.Memory.ReadingProblem.Failed, "bosun report ran past 60 s."),
        }, "Peaks not available"),
        // The page's own states (completion review): loading (the gauge skeleton), an empty table, and Windows unread.
        MemoryCase("loading", new Memory.MemoryPageTests.Source { HoldSnapshots = new TaskCompletionSource() }, "Memory"),
        MemoryCase("empty", new Memory.MemoryPageTests.Source
        {
            Windows = Bosun.Core.Memory.Reading<Bosun.Core.Memory.SnapshotDoc>.Of(Memory.MemoryPageTests.Snapshot(repos: " "), Memory.MemoryPageTests.Now),
            Wsl = Bosun.Core.Memory.Reading<Bosun.Core.Memory.SnapshotDoc>.Of(Memory.MemoryPageTests.WslSnapshot(" "), Memory.MemoryPageTests.Now),
            Report = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Of(Memory.MemoryPageTests.ReportOf(repos: " "), Memory.MemoryPageTests.Now),
        }, "No repo has processes running now"),
        MemoryCase("failed", new Memory.MemoryPageTests.Source
        {
            Windows = Bosun.Core.Memory.Reading<Bosun.Core.Memory.SnapshotDoc>.Fail(Bosun.Core.Memory.ReadingProblem.Missing, "The Windows logger has written nothing yet."),
        }, "The Windows logger has written nothing yet."),
        // D03 T02 §2: the Instances page with both sides and a job's slot; no distro reporting; a stale distro; a distro's
        // report that can't be read; Windows unread with WSL rows; nothing readable, with Retry; nothing running; loading.
        InstancesCase("data", new Instances.FakeInstances(), "Working, slot 1"),
        InstancesCase("wsl-notice", new Instances.FakeInstances
        {
            Answer = Instances.InstanceFixtures.View("No WSL distro has reported its instances to C:\\Users\\me\\AppData\\Local\\Bosun\\logs, so only Windows is listed.",
                Instances.InstanceFixtures.Win(Instances.InstanceFixtures.I("claude", 1, cwd: @"D:\Bosun", repo: "D:/Bosun"))),
        }, "No WSL distro has reported"),
        InstancesCase("stale", new Instances.FakeInstances
        {
            Answer = Instances.InstanceFixtures.View(null,
                Instances.InstanceFixtures.Win(Instances.InstanceFixtures.I("claude", 1, cwd: @"D:\Bosun", repo: "D:/Bosun")),
                Instances.InstanceFixtures.StaleWsl("Ubuntu", TimeSpan.FromMinutes(4), Instances.InstanceFixtures.I("claude", 2, side: "wsl", cwd: "/home/me/app", repo: "/home/me/app"))),
        }, "Last seen 4 minutes ago"),
        InstancesCase("unreadable-distro", new Instances.FakeInstances
        {
            Answer = Instances.InstanceFixtures.View(null,
                Instances.InstanceFixtures.Win(Instances.InstanceFixtures.I("claude", 1, cwd: @"D:\Bosun", repo: "D:/Bosun")),
                Instances.InstanceFixtures.Failed("wsl", "Ubuntu", "instances-wsl-Ubuntu.json is not an instances report: 'n' is an invalid start of a value.")),
        }, "WSL (Ubuntu): instances-wsl-Ubuntu.json is not an instances report"),
        InstancesCase("windows-failed", new Instances.FakeInstances
        {
            Answer = Instances.InstanceFixtures.View(null,
                Instances.InstanceFixtures.Failed("windows", null, "bosun instances --json ran past 20 s."),
                Instances.InstanceFixtures.Wsl("Ubuntu", Instances.InstanceFixtures.I("claude", 2, side: "wsl", cwd: "/home/me/app", repo: "/home/me/app"))),
        }, "Windows: bosun instances --json ran past 20 s."),
        InstancesCase("failed", new Instances.FakeInstances
        {
            Answer = Instances.InstanceFixtures.View(null, Instances.InstanceFixtures.Failed("windows", null,
                "bosun.exe is not installed at C:\\Users\\me\\AppData\\Local\\Bosun\\bin\\bosun.exe. Install it with scripts/install.ps1, or name it with BOSUN_EXE.")),
        }, "bosun.exe is not installed"),
        InstancesCase("empty", new Instances.FakeInstances
        {
            Answer = Instances.InstanceFixtures.View(null, Instances.InstanceFixtures.Win(), Instances.InstanceFixtures.Wsl("Ubuntu")),
        }, Bosun.Panel.Instances.InstancesPage.NothingRunning),
        InstancesCase("loading", new Instances.FakeInstances { Hold = new TaskCompletionSource() }, "Instances"),
        // With memory pressure notifications turned off (D02 T02 §7).
        new("settings", "notifications-off", () => Settings(f => f.Files.Put(Bosun.Panel.Tests.Settings.SettingsFixture.FilePath,
            "{\"schema_version\": 1, \"notify_memory_pressure\": false}"))) { Shows = "Notify me about memory pressure" },
        new("settings", "restored", () => Settings(f => f.Files.Put(Bosun.Panel.Tests.Settings.SettingsFixture.FilePath, "not json at all"))) { Shows = "panel.json.bad" },
        new("settings", "unavailable", () => Settings(f =>
        {
            f.Files.Put(Bosun.Panel.Tests.Settings.SettingsFixture.FilePath, "{}");
            f.Files.FailRead = new IOException("The process cannot access the file because it is being used by another process.");
        })) { Shows = "panel.json could not be read" },
        new("settings", "half-saved", () => Settings(_ => { }, f =>
        {
            f.Page.SelectedTheme = f.Option(Bosun.Core.ThemeChoice.Dark);
            f.Page.StartWithWindows = true;
            f.Key.FailWrite = new UnauthorizedAccessException("Access to the registry key is denied.");
            _ = f.Page.SaveAsync();
        })) { Shows = "Not saved: Start with Windows could not be turned on" },
    ];

    /// <summary>A page in the frame every page is drawn in, with the margin the shell gives it.</summary>
    private static FrameworkElement Framed(Shell.TestPage page)
    {
        var frame = new Bosun.Panel.Shell.PageFrame { DataContext = page, Margin = new Thickness(26, 22, 26, 12) };
        Shell.TestPage.AddTemplate(frame);
        return frame;
    }

    /// <summary>A page loading, on screen, in its frame, its skeleton drawn at once: a picture can't wait 150 ms.</summary>
    private static FrameworkElement Loading(Shell.TestPage page)
    {
        var frame = new Bosun.Panel.Shell.PageFrame { SkeletonDelay = TimeSpan.Zero, DataContext = page.Loading(), Margin = new Thickness(26, 22, 26, 12) };
        Shell.TestPage.AddTemplate(frame);
        page.Show();
        return frame;
    }

    /// <summary>A pill of each kind, as a page lays them out.</summary>
    private static FrameworkElement StatusPills()
    {
        var row = new StackPanel { Margin = new Thickness(26) };
        foreach (var (kind, word) in new[] { (Bosun.Panel.Controls.StatusKind.Ok, "Running"), (Bosun.Panel.Controls.StatusKind.Warn, "Stale"),
                     (Bosun.Panel.Controls.StatusKind.Danger, "Not available"), (Bosun.Panel.Controls.StatusKind.Info, "Waiting"), (Bosun.Panel.Controls.StatusKind.Neutral, "Idle") })
            row.Children.Add(new Bosun.Panel.Controls.StatusPill { Kind = kind, Word = word, Margin = new Thickness(0, 0, 0, 10) });
        return row;
    }

    /// <summary>A segmented control with its second choice made and its third disabled, and the Settings page's theme choice.</summary>
    private static FrameworkElement Segmented()
    {
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new Bosun.Panel.Controls.SegmentedControl
        {
            Items = { new ListBoxItem { Content = "Now" }, new ListBoxItem { Content = "Today" }, new ListBoxItem { Content = "Week", IsEnabled = false } },
            SelectedIndex = 1,
            Margin = new Thickness(0, 0, 0, 16),
        });
        panel.Children.Add(new Bosun.Panel.Controls.SegmentedControl { ItemsSource = new[] { "Follow Windows", "Dark", "Light" }, SelectedIndex = 0 });
        return panel;
    }

    /// <summary>Gauges of memory free in each state, a budget with no state, and one with nothing to read.</summary>
    private static FrameworkElement Gauges()
    {
        var row = new WrapPanel { Margin = new Thickness(26) };
        foreach (var (value, unit, word, state) in new (double?, string, string, Bosun.Panel.Controls.StatusKind)[]
                 {
                     (14.2, "GB free", "Safe", Bosun.Panel.Controls.StatusKind.Ok), (7.4, "GB free", "Tight", Bosun.Panel.Controls.StatusKind.Warn),
                     (1.6, "GB free", "Critical", Bosun.Panel.Controls.StatusKind.Danger), (4.12, "of $20 today", "", Bosun.Panel.Controls.StatusKind.Neutral),
                     (null, "GB free", "Safe", Bosun.Panel.Controls.StatusKind.Ok),
                 })
            row.Children.Add(new Bosun.Panel.Controls.RadialGauge { Value = value, Maximum = unit.StartsWith("of") ? 20 : 32, Unit = unit, Word = word, State = state, Margin = new Thickness(0, 0, 18, 18) });
        return row;
    }

    /// <summary>Sparklines rising, falling, with one sample and with none, each with what a screen reader hears beside it.</summary>
    private static FrameworkElement Sparklines()
    {
        var grid = new StackPanel { Margin = new Thickness(26), Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var samples in new double[][] { [9.8, 10.4, 10.1, 11.2, 11.9, 11.5, 12.4], [14.1, 13.2, 13.6, 12.0, 11.4, 11.0], [11.0], [] })
        {
            var spark = new Bosun.Panel.Controls.Sparkline { Samples = samples, Unit = "GB" };
            grid.Children.Add(new TextBlock { Text = spark.SpokenName, Margin = new Thickness(0, 8, 0, 0) });
            grid.Children.Add(spark);
        }
        return grid;
    }

    /// <summary>Stacked bars: memory by group, one share, all zero, and nothing to read.</summary>
    private static FrameworkElement StackedBars()
    {
        var panel = new StackPanel { Margin = new Thickness(26), Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var parts in new Bosun.Panel.Controls.BarPart[]?[]
                 {
                     [new("Windows", 12.8), new("WSL", 9.1), new("Codex", 2.7), new("Free", 7.4, Rest: true)], [new("Free", 32, Rest: true)],
                     [new("Windows", 0), new("WSL", 0)], null,
                 })
            panel.Children.Add(new Bosun.Panel.Controls.StackedBar { Parts = parts, Unit = "GB", Margin = new Thickness(0, 0, 0, 14) });
        return panel;
    }

    private static Bosun.Core.Memory.StatusDoc Queue(string slots, string waiters, bool readable = true, int skipped = 0) =>
        System.Text.Json.JsonSerializer.Deserialize("{\"schema_version\":1,\"ns\":\"Bosun\",\"at\":\"2026-11-16T15:07:49Z\",\"slots\":[" + slots
            + "],\"waiters\":[" + waiters + "],\"memory\":{\"available_mb\":4607,\"total_mb\":32601,\"min_avail_mb\":6144},\"guest\":null,"
            + "\"logger\":{\"windows\":\"running\",\"wsl\":null},\"records\":{\"readable\":" + (readable ? "true" : "false") + ",\"skipped\":" + skipped
            + ",\"dir\":\"C:\\\\run\"}}", Bosun.Core.Memory.MemoryJson.Default.StatusDoc)!;

    private static string QueueHolder(int slot, string phase, string side, string repo, string state = "live", string running = "240") =>
        "{\"index\":" + slot + ",\"busy\":true,\"holder\":{\"schema_version\":1,\"kind\":\"holder\",\"pid\":4120,\"process_start\":1,\"started_at\":\"2026-11-16T15:03:37Z\","
        + "\"command\":\"cmake --build build\",\"repo\":" + repo + ",\"side\":\"" + side + "\",\"slot\":" + slot + ",\"session\":1,\"ns\":\"Bosun\","
        + "\"acquired_at\":\"2026-11-16T15:03:49Z\",\"command_started_at\":null,\"min_guest_mb\":null,\"state\":\"" + state + "\",\"waited_s\":12,"
        + "\"running_s\":" + running + ",\"phase\":" + phase + "}}";

    private static string QueueWaiter(int pid, string why, string floor, string repo, string side, string state = "live") =>
        "{\"schema_version\":1,\"kind\":\"waiter\",\"pid\":" + pid + ",\"process_start\":1,\"started_at\":\"2026-11-16T15:04:37Z\",\"command\":\"dotnet test\","
        + "\"repo\":" + repo + ",\"side\":\"" + side + "\",\"session\":1,\"ns\":\"Bosun\",\"waiting_for\":" + why + ",\"min_avail_mb\":" + floor
        + ",\"max_wait_s\":3600,\"state\":\"" + state + "\",\"waited_s\":190}";

    /// <summary>A tile in each slot state: running, starting, waiting for guest memory, held with no phase, held with no record, free, unknown.</summary>
    private static FrameworkElement QueueSlots()
    {
        var panel = new WrapPanel { Margin = new Thickness(20), Width = 820, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var slot in new[]
                 {
                     QueueHolder(0, "\"running\"", "windows", "\"Recto\""), QueueHolder(1, "\"starting\"", "windows", "\"Office\"", running: "null"),
                     QueueHolder(0, "\"waiting for guest memory\"", "wsl", "\"Conclave\"", running: "null"),
                     QueueHolder(1, "null", "wsl", "null", state: "unknown", running: "null"),
                     "{\"index\":0,\"busy\":true,\"holder\":null}", "{\"index\":1,\"busy\":false,\"holder\":null}", "{\"index\":1,\"busy\":null,\"holder\":null}",
                 })
            panel.Children.Add(new Bosun.Panel.Controls.QueueSlotTile { Slot = Queue(slot, "").Slots[0], Width = 250, Margin = new Thickness(0, 0, 12, 12) });
        return panel;
    }

    /// <summary>The waiting jobs: held by memory with their own floors, by a slot, by a reason not recorded; then records not read.</summary>
    private static FrameworkElement QueueWaiting()
    {
        var panel = new StackPanel { Margin = new Thickness(20), Width = 560, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new Bosun.Panel.Controls.WaitingRow
        {
            Status = Queue("{\"index\":0,\"busy\":false,\"holder\":null}", string.Join(",",
                QueueWaiter(11, "\"memory\"", "8192", "\"Office\"", "wsl"), QueueWaiter(12, "\"slot\"", "null", "\"Recto\"", "windows"),
                QueueWaiter(13, "null", "null", "null", "wsl", "unknown")), skipped: 1),
            Margin = new Thickness(0, 0, 0, 18),
        });
        panel.Children.Add(new Bosun.Panel.Controls.WaitingRow { Status = Queue("{\"index\":0,\"busy\":false,\"holder\":null}", "", readable: false) });
        return panel;
    }

    /// <summary>The shell with toasts: a done one and a long failure, or three failures and two more only logged.</summary>
    private static FrameworkElement Toasted(bool full)
    {
        var shell = Shell.Shells.Panel();
        var sink = Bosun.Panel.Shell.LogSeverity.Fail;
        if (!full)
        {
            shell.Toasts.Show(Bosun.Panel.Shell.LogSeverity.Info, "Save settings");
            shell.Toasts.Show(sink, "Save queue limits: bosun config set slots 9: slots must be between 1 and 8, and the file was left as it was, "
                + "so the queue still runs 2 slots; change the value and save again, or read the Activity pane for what the tool said in full");
        }
        else
        {
            for (var i = 1; i <= 5; i++) shell.Toasts.Show(sink, $"Run the WSL logger: wsl.exe exited with code {i}");
        }
        return new Bosun.Panel.Shell.ShellView { DataContext = shell };
    }

    private static FrameworkElement Expanded(int count)
    {
        var shell = Shell.Shells.Panel();
        for (var i = 1; i <= count; i++)
        {
            shell.Toasts.Show(Bosun.Panel.Shell.LogSeverity.Fail, $"Run the WSL logger: wsl.exe exited with code {i}: " + string.Join(" ",
                Enumerable.Repeat("the distribution named in Settings did not start, so no reading was taken; start it from a WSL prompt, or pick another in Settings and save again.", 3)));
        }
        foreach (var toast in shell.Toasts.Shown.ToList()) shell.Toasts.Expand(toast, true);
        return new Bosun.Panel.Shell.ShellView { DataContext = shell };
    }

    /// <summary>A day's history for the renders: a slow fall to 1.5 GB in the afternoon, and two hours with no sample.</summary>
    internal static Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc> HistoryReport()
    {
        var points = Enumerable.Range(0, 24 * 60 / 5).Select(i => i * 5)
            .Where(m => m is < 600 or > 720)
            .Select(m => (m, (long?)(8000 + (long)(4000 * Math.Sin(m / 180.0)) - (m is > 200 and < 260 ? 6000 : 0)))).ToArray();
        return Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Of(Memory.MemoryPageTests.ReportOf(series: Memory.MemoryHistoryTests.Series(points)), Memory.MemoryPageTests.Now);
    }

    /// <summary>A verdict read, then a fortnight that fails: the card keeps the verdict and says how old it is.</summary>
    private static RenderCase KeptDecision()
    {
        // A source of its own each time it is drawn (once a theme): the failure it ends with must not be the next
        // theme's first reading.
        Func<FrameworkElement, Task>? ready = null;
        return new RenderCase("memory", "decision-kept", () =>
        {
            var source = new Memory.MemoryPageTests.Source { Report = HistoryReport() };
            var (view, isReady) = Memory.MemoryPageTests.Rendered(source, page =>
            {
                source.Fortnight = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Fail(Bosun.Core.Memory.ReadingProblem.Failed, "bosun report ran past 60 s.");
                page.RefreshNow!.Execute(null);
            });
            ready = isReady;
            return view;
        }) { Shows = "Showing the verdict from", Ready = async view => { await ready!(view); ShowDecision(view); }, Height = 1100 };
    }

    /// <summary>
    /// Scrolls the Memory page to its end, where the Decision card is, so a render whose subject is the verdict shows
    /// the card and every evidence row whole (D02 T04 §8 completion review: the list was cut off below the viewport).
    /// </summary>
    private static void ShowDecision(FrameworkElement view)
    {
        var scroll = Shell.ShellSupport.Under<System.Windows.Controls.ScrollViewer>(view).Single(s => s.Name == "MemoryScroll");
        scroll.UpdateLayout();
        scroll.ScrollToBottom();
        scroll.UpdateLayout();
    }

    private static RenderCase InstancesCase(string name, Instances.FakeInstances source, string shows)
    {
        Func<FrameworkElement, Task>? ready = null;
        return new RenderCase("instances", name, () =>
        {
            var (view, isReady) = Instances.InstancesPageTests.Rendered(source);
            ready = isReady;
            return view;
        }) { Shows = shows, Ready = view => ready!(view) };
    }

    private static RenderCase MemoryCase(string name, Memory.MemoryPageTests.Source source, string shows, Action<Bosun.Panel.Memory.MemoryPage>? then = null, bool tall = false)
    {
        Func<FrameworkElement, Task>? ready = null;
        return new RenderCase("memory", name, () =>
        {
            var (view, isReady) = Memory.MemoryPageTests.Rendered(source, then);
            ready = isReady;
            return view;
        }) { Shows = shows, Ready = tall ? async view => { await ready!(view); ShowDecision(view); } : view => ready!(view),
            Height = tall ? 1100 : PageRenderer.Height };  // tall: the Decision card, scrolled into view whole
    }

    /// <summary>The shell on the Settings page with the palette open and <paramref name="query"/> typed.</summary>
    private static FrameworkElement Paletted(string query)
    {
        var settings = new Settings.SettingsFixture();
        var shell = settings.Shell();
        shell.Show(shell.Sections.Single(s => s.Key == "settings"));
        shell.Palette.Open();
        shell.Palette.Query = query;
        return new Bosun.Panel.Shell.ShellView { DataContext = shell };
    }

    /// <summary>The Settings page's theme choice with Dark chosen, and a ring to place on it once it is laid out.</summary>
    private static FrameworkElement SegmentedFocus() => new System.Windows.Documents.AdornerDecorator
    {
        Child = new Grid
        {
            Margin = new Thickness(26), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Children = { new Bosun.Panel.Controls.SegmentedControl { ItemsSource = new[] { "Follow Windows", "Dark", "Light" }, SelectedIndex = 1 } },
        },
    };

    /// <summary>The chosen segment's own focus visual, drawn as keyboard focus draws it (FocusShown, D02 T04 §1).</summary>
    private static Task PlaceRing(FrameworkElement view)
    {
        view.UpdateLayout();
        var control = Shell.ShellSupport.Under<Bosun.Panel.Controls.SegmentedControl>(view).Single();
        FocusShown.Show((FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(control.SelectedIndex));
        return Task.CompletedTask;
    }

    /// <summary>A field in each state beside a combo box, labelled (D02 T04 §2).</summary>
    private static FrameworkElement FieldStates()
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(20) };
        void Add(string name, Action<TextBox> set, bool focus = false)
        {
            var field = new TextBox { Text = name.StartsWith("invalid", StringComparison.Ordinal) ? "lots" : "2048", Width = 180, HorizontalAlignment = HorizontalAlignment.Left, Tag = focus ? "focus" : null };
            field.SetResourceReference(FrameworkElement.StyleProperty, "Field");
            set(field);
            var label = new TextBlock { Text = name };
            label.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");
            grid.Children.Add(new StackPanel { Margin = new Thickness(16), Children = { label, field } });
        }
        Add("normal", _ => { });
        Add("invalid", f => Bosun.Panel.Theme.FieldState.SetInvalid(f, true));
        Add("focused", _ => { }, focus: true);
        Add("invalid and focused", f => Bosun.Panel.Theme.FieldState.SetInvalid(f, true), focus: true);
        Add("read only", f => f.IsReadOnly = true);
        Add("disabled", f => f.IsEnabled = false);
        var combo = new ComboBox { ItemsSource = new[] { "2048" }, SelectedIndex = 0, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        var comboLabel = new TextBlock { Text = "combo box, for its inset" };
        comboLabel.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");
        grid.Children.Add(new StackPanel { Margin = new Thickness(16), Children = { comboLabel, combo } });
        return new System.Windows.Documents.AdornerDecorator { Child = grid };
    }

    private static Task ShowFieldFocus(FrameworkElement view)
    {
        view.UpdateLayout();
        foreach (var field in Shell.ShellSupport.Under<TextBox>(view).Where(f => (string?)f.Tag == "focus")) FocusShown.Show(field);
        return Task.CompletedTask;
    }

    /// <summary>Every keyboard target with its own focus visual shown (D02 T04 §1), labelled, two columns.</summary>
    private static FrameworkElement Focused()
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(20) };
        foreach (var c in FocusOutlineTests.All)
        {
            var cell = new StackPanel { Margin = new Thickness(16), Tag = c };
            cell.Children.Add(new TextBlock { Text = c.Name, Margin = new Thickness(0, 0, 0, 10) });
            cell.Children.Add(new Grid { HorizontalAlignment = HorizontalAlignment.Left, Children = { c.Make() } });
            grid.Children.Add(cell);
        }
        return new System.Windows.Documents.AdornerDecorator { Child = grid };
    }

    private static Task ShowFocus(FrameworkElement view)
    {
        view.UpdateLayout();
        foreach (var cell in Shell.ShellSupport.Under<StackPanel>(view).Where(p => p.Tag is FocusOutlineTests.Case))
        {
            var c = (FocusOutlineTests.Case)cell.Tag;
            var made = (FrameworkElement)((Grid)cell.Children[1]).Children[0];
            FocusShown.Show(c.Target(made));
        }
        return Task.CompletedTask;
    }

    /// <summary>The shell with counts waiting: Overview (selected) 12, Queue 3, Settings 1.</summary>
    private static FrameworkElement Badged()
    {
        var shell = Shell.Shells.Panel();
        foreach (var (key, count) in new[] { ("overview", 12), ("queue", 3), ("settings", 1) })
            shell.Sections.Single(s => s.Key == key).Page.Waiting = count;
        return new Bosun.Panel.Shell.ShellView { DataContext = shell };
    }

    /// <summary>The shell painted for a backdrop, as <see cref="Bosun.Panel.Shell.Backdrop.Paint"/> paints it in the window.</summary>
    private static FrameworkElement Backdropped(Bosun.Panel.Shell.BackdropKind kind)
    {
        var shell = new Bosun.Panel.Shell.ShellView { DataContext = Shell.Shells.Panel() };
        Bosun.Panel.Shell.Backdrop.Paint(null, shell, kind);
        return shell;
    }

    /// <summary>A page showing the data it read 12 minutes ago, before WSL stopped, by a clock fixed for the picture.</summary>
    private static FrameworkElement Stale()
    {
        var clock = new Helpers.ManualClock();
        var page = new Shell.TestPage("Instances", "Every dev instance on both sides.") { Clock = clock, Body = "Ubuntu: 3 instances, last read 09:48" };
        page.LastKnown("WSL is stopped", clock.GetUtcNow() - TimeSpan.FromMinutes(12));
        return Framed(page);
    }

    /// <summary>Two values that can't be read: one with its reason, and a null, which says only that.</summary>
    private static FrameworkElement NotAvailableValues()
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(26, 22, 26, 12) };
        foreach (var value in new object?[] { new Bosun.Panel.Shell.NotAvailable("WSL is stopped, so its memory can't be read."), null })
        {
            var shown = new System.Windows.Controls.ContentControl { Content = value, Margin = new Thickness(0, 0, 0, 16) };
            shown.SetResourceReference(FrameworkElement.StyleProperty, "Value");
            panel.Children.Add(shown);
        }
        return panel;
    }

    /// <summary>A control with the focus ring drawn round it, and a drop-down's list as it opens: one item highlighted, one selected.</summary>
    private static FrameworkElement States()
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(26, 22, 26, 12) };
        var focused = new System.Windows.Controls.Control { Width = 160, Height = 32, Margin = new Thickness(4, 4, 4, 24), HorizontalAlignment = HorizontalAlignment.Left };
        focused.SetResourceReference(FrameworkElement.StyleProperty, "FocusRing");
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Focus ring", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(focused);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "A drop-down's list", Margin = new Thickness(0, 0, 0, 6) });
        var list = new System.Windows.Controls.StackPanel { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        var drop = new System.Windows.Controls.Border { Child = list, BorderThickness = new Thickness(1), Padding = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        drop.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "B.Raised");
        drop.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "B.Line");
        drop.SetResourceReference(System.Windows.Controls.Border.CornerRadiusProperty, "R.Control");
        list.Children.Add(new DropItem("Follow Windows"));
        list.Children.Add(new DropItem("Highlighted", highlighted: true));
        list.Children.Add(new DropItem("Selected") { IsSelected = true });
        panel.Children.Add(drop);
        return panel;
    }

    /// <summary>A drop-down's item drawn outside a drop-down, so a picture can show the pointer's highlight on it.</summary>
    private sealed class DropItem : System.Windows.Controls.ComboBoxItem
    {
        public DropItem(string words, bool highlighted = false)
        {
            Content = words;
            IsHighlighted = highlighted;
            SetResourceReference(StyleProperty, typeof(System.Windows.Controls.ComboBoxItem));
        }
    }

    /// <summary>The Settings page while Windows draws in a contrast theme: the line under the theme box says why the choice waits.</summary>
    private static FrameworkElement SettingsInContrast()
    {
        var fixture = new Bosun.Panel.Tests.Settings.SettingsFixture().Open();
        var theme = new Bosun.Panel.Theme.ThemeService(new List<ResourceDictionary>(), new Bosun.Panel.Tests.Settings.FakeWindowsTheme(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher, _ => new ResourceDictionary(), new Helpers.ContrastOn());
        theme.Apply(Bosun.Core.ThemeChoice.Dark);
        var page = new Bosun.Panel.Settings.SettingsPage(fixture.Store, fixture.Startup, fixture.Log, () => { }, theme);
        return new Bosun.Panel.Shell.PageFrame { DataContext = page, Margin = new Thickness(26, 22, 26, 12) };
    }

    /// <summary>The Settings page in the frame, over settings that are only memory, arranged before and after it opens.</summary>
    private static FrameworkElement Settings(Action<Bosun.Panel.Tests.Settings.SettingsFixture> before, Action<Bosun.Panel.Tests.Settings.SettingsFixture>? after = null)
    {
        var fixture = new Bosun.Panel.Tests.Settings.SettingsFixture();
        before(fixture);
        fixture.Open();
        after?.Invoke(fixture);
        return new Bosun.Panel.Shell.PageFrame { DataContext = fixture.Page, Margin = new Thickness(26, 22, 26, 12) };
    }

    /// <summary>The shell with a page that has unsaved changes, asked about as it is being left.</summary>
    private static FrameworkElement LeaveQuestion()
    {
        var (shell, first, _, _) = Shell.Shells.WithTestPages();
        first.Edits = "slots 3";
        var view = new Bosun.Panel.Shell.ShellView { DataContext = shell };
        Shell.TestPage.AddTemplate(view);
        _ = shell.LeaveAsync(); // the question is up; nobody answers it in a picture
        return view;
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^[a-z0-9-]+\.[a-z0-9-]+\.(dark|light|highcontrast)\.png(\.tmp)?$")]
    private static partial Regex OwnedFile();

    private const string Index = "index.json";

    /// <summary>
    /// Whether a file in the renders folder is this catalogue's own: a render (or one half written) or the index.
    /// Anything else there, another picture included, is somebody else's: never removed, and never counted.
    /// </summary>
    public static bool Owns(string fileName) => fileName is Index or Index + ".tmp" || OwnedFile().IsMatch(fileName);

    /// <summary>What is wrong with a catalogue before anything is drawn: one line per problem, none when it is sound.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<RenderCase> cases)
    {
        var problems = new List<string>();
        foreach (var view in cases)
        {
            // The names become file names, and the dots between them are how a file is read back.
            if (!Name().IsMatch(view.View)) problems.Add($"view name '{view.View}' must be lower-case letters, digits and hyphens");
            if (!Name().IsMatch(view.State)) problems.Add($"state name '{view.State}' of {view.View} must be lower-case letters, digits and hyphens");
        }
        foreach (var twice in cases.GroupBy(view => view.Name).Where(group => group.Count() > 1)) problems.Add($"{twice.Key} is registered {twice.Count()} times");
        return problems;
    }

    /// <summary>Where the renders go: <c>build/renders/</c> in the checkout, unless <c>BOSUN_RENDER_DIR</c> names another folder.</summary>
    public static string Folder() =>
        Environment.GetEnvironmentVariable("BOSUN_RENDER_DIR") is { Length: > 0 } named ? Path.GetFullPath(named) : Path.Combine(TestPaths.Repo(), "build", "renders");

    /// <summary>The renders and index an earlier run left in <paramref name="folder"/>; nothing else in it is touched.</summary>
    public static void Clear(string folder)
    {
        if (!Directory.Exists(folder)) return;
        // The index goes first: if a picture can't be removed, what is left must not read as a whole run.
        File.Delete(Path.Combine(folder, Index));
        File.Delete(Path.Combine(folder, Index + ".tmp"));
        foreach (var file in Directory.GetFiles(folder))
            if (Owns(Path.GetFileName(file))) File.Delete(file);
    }

    /// <summary>
    /// Draws every case in both themes into <paramref name="folder"/> as <c>view.state.theme.png</c>, after clearing
    /// what an earlier run left. The index is written last and only when every picture was drawn and written, so a
    /// folder with an index is one whole, current run. Any failure throws, naming every view that failed.
    /// </summary>
    public static IReadOnlyList<string> WriteAll(IReadOnlyList<RenderCase> cases, string folder)
    {
        Directory.CreateDirectory(folder);
        Clear(folder);
        if (Problems(cases) is { Count: > 0 } problems) throw new RenderException("the catalogue can't be drawn: " + string.Join("; ", problems));

        var failures = new List<string>();
        var written = new List<object>();
        var names = new List<string>();
        foreach (var theme in PageRenderer.Themes)
        {
            var pictures = new Dictionary<string, (RenderCase View, uint[] Pixels)>();
            foreach (var view in cases)
            {
                var name = $"{view.Name}.{theme.ToLowerInvariant()}.png";
                try
                {
                    var drawn = PageRenderer.Render(view, theme, view.Width, view.Height);
                    var (width, height, pixels) = PageRenderer.Decode(drawn.Png);
                    // Two states of one view that draw the same picture are one state: the factory ignored its state.
                    var same = pictures.Values.FirstOrDefault(other => other.View.View == view.View && other.Pixels.AsSpan().SequenceEqual(pixels));
                    if (same.View is not null) throw new RenderException($"{view.Name}: draws the same picture as {same.View.Name} in {theme}, so the two states are not two states");
                    pictures[view.Name] = (view, pixels);
                    Replace(Path.Combine(folder, name), drawn.Png);
                    names.Add(name);
                    written.Add(new { file = name, view = view.View, state = view.State, theme = theme.ToLowerInvariant(), width, height });
                }
                catch (Exception e) when (e is RenderException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(e is RenderException ? e.Message : $"{view.Name}: {name} can't be written: {e.Message}");
                }
            }
        }
        if (failures.Count > 0) throw new RenderException($"{failures.Count} of {cases.Count * PageRenderer.Themes.Count} renders failed, so no index was written: " + string.Join("; ", failures));

        var index = new { commit = Commit(), written_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), renders = written };
        Replace(Path.Combine(folder, Index), JsonSerializer.SerializeToUtf8Bytes(index, new JsonSerializerOptions { WriteIndented = true }));
        return names;
    }

    /// <summary>Writes beside the destination, then moves into place: a reader never sees half a file.</summary>
    private static void Replace(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary); // gone already when the move worked
        }
    }

    /// <summary>The commit the checkout is at, for the index; "unknown" when git can't say.</summary>
    private static string Commit()
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = TestPaths.Repo(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("rev-parse");
            start.ArgumentList.Add("HEAD");
            using var git = Process.Start(start)!;
            var said = git.StandardOutput.ReadToEnd().Trim();
            if (!git.WaitForExit(10_000))
            {
                git.Kill(entireProcessTree: true);
                return "unknown";
            }
            return git.ExitCode == 0 && said.Length == 40 ? said : "unknown";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown"; // no git on this machine: the pictures are still good, their commit is not recorded
        }
    }
}

/// <summary>What the sample view shows: a title, rows, or the words that stand where the rows would be.</summary>
public sealed record SampleData(string Title, IReadOnlyList<string> Rows, string? Empty);

/// <summary>
/// The catalogue's first view, the tests' own: a panel in the theme's colours whose every text is bound, so a render
/// of it proves the theme, the bindings and the state all reached the picture.
/// </summary>
public sealed class SampleView : UserControl
{
    /// <summary>Where the panel's own colour can be read: inside its padding, where no text is.</summary>
    public const int PanelX = 60, PanelY = 60;

    public SampleView(SampleData data)
    {
        DataContext = data;

        var title = new TextBlock { FontSize = 24, Margin = new Thickness(0, 0, 0, 16) };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(SampleData.Title)));
        title.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");

        var row = new FrameworkElementFactory(typeof(TextBlock));
        row.SetBinding(TextBlock.TextProperty, new Binding());
        row.SetResourceReference(TextBlock.ForegroundProperty, "B.TextDim");
        row.SetValue(MarginProperty, new Thickness(0, 4, 0, 4));
        var rows = new ItemsControl { ItemTemplate = new DataTemplate { VisualTree = row } };
        rows.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(SampleData.Rows)));

        var empty = new TextBlock { FontSize = 16 };
        empty.SetBinding(TextBlock.TextProperty, new Binding(nameof(SampleData.Empty)));
        empty.SetResourceReference(TextBlock.ForegroundProperty, "B.TextMute");

        var stack = new StackPanel { Margin = new Thickness(40, 60, 40, 40) };
        stack.Children.Add(title);
        stack.Children.Add(rows);
        stack.Children.Add(empty);

        var panel = new Border { Margin = new Thickness(40), CornerRadius = new CornerRadius(8), Child = stack };
        panel.SetResourceReference(Border.BackgroundProperty, "B.Surface");
        Content = panel;
    }
}

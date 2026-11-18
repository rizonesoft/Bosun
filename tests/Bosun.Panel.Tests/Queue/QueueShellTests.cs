using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Bosun.Core;
using Bosun.Core.Memory;
using Bosun.Core.Queue;
using Bosun.Panel.Controls;
using Bosun.Panel.Queue;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;
using static Bosun.Panel.Tests.Queue.QueueFixtures;
using static Bosun.Panel.Tests.Queue.QueuePageTests;

namespace Bosun.Panel.Tests.Queue;

/// <summary>
/// The real Queue page in the real shell (D03 T02 §3): opened through the rail, its tiles, waiting rows, history and
/// form drawn through PageFrame's template, the header's Refresh, Retry after a failure, the rail's badge, Ctrl+K, and
/// the history's columns sorting by their numbers.
/// </summary>
public class QueueShellTests
{
    /// <summary>The shell as the panel starts it, with the Queue page over <paramref name="source"/>, drawn.</summary>
    static void InShell(FakeQueueSource source, Action<ShellView, ShellViewModel, QueuePage, Border> test)
    {
        PageRenderer.InPage("Dark", () =>
        {
            var scheduler = new RefreshScheduler(() => Now.UtcDateTime, new Driver());
            var log = Shells.Log();
            var page = new QueuePage(source, scheduler, log);
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["queue"] = page }), log);
            return new ShellView { DataContext = shell };
        }, border =>
        {
            var view = ShellSupport.Under<ShellView>(border).Single();
            var shell = (ShellViewModel)view.DataContext;
            test(view, shell, (QueuePage)shell.Sections.Single(s => s.Key == "queue").Page, border);
            return 0;
        }, 1360, 1400);
    }

    static void Draw(Border border)
    {
        Settle();
        PageRenderer.Settle(border);
    }

    [Fact]
    public void The_rail_opens_Queue_with_its_tiles_waiting_rows_history_and_form_and_Refresh_and_Retry_work()
    {
        var source = new FakeQueueSource();
        InShell(source, (view, shell, page, border) =>
        {
            var rail = ShellSupport.Under<ListBox>(border).First(l => l.Name == "Rail");
            Assert.Equal(0, source.StatusReads);                                   // not on screen: nothing read
            ShellSupport.Select(rail, rail.Items.IndexOf(shell.Sections.Single(s => s.Key == "queue")));
            Draw(border);
            Assert.Same(page, shell.Page);
            var queue = ShellSupport.Under<QueueView>(border).Single();
            var tiles = ShellSupport.Under<QueueSlotTile>(queue).ToList();
            Assert.Equal(["Slot 1, Running, Recto: cmake --build build, Windows, waited 12 s, running 4 min", "Slot 2, Free"],
                tiles.Select(t => UIElementAutomationPeer.CreatePeerForElement(t).GetName()));
            var waiting = ShellSupport.Under<WaitingLineView>(queue).Select(l => UIElementAutomationPeer.CreatePeerForElement(l).GetName()).ToList();
            Assert.Equal(2, waiting.Count);
            Assert.StartsWith("1. Office: dotnet build. Waiting for memory: slot 2 opens when 6.0 GB is free", waiting[0], StringComparison.Ordinal);
            Assert.StartsWith("2. Office: dotnet test. Waiting for a free slot", waiting[1], StringComparison.Ordinal);
            var grid = ShellSupport.Under<DataGrid>(queue).Single(g => g.Name == "HistoryTable");
            Assert.Equal(["Command", "Repo", "Side", "Slot", "Waited", "Ran", "Exit", "Admission", "Ended"], grid.Columns.Select(c => (string)c.Header));
            Assert.Single(grid.Items);
            var boxes = ShellSupport.Under<TextBox>(queue).Where(b => b.Name == "Value").ToList();
            Assert.Equal(["Slots", "Memory floor for each slot after the first, in MB", "Maximum wait, in seconds", "Guest memory floor, in MB"],
                boxes.Select(AutomationProperties.GetName));
            var save = ShellSupport.Under<Button>(queue).Single(b => b.Name == "Save");
            Assert.Same(save.FindResource("Btn.Primary"), save.Style);                // the page's one primary action
            var resets = ShellSupport.Under<Button>(queue).Where(b => b.Name == "UseDefault").ToList();
            Assert.Equal(4, resets.Count);                                            // one per limit (round 3: none would pass All)
            Assert.True(resets.All(b => b.ActualHeight >= 32), "every Use the default is a 32 px target");

            // The header's Refresh reads again.
            var reads = source.StatusReads;
            ShellSupport.Invoke(ShellSupport.Under<Button>(border).Single(b => b.Name == "Refresh"));
            Draw(border);
            Assert.Equal(reads + 1, source.StatusReads);

            // Nothing readable: Failed with the tool's words and Retry, which recovers.
            source.Status = Reading<StatusDoc>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.");
            source.History = Reading<JobHistory>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.");
            source.Limits = Reading<ConfigDoc>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.");
            ShellSupport.Invoke(ShellSupport.Under<Button>(border).Single(b => b.Name == "Refresh"));
            Draw(border);
            Assert.Equal(PageState.Failed, page.State);
            Assert.Contains("bosun.exe is not installed.", PageRenderer.TextsOf(border));
            source.Status = Reading<StatusDoc>.Of(IdleStatus(), Now);
            source.History = Reading<JobHistory>.Of(new JobHistory([Job()], 0, []), Now);
            source.Limits = Reading<ConfigDoc>.Of(Config(), Now);
            ShellSupport.Invoke(ShellSupport.Under<Button>(border).Single(b => b.Name == "Retry" && b.IsVisible));
            Draw(border);
            Assert.Equal(PageState.Data, page.State);
            Assert.Contains(QueuePage.IdleNote, PageRenderer.TextsOf(ShellSupport.Under<QueueView>(border).Single()));
        });
    }

    [Fact]
    public void The_rail_badge_counts_the_waiters_and_Ctrl_K_finds_Queue()
    {
        var source = new FakeQueueSource();
        InShell(source, (view, shell, page, border) =>
        {
            Wait(shell.GoToAsync(shell.Sections.Single(s => s.Key == "queue")));
            Draw(border);
            var rail = ShellSupport.Under<ListBox>(border).First(l => l.Name == "Rail");
            var row = ShellSupport.Row(rail, rail.Items.IndexOf(shell.Sections.Single(s => s.Key == "queue")));
            Assert.Equal(2, page.Waiting);
            Assert.Equal("Queue, 2 waiting", UIElementAutomationPeer.CreatePeerForElement(row).GetName());
            Assert.Contains("2", PageRenderer.TextsOf(ShellSupport.Under<Decorative>(row).Single(d => d.Name == "Badge")));
            source.Status = Reading<StatusDoc>.Of(IdleStatus(), Now);
            page.RefreshNow!.Execute(null);
            Draw(border);
            Assert.Equal("Queue", UIElementAutomationPeer.CreatePeerForElement(row).GetName());

            Wait(shell.GoToAsync(shell.Sections.Single(s => s.Key == "overview")));
            shell.Palette.Open();
            shell.Palette.Query = "Queue";
            var go = shell.Palette.Rows.First(r => r.Name == "Queue" && r.Group == CommandPalette.PagesGroup);
            Assert.True(Wait(shell.Palette.RunAsync(go)));
            Settle();
            Assert.Same(page, shell.Page);
        });
    }

    [Fact]
    public void The_history_sorts_Slot_Waited_Ran_and_Exit_by_their_numbers_both_ways()
    {
        var source = new FakeQueueSource
        {
            History = Reading<JobHistory>.Of(new JobHistory(
            [
                Job(command: "a", slot: 1, waited: 3, ran: 0.5, exit: 2),
                Job(command: "b", slot: 9, waited: 0.5, ran: 3, exit: 10),
                Job(command: "c", slot: 0, waited: 10, ran: 20, exit: 0),
            ], 0, []), Now),
        };
        InShell(source, (view, shell, page, border) =>
        {
            Wait(shell.GoToAsync(shell.Sections.Single(s => s.Key == "queue")));
            Draw(border);
            var grid = ShellSupport.Under<DataGrid>(border).Single(g => g.Name == "HistoryTable");
            // Sorted as a person sorts it: the column's header pressed, once and again
            // (round 3: setting SortDescriptions in the test would pass with header sorting turned off).
            (string Ascending, string Descending) Order(string header)
            {
                string Press()
                {
                    var head = ShellSupport.Under<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).Single(h => h.Column is { } c && (string)c.Header == header);
                    // The header's own click, as a person's press runs it (DataGridColumnHeader.OnClick sorts by its column);
                    // its automation peer offers no Invoke pattern, so the protected method is called as the press calls it.
                    typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                        .Invoke(head, null);
                    Draw(border);
                    return string.Concat(grid.Items.Cast<HistoryLine>().Select(l => l.Command));
                }
                return (Press(), Press());
            }
            Assert.Equal(("cab", "bac"), Order("Exit"));      // 0, 2, 10
            Assert.Equal(("bac", "cab"), Order("Waited"));    // 0.5 s, 3 s, 10 s
            Assert.Equal(("abc", "cba"), Order("Ran"));       // 0.5 s, 3 s, 20 s
            Assert.Equal(("cab", "bac"), Order("Slot"));      // 1, 2, 10
            Assert.Equal(["10", "2", "1"], grid.Items.Cast<HistoryLine>().Select(l => l.Slot));       // one-based, as the tiles number them
        });
    }
}

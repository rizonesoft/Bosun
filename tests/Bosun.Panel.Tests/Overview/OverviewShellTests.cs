using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Bosun.Core.Memory;
using Bosun.Panel.Controls;
using Bosun.Panel.Overview;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Queue;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;
using static Bosun.Panel.Tests.Overview.OverviewFixtures;

namespace Bosun.Panel.Tests.Overview;

/// <summary>
/// The real Overview in the real shell (D03 T01 §5): the panel opens on it with its cards and table; every card and a
/// named row opens its page from its own control and through Ctrl+K; a cleared row leaves the palette; the frame's
/// states, the table kept above the failure; and the rail badge's spoken words.
/// </summary>
public class OverviewShellTests
{
    /// <summary>The panel's shell as NewShell makes it, drawn, with the rig the test drives.</summary>
    static void InShell(Func<OverviewRig> make, Action<OverviewRig, Border> test)
    {
        OverviewRig? rig = null;
        PageRenderer.InPage("Dark", () =>
        {
            rig = make();
            return new ShellView { DataContext = rig.Shell };
        }, border =>
        {
            Draw(border);
            test(rig!, border);
            return 0;
        }, 1360, 1000);
    }

    static void Draw(Border border)
    {
        OverviewRig.Settle();
        PageRenderer.Settle(border);
    }

    static string Said(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element).GetName();

    [Fact]
    public void The_panel_opens_on_the_Overview_with_its_four_cards_and_Needs_you_and_keeps_the_rail_row()
    {
        Assert.Contains(Sections.Rows, row => row.Key == "overview" && row.Owner == "D03 T01 §5");  // the descriptor stays
        InShell(() => new OverviewRig().WithTray(() => Fresh(WindowsSnapshot())), (rig, border) =>
        {
            Assert.Equal("overview", rig.Shell.Sections[0].Key);
            Assert.Same(rig.Shell.Sections[0], rig.Shell.Selected);
            Assert.IsType<OverviewPage>(rig.Shell.Page);
            Assert.True(rig.Page.IsShown);
            var view = ShellSupport.Under<OverviewView>(border).Single();
            var cards = new[] { "WindowsCard", "GuestCard", "QueueCard", "InstancesCard" }
                .Select(name => ShellSupport.Under<Button>(view).Single(b => b.Name == name)).ToList();
            Assert.Equal(["Windows available: 20.0 GB, Safe. Open Memory", "WSL guest available: 6.0 GB, Free. Open Memory",
                "Queue: 1 running, 2 waiting. Open Queue", "Dev instances: 5. Open Instances"], cards.Select(AutomationProperties.GetName));
            Assert.True(cards.All(c => c.ActualHeight >= 32 && c.IsVisible), "every card is a visible 32 px target");
            // The gap between cards is the theme's M space, never a literal (round 2): each card starts M after the last ends.
            var gap = (double)view.FindResource("S.M");
            for (var i = 1; i < cards.Count; i++)
            {
                var end = cards[i - 1].TranslatePoint(new System.Windows.Point(cards[i - 1].ActualWidth, 0), view).X;
                var start = cards[i].TranslatePoint(new System.Windows.Point(0, 0), view).X;
                Assert.Equal(gap, start - end, 0.5);
            }
            var gauges = ShellSupport.Under<RadialGauge>(view).Where(g => g.IsVisible).ToList();
            Assert.Equal([20.0 / 32.0, 6.0 / 14.0], gauges.Select(g => g.Fraction));
            var texts = PageRenderer.TextsOf(view);
            Assert.Contains("1 running, 2 waiting", texts);
            Assert.Contains("Windows 3, WSL (Ubuntu) 2", texts);
            Assert.Contains(NeedsYouTable.Nothing, texts);
            Assert.True(ShellSupport.Under<NeedsYouView>(view).Single().IsVisible);
            // No button on the page is the primary: the cards open pages, and the page has no one action that matters.
            Assert.DoesNotContain(ShellSupport.Under<Button>(view), b => ReferenceEquals(b.Style, b.TryFindResource("Btn.Primary")));
        });
    }

    [Fact]
    public void Every_card_and_a_named_row_opens_its_page_from_its_control_and_through_Ctrl_K_and_a_cleared_row_leaves_the_palette()
    {
        var provider = new FakeProvider();
        InShell(() =>
        {
            var rig = new OverviewRig();
            rig.Page.Needs.Add(provider);
            provider.Say(FakeProvider.Row("stuck", section: "queue", what: "A job has waited 2 hours"));
            return rig;
        }, (rig, border) =>
        {
            void Back()
            {
                rig.GoTo("overview");
                Draw(border);
            }

            foreach (var (name, key) in new[] { ("WindowsCard", "memory"), ("GuestCard", "memory"), ("QueueCard", "queue"), ("InstancesCard", "instances") })
            {
                ShellSupport.Invoke(ShellSupport.Under<Button>(border).Single(b => b.Name == name && b.IsVisible));
                Draw(border);
                Assert.Equal(key, rig.Shell.Selected.Key);
                Back();
            }
            var open = ShellSupport.Under<Button>(border).Single(b => b.Name == "OpenRow" && b.IsVisible);
            Assert.Equal("Open Queue: A job has waited 2 hours", AutomationProperties.GetName(open));
            Assert.True(open.ActualHeight >= 32);
            ShellSupport.Invoke(open);
            Draw(border);
            Assert.Equal("queue", rig.Shell.Selected.Key);
            Back();

            // The same, through Ctrl+K: each card's Open and the row's are the page's own commands.
            foreach (var (name, key) in new[] { ("Open Memory: Windows available", "memory"), ("Open Memory: WSL guest available", "memory"),
                         ("Open Queue: Queue", "queue"), ("Open Instances: Dev instances", "instances"), ("Open Queue: A job has waited 2 hours", "queue") })
            {
                rig.Shell.Palette.Open();
                var row = rig.Shell.Palette.Rows.Single(r => r.Name == name && r.Group == "Overview");
                Assert.True(OverviewRig.Wait(rig.Shell.Palette.RunAsync(row)));
                Draw(border);
                Assert.Equal(key, rig.Shell.Selected.Key);
                Back();
            }

            // A row that clears leaves the palette.
            provider.Say();
            Draw(border);
            rig.Shell.Palette.Open();
            Assert.DoesNotContain(rig.Shell.Palette.Rows, r => r.Name == "Open Queue: A job has waited 2 hours");
            rig.Shell.Palette.Close();
        });
    }

    [Fact]
    public void A_card_that_cant_be_read_still_opens_its_page_where_the_reason_is()
    {
        InShell(() => new OverviewRig(new FakeMemory { Windows = Missing("windows") }), (rig, border) =>
        {
            var card = ShellSupport.Under<Button>(border).Single(b => b.Name == "WindowsCard");
            Assert.StartsWith("Windows available: Not available: ", AutomationProperties.GetName(card), StringComparison.Ordinal);
            Assert.Contains(RadialGauge.NotAvailable, PageRenderer.TextsOf(card));
            ShellSupport.Invoke(card);
            Draw(border);
            Assert.Equal("memory", rig.Shell.Selected.Key);
        });
    }

    [Fact]
    public void Loading_until_the_first_source_answers_then_data_with_the_cards_and_Nothing_needs_you_inside_the_page()
    {
        var memory = new FakeMemory { NeverAnswers = true };
        var queue = new FakeQueueSource { StatusNeverAnswers = true };
        var held = new TaskCompletionSource();
        InShell(() => new OverviewRig(memory, queue, new Instances.FakeInstances { Hold = held }).WithTray(() => Fresh(WindowsSnapshot())), (rig, border) =>
        {
            Assert.Equal(PageState.Loading, rig.Page.State);
            held.SetResult();                                                        // the instances answer first
            Draw(border);
            Assert.Equal(PageState.Data, rig.Page.State);
            var view = ShellSupport.Under<OverviewView>(border).Single();
            Assert.True(view.IsVisible);
            Assert.Contains(NeedsYouTable.Nothing, PageRenderer.TextsOf(view));      // in the page, not the frame's Empty
            Assert.Equal(OverviewPage.Checking, rig.Page.Windows.Said);              // still reading, not "Not available"
        });
    }

    [Fact]
    public void With_every_card_failed_the_frame_fails_with_Retry_and_keeps_the_pressure_row_above_the_failure()
    {
        InShell(() => new OverviewRig(new FakeMemory { Windows = Missing("windows"), Guest = Missing("wsl") },
                new FakeQueueSource { Status = Reading<StatusDoc>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.") },
                new Instances.FakeInstances { Answer = Instances.InstanceFixtures.View(null, Instances.InstanceFixtures.Failed("windows", null, "bosun.exe is not installed.")) })
            .WithTray(() => Fresh(WindowsSnapshot(available: 1000))), (rig, border) =>
        {
            Assert.Equal(PageState.Failed, rig.Page.State);
            var failed = ShellSupport.Under<StackPanel>(border).Single(p => p.Name == "Failed");
            Assert.True(failed.IsVisible);
            var kept = ShellSupport.Under<NeedsYouView>(failed).Single();
            Assert.True(kept.IsVisible);
            var open = ShellSupport.Under<Button>(kept).Single(b => b.Name == "OpenRow");
            Assert.StartsWith("Open Memory: Memory pressure since ", AutomationProperties.GetName(open), StringComparison.Ordinal);
            Assert.True(ShellSupport.Under<Button>(failed).Single(b => b.Name == "Retry").IsVisible);
            // Above the failure: the table's bottom is over the heading's top.
            var heading = ShellSupport.Under<TextBlock>(failed).Single(t => t.Name == "FailedHeading");
            Assert.True(kept.TranslatePoint(new Point(0, kept.ActualHeight), failed).Y <= heading.TranslatePoint(new Point(0, 0), failed).Y);
            ShellSupport.Invoke(open);
            Draw(border);
            Assert.Equal("memory", rig.Shell.Selected.Key);
        });
    }

    [Fact]
    public void The_rail_rows_say_their_badges_in_words_from_the_first_reading()
    {
        var provider = new FakeProvider();
        InShell(() =>
        {
            var rig = new OverviewRig();
            rig.Page.Needs.Add(provider);
            return rig;
        }, (rig, border) =>
        {
            var rail = ShellSupport.Under<ListBox>(border).First(l => l.Name == "Rail");
            ListBoxItem Row(string key) => ShellSupport.Row(rail, rail.Items.IndexOf(rig.Section(key)));
            Assert.Equal("Queue, 2 waiting", Said(Row("queue")));                     // Queue never visited
            Assert.Contains("2", PageRenderer.TextsOf(ShellSupport.Under<Decorative>(Row("queue")).Single(d => d.Name == "Badge")));
            provider.Say(FakeProvider.Row("stuck"), FakeProvider.Row("mem", section: "memory"));
            Draw(border);
            Assert.Equal("Queue, 2 waiting, 1 needs you", Said(Row("queue")));
            Assert.Contains("3", PageRenderer.TextsOf(ShellSupport.Under<Decorative>(Row("queue")).Single(d => d.Name == "Badge")));
            Assert.Equal("Memory, 1 needs you", Said(Row("memory")));
            provider.Say();
            Draw(border);
            Assert.Equal("Memory", Said(Row("memory")));
            Assert.False(ShellSupport.Under<Decorative>(Row("memory")).Single(d => d.Name == "Badge").IsVisible);
        });
    }
}

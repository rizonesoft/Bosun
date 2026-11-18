using Bosun.Core.Memory;
using Bosun.Panel.Memory;
using Bosun.Panel.Tests.Settings;
using Xunit;
using static Bosun.Panel.Tests.Memory.MemoryPageTests;

namespace Bosun.Panel.Tests.Memory;

/// <summary>
/// The Memory page's Decision card (D03 T01 §6): its verdict from the fortnight's report read at the stored threshold, a
/// reading at a threshold since changed dropped, and its own failure, Retry and kept verdict, apart from the chart.
/// </summary>
public class DecisionCardTests
{
    [Fact]
    public void The_fortnight_is_read_at_the_stored_threshold_and_the_card_says_its_verdict()
    {
        var settings = new SettingsFixture().Open();
        settings.Store.Update(s => s with { PressureFloorMb = 3072 });
        var source = new Source { Fortnight = Reading<ReportDoc>.Of(ReportOf(evidence: Evidence.Replace("\"low_minutes\":0", "\"low_minutes\":12"), lowMb: 3072), Now) };
        OnMemoryPage(source, (page, _, _, _) =>
        {
            Assert.Equal((long?)3072, source.LastFloor);
            Assert.Equal(DecisionKind.More, page.Decision!.Kind);
            Assert.Contains("12 minutes below the 3.0 GB pressure line", page.Decision.Evidence);
            Assert.Equal("", page.DecisionState);
        }, settings.Store);
    }

    [Fact]
    public void A_new_threshold_reads_again_and_a_reading_at_the_old_one_is_dropped()
    {
        var settings = new SettingsFixture().Open();
        // The pending request reads at 2048 and would say more memory; the one at the new floor says enough.
        var old = Reading<ReportDoc>.Of(ReportOf(evidence: Evidence.Replace("\"low_minutes\":0", "\"low_minutes\":12"), lowMb: 2048), Now);
        var source = new Source { Fortnight = old, HoldFortnight = new TaskCompletionSource() };
        OnMemoryPage(source, (page, _, _, _) =>
        {
            Assert.Equal(MemoryPage.ReadingDecision, page.DecisionState);
            Assert.Equal((long?)2048, source.LastFloor);                                        // pending at 2048
            var first = source.HoldFortnight!;
            Assert.True(settings.Store.Update(s => s with { PressureFloorMb = 4096 }).Succeeded); // saved while it is pending
            Settle();
            Assert.Equal(4096, page.FloorMb);

            source.Fortnight = Reading<ReportDoc>.Of(ReportOf(lowMb: 4096), Now);
            var second = new TaskCompletionSource();
            source.HoldFortnight = second;                                                       // the re-read stays pending
            first.SetResult();                                                                   // the 2048 reading arrives late
            Settle();
            Settle();
            Assert.Equal((long?)4096, source.LastFloor);                                        // read again at the new one
            Assert.Null(page.Decision);                                                          // the old verdict never shows
            Assert.NotEqual("", page.DecisionState);

            source.HoldFortnight = null;
            second.SetResult();
            Settle();
            Settle();
            Assert.Equal("32.0 GB has been enough so far.", page.Decision!.Verdict);
            Assert.Contains("0 minutes below the 4.0 GB pressure line", page.Decision.Evidence); // the line, minutes and verdict agree
        }, settings.Store);
    }

    [Fact]
    public void A_verdict_counted_at_the_old_floor_goes_at_once_when_another_is_saved()
    {
        var settings = new SettingsFixture().Open();
        var source = new Source();
        OnMemoryPage(source, (page, _, _, _) =>
        {
            Assert.Equal("32.0 GB has been enough so far.", page.Decision!.Verdict);           // read at 2048
            source.HoldFortnight = new TaskCompletionSource();                                   // the next read stays pending
            source.Fortnight = Reading<ReportDoc>.Of(ReportOf(lowMb: 4096), Now);                // what it will read at the new floor
            Assert.True(settings.Store.Update(s => s with { PressureFloorMb = 4096 }).Succeeded);
            Settle();
            Assert.Null(page.Decision);
            Assert.Equal("Reading the last 14 days again at the new 4.0 GB pressure line.", page.DecisionState);
            var held = source.HoldFortnight;
            source.HoldFortnight = null;
            held.SetResult();
            Settle();
            Settle();
            Assert.Contains("0 minutes below the 4.0 GB pressure line", page.Decision!.Evidence);
            Assert.Equal("", page.DecisionState);
        }, settings.Store);
    }

    /// <summary>The Memory page drawn in the shell at this window size, once its reads are in.</summary>
    static void Drawn(int width, int height, Action<System.Windows.Controls.Border, MemoryPage> read, Source? source = null)
    {
        Func<System.Windows.FrameworkElement, Task>? ready = null;
        MemoryPage? shown = null;
        Bosun.Panel.Tests.Render.PageRenderer.InPage("Dark", () =>
        {
            var (view, isReady) = Rendered(source ?? new Source(), page => shown = page);
            ready = isReady;
            return view;
        }, root =>
        {
            var waiting = ready!(root);
            while (!waiting.IsCompleted) Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            Assert.True(waiting.IsCompletedSuccessfully, $"the page was drawn ({waiting.Status}): {waiting.Exception}");   // a cancelled or failed draw is no draw
            Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            read(root, shown!);
            return 0;
        }, width, height);
    }

    static System.Windows.Rect Box(System.Windows.FrameworkElement element, System.Windows.Media.Visual to) =>
        element.TransformToAncestor(to).TransformBounds(new System.Windows.Rect(element.RenderSize));

    static System.Windows.Controls.ScrollViewer MemoryScroll(System.Windows.FrameworkElement root) =>
        Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.ScrollViewer>(root).Single(s => s.Name == "MemoryScroll");

    static List<System.Windows.Controls.Border> Cards(System.Windows.Controls.ScrollViewer scroll) =>
        Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.Border>(scroll)
            .Where(b => ReferenceEquals(b.Style, b.TryFindResource("Card")) && b.IsVisible).ToList();

    [Fact]
    public void The_page_s_scroll_bar_has_a_gutter_the_cards_stop_short_of_and_none_when_it_is_gone()
    {
        // D02 T04 §8: the cards' right borders met the scroll bar.
        Drawn(960, 600, (root, _) =>
        {
            var scroll = MemoryScroll(root);
            var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            Assert.True(bar.IsVisible, "the page scrolls at 960 x 600");
            var gap = (double)root.FindResource("S.S");
            var barLeft = Box(bar, root).Left;
            var cards = Cards(scroll);
            Assert.True(cards.Count >= 5, $"the cards are drawn ({cards.Count})");
            // Every card ends at least the gap short of the bar, and the rightmost (the full-width cards and the row of
            // three, read as one) exactly the gap short: no gap too small, none too big.
            Assert.All(cards, c => Assert.True(Box(c, root).Right <= barLeft - gap + 1, $"{System.Windows.Automation.AutomationProperties.GetName(c)} ends at {Box(c, root).Right}, the bar starts at {barLeft}"));
            Assert.Equal(barLeft - gap, cards.Max(c => Box(c, root).Right), 1.0);
            var decision = cards.Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Decision");
            Assert.Equal(barLeft - gap, Box(decision, root).Right, 1.0);
        });
        Drawn(960, 2400, (root, _) =>
        {
            var scroll = MemoryScroll(root);
            var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            Assert.False(bar.IsVisible, "the page fits at 960 x 2400");
            var presenter = (System.Windows.FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            var decision = Cards(scroll).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Decision");
            Assert.Equal(Box(presenter, root).Right, Box(decision, root).Right, 1.0);       // no gutter left without a bar
            Assert.Equal(Box(scroll, root).Right, Box(presenter, root).Right, 1.0);
        });
    }

    [Fact]
    public void The_shown_scroll_bar_takes_the_page_to_its_last_evidence_row_and_back()
    {
        // Driven through the bar a person drags or pages, not the scroll area's own methods (§8 plan review).
        Drawn(960, 600, (root, page) =>
        {
            var scroll = MemoryScroll(root);
            var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            var presenter = (System.Windows.FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
            Assert.Equal(0, scroll.VerticalOffset);
            for (var i = 0; i < 40 && scroll.VerticalOffset < scroll.ScrollableHeight - 0.5; i++)
            {
                System.Windows.Controls.Primitives.ScrollBar.PageDownCommand.Execute(null, bar);
                Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            }
            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 0.5);
            var rows = Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.TextBlock>(scroll).Where(t => t.Name == "Line").ToList();
            Assert.NotEmpty(rows);
            var last = Box(rows[^1], presenter);
            Assert.True(last.Top >= 0 && last.Bottom <= presenter.ActualHeight + 1, $"the last evidence row is in view ({last} in {presenter.ActualHeight})");
            System.Windows.Controls.Primitives.ScrollBar.ScrollToTopCommand.Execute(null, bar);
            Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            Assert.Equal(0, scroll.VerticalOffset);
            var top = Cards(scroll).First(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Available memory");
            Assert.True(Box(top, presenter).Top >= 0, "the top card is in view again");
        });
    }

    [Fact]
    public void The_evidence_is_a_labelled_list_its_rows_the_verdict_s_evidence_in_order_each_with_a_marker()
    {
        Drawn(1360, 2400, (root, page) =>
        {
            var scroll = MemoryScroll(root);
            var list = Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.StackPanel>(scroll).Single(s => s.Name == "EvidenceList");
            Assert.True(list.IsVisible);
            Assert.Contains("What the verdict rests on", Bosun.Panel.Tests.Render.PageRenderer.TextsOf(list));
            var lines = Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.TextBlock>(list).Where(t => t.Name == "Line").ToList();
            Assert.Equal(page.Decision!.Evidence, lines.Select(l => l.Text));                  // what the user reads, in order
            var markers = Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.TextBlock>(list).Where(t => t.Name == "Marker").ToList();
            Assert.Equal(lines.Count, markers.Count);
            // Made narrow, a row wraps: its whole text drawn, not cut, and its marker on its first line.
            list.Width = 160;
            Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            var row = lines.MaxBy(l => l.Text.Length)!;
            var marker = markers[lines.IndexOf(row)];
            var height = row.LineHeight is > 0 and var h ? h : row.FontSize * row.FontFamily.LineSpacing;
            Assert.True(row.ActualHeight > height * 1.5, $"\"{row.Text}\" wraps at 160 px ({row.ActualHeight} tall)");
            Assert.Equal(System.Windows.TextTrimming.None, row.TextTrimming);
            Assert.Equal(row.DesiredSize.Height, row.ActualHeight, 0.5);                         // the whole of it, not clipped
            var (r, m) = (Box(row, root), Box(marker, root));
            Assert.True(m.Top >= r.Top - 0.5 && m.Top < r.Top + height, $"the marker sits on the first line ({m.Top} against {r.Top})");
            // A screen reader says each row's words once, and no marker.
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(
                Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.ItemsControl>(list).Single(i => i.Name == "Evidence"));
            var said = new List<string>();
            void Walk(System.Windows.Automation.Peers.AutomationPeer p) { foreach (var c in p.GetChildren() ?? []) { said.Add(c.GetName()); Walk(c); } }
            Walk(peer);
            Assert.DoesNotContain(said, s => s.Contains('•'));
            Assert.All(page.Decision!.Evidence, e => Assert.Single(said, s => s == e));
        });
    }

    [Fact]
    public void The_evidence_shows_only_with_evidence_and_stays_beside_a_verdict_kept_after_a_failed_read()
    {
        var source = new Source { HoldFortnight = new TaskCompletionSource() };
        Drawn(1360, 2400, (root, page) =>
        {
            var list = Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.StackPanel>(root).Single(s => s.Name == "EvidenceList");
            Assert.Null(page.Decision);                                                          // the first read is pending
            Assert.False(list.IsVisible, "no label over an empty list while the first read is pending");
            var held = source.HoldFortnight!;
            source.HoldFortnight = null;
            held.SetResult();
            for (var i = 0; i < 20 && page.Decision is null; i++) Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            Assert.True(list.IsVisible, "shown once the verdict has its evidence");
            // A later read that fails keeps the verdict, beside the stale-data message, and its evidence with it.
            source.Fortnight = Reading<ReportDoc>.Fail(ReadingProblem.Failed, "bosun report ran past 60 s.");
            page.RefreshNow!.Execute(null);
            for (var i = 0; i < 20 && !page.DecisionFailed; i++) Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            Assert.True(page.DecisionFailed);
            Assert.NotNull(page.Decision);
            Assert.NotEqual("", page.DecisionState);
            Assert.True(list.IsVisible, "the kept verdict keeps its evidence");
        }, source);
    }

    [Fact]
    public void The_Decision_card_is_in_reach_in_a_window_shorter_than_the_page()
    {
        // The frame is the window's height; the Memory page is taller, so it scrolls itself (D03 T02 §2 completion review).
        Func<System.Windows.FrameworkElement, Task>? ready = null;
        Bosun.Panel.Tests.Render.PageRenderer.InPage("Dark", () =>
        {
            var (view, isReady) = Rendered(new Source { Report = Bosun.Core.Memory.Reading<ReportDoc>.Of(ReportOf(), Now) });
            ready = isReady;
            return view;
        }, root =>
        {
            var waiting = ready!(root);
            while (!waiting.IsCompleted) Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            Assert.True(waiting.IsCompletedSuccessfully, $"the page was drawn ({waiting.Status}): {waiting.Exception}");   // a cancelled or failed draw is no draw
            Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            var scroll = Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.ScrollViewer>(root).Single(s => s.Name == "MemoryScroll");
            Assert.True(scroll.ScrollableHeight > 0, $"the page scrolls (extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight})");
            scroll.ScrollToBottom();
            Bosun.Panel.Tests.Render.PageRenderer.Settle(root);
            var card = Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.Border>(root)
                .Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Decision");
            var presenter = Bosun.Panel.Tests.Shell.ShellSupport.Under<System.Windows.Controls.ScrollContentPresenter>(scroll).First();
            var bottom = card.TransformToAncestor(presenter).Transform(new System.Windows.Point(0, card.ActualHeight)).Y;
            Assert.True(bottom <= scroll.ViewportHeight + 1, $"the Decision card is in view at the bottom ({bottom} of {scroll.ViewportHeight})");
            return 0;
        }, 960, 600);
    }

    [Fact]
    public void A_failed_fortnight_has_the_cards_own_Retry_while_the_days_chart_is_fine_and_a_later_failure_keeps_the_verdict()
    {
        var source = new Source { Fortnight = Reading<ReportDoc>.Fail(ReadingProblem.Failed, "bosun report ran past 60 s.") };
        OnMemoryPage(source, (page, _, _, _) =>
        {
            Assert.Equal(HistoryRange.Day, page.Range);
            Assert.False(page.HistoryFailed);                                                    // the chart's day is fine
            Assert.True(page.DecisionFailed);
            Assert.Null(page.Decision);
            Assert.Equal("The last 14 days could not be read for the decision: bosun report ran past 60 s.", page.DecisionState);
            var retry = Assert.Single(page.PaletteCommands, c => c.Name == "Retry reading the decision's history");
            source.Fortnight = Reading<ReportDoc>.Of(ReportOf(), Now);
            retry.Command.Execute(null);
            Settle();
            Assert.False(page.DecisionFailed);
            Assert.Equal("32.0 GB has been enough so far.", page.Decision!.Verdict);

            source.Fortnight = Reading<ReportDoc>.Fail(ReadingProblem.Failed, "bosun report ran past 60 s.");
            page.RefreshNow!.Execute(null);
            Settle();
            Assert.Equal("32.0 GB has been enough so far.", page.Decision!.Verdict);           // kept
            Assert.StartsWith("The last 14 days could not be read again: bosun report ran past 60 s. Showing the verdict from ", page.DecisionState, StringComparison.Ordinal);
            Assert.EndsWith(" ago.", page.DecisionState, StringComparison.Ordinal);
        });
    }
}

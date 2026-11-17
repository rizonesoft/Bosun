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

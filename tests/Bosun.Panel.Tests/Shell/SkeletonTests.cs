using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Bosun.Panel.Accessibility;
using Bosun.Panel.Controls;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Helpers;
using Bosun.Panel.Tests.Render;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The loading state's skeleton (D02 T03 §7, design/SPEC.md §19): it appears 150 ms after a load starts and not
/// before, so a fast load never flashes it; it says "Loading" once per load that shows it; and it runs only while its
/// page is on screen. Time is a clock the test moves.
/// </summary>
public class SkeletonTests
{
    /// <summary>A page in a frame, live in a page that is never shown, with the clock and what was announced.</summary>
    private sealed class Bench
    {
        public ManualClock Clock { get; } = new();
        public TestPage Page { get; init; } = new();
        public PageFrame Frame { get; private set; } = null!;
        public List<string> Said { get; } = [];
        public Border Host { get; private set; } = null!;

        public static void Run(Action<Bench> test, TestPage? page = null) => PageRenderer.InPage("Dark", () =>
        {
            var bench = new Bench { Page = page ?? new TestPage() };
            LiveRegion.Raising = peer => bench.Said.Add(peer.GetName());
            bench.Frame = new PageFrame { Clock = bench.Clock, DataContext = bench.Page };
            TestPage.AddTemplate(bench.Frame);
            BenchHolder.Current = bench;
            return bench.Frame;
        }, host =>
        {
            var bench = BenchHolder.Current!;
            bench.Host = host;
            try
            {
                test(bench);
            }
            finally
            {
                LiveRegion.Raising = null;
                BenchHolder.Current = null;
            }
            return true;
        });

        /// <summary>Moves time on, then lets what it queued run and lays the frame out, as the dispatcher would.</summary>
        public void After(double milliseconds)
        {
            Clock.Advance(milliseconds);
            PageRenderer.Settle(Host);
        }

        public void Settle() => PageRenderer.Settle(Host);

        public bool Shows => Frame.Skeleton.Revealed;

        public IReadOnlyList<string> Loadings => [.. Said.Where(s => s.StartsWith("Loading", StringComparison.Ordinal))];
    }

    private static class BenchHolder
    {
        [ThreadStatic] public static Bench? Current;
    }

    [Fact]
    public void The_skeleton_appears_at_150_ms_and_not_a_moment_before_and_each_load_waits_its_own()
    {
        Bench.Run(b =>
        {
            b.Page.Loading();
            b.Page.Show();
            b.Settle();
            Assert.False(b.Shows);
            b.After(149);
            Assert.False(b.Shows);
            b.After(1);
            Assert.True(b.Shows);
            Assert.Equal(SkeletonShape.Card, b.Frame.Skeleton.Shape);

            b.Page.Data();
            b.Settle();
            Assert.False(b.Shows);

            b.Page.Loading(); // a second load: its own 150 ms, not what is left of the first
            b.Settle();
            Assert.False(b.Shows);
            b.After(149);
            Assert.False(b.Shows);
            b.After(1);
            Assert.True(b.Shows);
        });
    }

    [Fact]
    public void A_fast_load_never_shows_it_or_says_anything_and_a_slow_one_says_Loading_once()
    {
        Bench.Run(b =>
        {
            b.Page.Loading();
            b.Page.Show();
            b.After(50);
            b.Page.Data(); // done in 50 ms
            b.After(500);
            Assert.False(b.Shows);
            Assert.Empty(b.Loadings);

            b.Page.Loading();
            b.Settle();
            b.After(500); // a 500 ms load
            Assert.True(b.Shows);
            Assert.Equal(["Loading Queue"], b.Loadings);
            Assert.Equal("Loading", System.Windows.Automation.AutomationProperties.GetItemStatus(ShellSupport.Under<TextBlock>(b.Frame.Skeleton).First()));
            b.After(5000);
            Assert.Equal(["Loading Queue"], b.Loadings); // once per load, however long it lasts
        });
    }

    [Fact]
    public void A_page_that_leaves_the_screen_during_the_delay_drops_it_and_starts_afresh_when_it_is_back()
    {
        Bench.Run(b =>
        {
            b.Page.Loading();
            b.Page.Show();
            b.After(100);
            b.Page.Hide(); // navigated away, minimised or hidden to the tray: the shell hides the page (D02 T02 §8)
            b.After(500);
            Assert.False(b.Shows);
            Assert.Empty(b.Loadings);
            Assert.Equal(0, b.Clock.Pending); // no timer left behind

            b.Page.Show(); // back, still loading: a delay of its own, from now
            b.Settle();
            b.After(149);
            Assert.False(b.Shows);
            b.After(1);
            Assert.True(b.Shows);
            Assert.Equal(["Loading Queue"], b.Loadings);

            b.Page.Hide(); // shown and then hidden: gone at once
            b.Settle();
            Assert.False(b.Shows);
        });
    }

    [Fact]
    public void A_frame_given_another_page_during_the_delay_never_shows_the_old_page_s_skeleton()
    {
        Bench.Run(b =>
        {
            b.Page.Loading();
            b.Page.Show();
            b.After(100);
            var next = new TestPage("Memory", "Live headroom.").Data();
            b.Page.Hide();
            b.Frame.DataContext = next;
            next.Show();
            b.After(500); // the old delay's time comes and goes
            Assert.False(b.Shows);
            Assert.Empty(b.Loadings);

            b.Page.Show(); // the old page, shown elsewhere, changes nothing in this frame
            b.After(500);
            Assert.False(b.Shows);
        });
    }

    [Fact]
    public void The_skeleton_takes_the_page_s_shape()
    {
        Bench.Run(b =>
        {
            b.Page.Loading();
            b.Page.Show();
            b.After(150);
            Assert.True(b.Shows);
            Assert.Equal(SkeletonShape.Table, b.Frame.Skeleton.Shape);
            Assert.Equal(7, ShellSupport.Under<Border>(b.Frame.Skeleton).Count(block => block.Height is 28 or 36)); // a header and six rows
        }, new TestPage { LoadingShape = SkeletonShape.Table });
    }

    [Fact]
    public void A_frame_shown_by_the_shell_hides_the_skeleton_when_the_window_goes_to_the_tray()
    {
        var clock = new ManualClock();
        TestPage first = null!;
        ShellSupport.InShell(null, (view, model, host) =>
        {
            view.PageHost.Clock = clock;
            first.Loading();
            PageRenderer.Settle(host);
            clock.Advance(150);
            PageRenderer.Settle(host);
            Assert.True(view.PageHost.Skeleton.Revealed);

            model.WindowHidden();
            PageRenderer.Settle(host);
            Assert.False(view.PageHost.Skeleton.Revealed);
            clock.Advance(1000);
            PageRenderer.Settle(host);
            Assert.False(view.PageHost.Skeleton.Revealed);

            model.WindowShown();
            PageRenderer.Settle(host);
            Assert.False(view.PageHost.Skeleton.Revealed); // its own 150 ms again
            clock.Advance(150);
            PageRenderer.Settle(host);
            Assert.True(view.PageHost.Skeleton.Revealed);
        }, make: () =>
        {
            var (shell, queue, _, _) = Shells.WithTestPages();
            first = queue;
            return shell;
        });
    }
}

using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Bosun.Panel.Accessibility;
using Xunit;

namespace Bosun.Panel.Tests.Helpers;

/// <summary>
/// <see cref="LiveRegion"/> on a real <see cref="TextBlock"/> in a window that is never shown (D02 T02 §2, after
/// Steward's LiveRegionTests): what it raises, seen through its seam, which stands where the event is raised, so a
/// test fails when nothing is raised any more. The seam is per thread and each test has a thread of its own. A
/// screen reader's side of it is heard from the shown window, in D02 T02 §10.
/// </summary>
public class LiveRegionTests
{
    /// <summary>What a raise said: the line's name and live setting, as its automation peer gives them.</summary>
    private sealed record Raised(string Name, AutomationLiveSetting Setting);

    /// <summary>Makes the line on the thread that will hold it, loads it in a window that is never shown, and runs the test there.</summary>
    private static void WithALine(Func<TextBlock> make, Action<TextBlock, List<Raised>, HwndSource> test) => Sta.Run(() =>
    {
        var line = make();
        var raised = new List<Raised>();
        LiveRegion.Raising = peer => raised.Add(new Raised(peer.GetName(), peer.GetLiveSetting()));
        try
        {
            LiveRegion.SetAnnounce(line, true);
            // WindowStyle 0 has no WS_VISIBLE: the line is loaded, and nothing appears.
            using var window = new HwndSource(new HwndSourceParameters("live-region-test") { WindowStyle = 0, Width = 300, Height = 100 });
            window.RootVisual = line;
            test(line, raised, window);
        }
        finally
        {
            LiveRegion.Raising = null;
        }
    });

    /// <summary>Runs everything queued at or above the announcement's own priority.</summary>
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [Fact]
    public void Text_already_shown_when_the_line_loads_is_announced_then_each_change_politely_by_its_new_name()
    {
        WithALine(() => new TextBlock { Text = "The queue could not be read." }, (line, raised, _) =>
        {
            Drain();
            Assert.Equal([new Raised("The queue could not be read.", AutomationLiveSetting.Polite)], raised);

            line.Text = "The queue was read.";
            Drain();
            Assert.Equal(2, raised.Count);
            Assert.Equal(new Raised("The queue was read.", AutomationLiveSetting.Polite), raised[1]);

            line.Text = "The queue was read."; // the same words again are not a change
            Drain();
            Assert.Equal(2, raised.Count);
        });
    }

    [Fact]
    public void A_line_that_asked_to_interrupt_keeps_its_own_live_setting()
    {
        WithALine(() =>
        {
            var urgent = new TextBlock { Text = "Memory pressure" };
            AutomationProperties.SetLiveSetting(urgent, AutomationLiveSetting.Assertive);
            return urgent;
        }, (_, raised, _) =>
        {
            Drain();
            Assert.Equal([new Raised("Memory pressure", AutomationLiveSetting.Assertive)], raised);
        });
    }

    [Fact]
    public void An_empty_line_announces_nothing_when_it_loads_or_when_it_is_cleared()
    {
        WithALine(() => new TextBlock(), (line, raised, _) =>
        {
            Drain();
            Assert.Empty(raised);

            line.Text = "Saved";
            Drain();
            line.Text = "";
            Drain();
            Assert.Equal(["Saved"], raised.Select(r => r.Name));
        });
    }

    [Fact]
    public void A_line_changed_and_then_cleared_before_the_announcement_ran_says_nothing()
    {
        WithALine(() => new TextBlock(), (line, raised, _) =>
        {
            Drain();
            line.Text = "Saving";
            line.Text = ""; // gone again before anything ran: there is nothing on screen to read
            Drain();
            Assert.Empty(raised);
        });
    }

    [Fact]
    public void A_line_changed_and_then_unloaded_before_the_announcement_ran_says_nothing_and_nothing_after()
    {
        WithALine(() => new TextBlock(), (line, raised, window) =>
        {
            Drain();
            line.Text = "Saving";
            window.RootVisual = null; // the page was left
            Drain();
            Assert.Empty(raised);

            line.Text = "Saved"; // nobody is looking at it
            Drain();
            Assert.Empty(raised);

            window.RootVisual = line; // back on screen, showing text: announced as a line that loads with text
            Drain();
            Assert.Equal(["Saved"], raised.Select(r => r.Name));
        });
    }

    [Fact]
    public void Two_changes_before_the_announcement_ran_say_the_latest_once()
    {
        WithALine(() => new TextBlock(), (line, raised, _) =>
        {
            Drain();
            line.Text = "1 waiting";
            line.Text = "2 waiting";
            Drain();
            Assert.Equal(["2 waiting"], raised.Select(r => r.Name));

            line.Text = "3 waiting"; // and the next change is announced again: nothing is left pending
            Drain();
            Assert.Equal(["2 waiting", "3 waiting"], raised.Select(r => r.Name));
        });
    }

    [Fact]
    public void A_line_switched_off_says_nothing_and_switched_on_twice_says_each_change_once()
    {
        WithALine(() => new TextBlock(), (line, raised, _) =>
        {
            Drain();
            LiveRegion.SetAnnounce(line, false);
            line.Text = "not announced";
            Drain();
            Assert.Empty(raised);

            LiveRegion.SetAnnounce(line, true);
            LiveRegion.SetAnnounce(line, false);
            LiveRegion.SetAnnounce(line, true);
            line.Text = "announced";
            Drain();
            Assert.Equal(["announced"], raised.Select(r => r.Name));
        });
    }

    [Fact]
    public void Without_the_seam_the_line_raises_through_its_real_peer_and_throws_nothing()
    {
        // No client is listening off screen, so nothing can be heard here: this proves the real path runs.
        Sta.Run(() =>
        {
            var line = new TextBlock { Text = "Saved" };
            LiveRegion.SetAnnounce(line, true);
            using var window = new HwndSource(new HwndSourceParameters("live-region-test") { WindowStyle = 0, Width = 300, Height = 100 });
            window.RootVisual = line;
            Drain();
            Assert.Equal(AutomationLiveSetting.Polite, UIElementAutomationPeer.CreatePeerForElement(line).GetLiveSetting());
            Assert.Equal("Saved", UIElementAutomationPeer.CreatePeerForElement(line).GetName());
        });
    }
}

using System.ComponentModel;
using System.Windows.Controls;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// A page and the frame it is drawn in (D02 T02 §4, design/SPEC.md §1): the header, and exactly one of the four
/// states, each with the words that make it that state.
/// </summary>
public class PageTests
{
    [Fact]
    public void A_page_moves_through_its_four_states_and_says_each_change()
    {
        Sta.Run(() =>
        {
            var page = new TestPage("Queue", "Who holds the build slots.");
            var changed = new List<string?>();
            ((INotifyPropertyChanged)page).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            Assert.Equal(PageState.Loading, page.State); // a page starts by loading
            Assert.Equal(("Queue", "Who holds the build slots."), (page.Title, page.Summary));

            page.Data();
            Assert.Equal(PageState.Data, page.State);
            page.Empty("Nothing is waiting.", "Start a build.");
            Assert.Equal((PageState.Empty, "Nothing is waiting.", "Start a build."), (page.State, page.EmptyReason, page.EmptyAction));
            page.Failed("bosun: no such file");
            Assert.Equal((PageState.Failed, "bosun: no such file"), (page.State, page.FailureMessage));
            Assert.NotNull(page.Retry);
            page.Loading();
            Assert.Equal(PageState.Loading, page.State);

            Assert.Equal(4, changed.Count(name => name == nameof(PageBase.State)));
            Assert.Contains(nameof(PageBase.EmptyReason), changed);
            Assert.Contains(nameof(PageBase.FailureMessage), changed);
        });
    }

    [Fact]
    public void An_empty_page_must_say_why_and_what_to_do_and_a_failed_one_must_quote_a_message_and_offer_retry()
    {
        Sta.Run(() =>
        {
            var page = new TestPage().Data();
            Assert.Throws<ArgumentException>(() => page.Empty("", "Start a build."));
            Assert.Throws<ArgumentException>(() => page.Empty("Nothing is waiting.", "  "));
            Assert.Throws<ArgumentException>(() => page.Failed(" "));
            Assert.Equal(PageState.Data, page.State); // a refused change leaves the page as it was

            Assert.Throws<InvalidOperationException>(new TestPage().FailedWithoutRetry);
            Assert.Throws<ArgumentException>(() => new TestPage(title: " "));
        });
    }

    [Fact]
    public void A_page_is_told_once_when_it_comes_on_screen_and_once_when_it_leaves()
    {
        Sta.Run(() =>
        {
            var page = new TestPage();
            Assert.False(page.IsShown);
            page.Show();
            page.Show();
            Assert.True(page.IsShown);
            Assert.Equal((1, 0), (page.ShownCount, page.HiddenCount));
            page.Hide();
            page.Hide();
            Assert.False(page.IsShown);
            Assert.Equal((1, 1), (page.ShownCount, page.HiddenCount));
        });
    }

    public static TheoryData<string> Themes => ["Dark", "Light"];

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_frame_draws_the_header_and_exactly_one_state_with_its_own_words(string theme)
    {
        TestPage page = null!;
        PageFrame frame = null!;
        PageRenderer.InPage(theme, () =>
        {
            page = new TestPage("Queue", "Who holds the build slots.");
            // The loading state is the skeleton, drawn at once here rather than after its 150 ms (D02 T03 §7), on a
            // page on screen, which is the only page it is drawn for.
            frame = new PageFrame { SkeletonDelay = TimeSpan.Zero, DataContext = page };
            TestPage.AddTemplate(frame);
            page.Show();
            return frame;
        }, host =>
        {
            const string loading = "Loading Queue", body = "two slots, one waiting", reason = "Nothing is waiting.", action = "Start a build with bosun run to see it here.";
            const string heading = "This did not work", message = "bosun: the queue file is damaged at line 3", retry = "Retry";
            string[] header = ["Queue", "Who holds the build slots."];

            Assert.Equal([.. header, loading], PageRenderer.TextsOf(frame));

            page.Data();
            PageRenderer.Settle(host);
            Assert.Equal([.. header, body], PageRenderer.TextsOf(frame)); // through the implicit template for the page's type
            Assert.Equal("TestPageBody", ShellSupport.Under<TextBlock>(frame).Single(text => text.Text == body).Name);

            page.Empty();
            PageRenderer.Settle(host);
            Assert.Equal([.. header, reason, action], PageRenderer.TextsOf(frame));

            page.Failed();
            PageRenderer.Settle(host);
            Assert.Equal([.. header, heading, message, retry], PageRenderer.TextsOf(frame));

            // Retry runs the page's retry, once.
            var button = ShellSupport.Under<Button>(frame).Single(b => b.IsVisible);
            Assert.Equal(retry, button.Content);
            ShellSupport.Invoke(button);
            Assert.True(StartupSupport.Pump(() => page.RetryCount == 1), "Retry never ran");
            PageRenderer.Settle(host);
            Assert.Equal(1, page.RetryCount);

            // The title is the page's heading to a screen reader.
            var title = ShellSupport.Under<TextBlock>(frame).First(text => text.Text == "Queue");
            Assert.Equal(System.Windows.Automation.AutomationHeadingLevel.Level1, System.Windows.Automation.AutomationProperties.GetHeadingLevel(title));
            Assert.Same(host.FindResource("F.Display"), title.FontFamily);
            Assert.Equal((double)host.FindResource("T.Title"), title.FontSize);
            return true;
        }, 900, 500);
    }

    [Fact]
    public void A_save_that_did_not_happen_is_said_on_the_page_until_one_does()
    {
        TestPage page = null!;
        PageFrame frame = null!;
        PageRenderer.InPage("Dark", () =>
        {
            page = new TestPage().Data();
            frame = new PageFrame { DataContext = page };
            TestPage.AddTemplate(frame);
            return frame;
        }, host =>
        {
            Assert.DoesNotContain("Not saved: the disk is full", PageRenderer.TextsOf(frame));
            page.SaveFailure = "Not saved: the disk is full";
            PageRenderer.Settle(host);
            Assert.Contains("Not saved: the disk is full", PageRenderer.TextsOf(frame));
            page.SaveFailure = null;
            PageRenderer.Settle(host);
            Assert.DoesNotContain("Not saved: the disk is full", PageRenderer.TextsOf(frame));
            return true;
        }, 900, 500);
    }
}

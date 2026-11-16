using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Helpers;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Theme;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The page frame's part of D02 T03 §7: current data stays on screen through a background refresh, a page change
/// fades in and rises through the application's motion gate (only on navigation, at once with motion off, ended when
/// motion is turned off or the page leaves the screen), the gate is installed as the application installs it, a value
/// that can't be read says so, and stale data says how old it is.
/// </summary>
public class PageFrameTests
{
    sealed class FakeAnimation(bool on) : IWindowsAnimation
    {
        public bool On { get; private set; } = on;

        public event Action? Changed;

        public void Set(bool on)
        {
            On = on;
            Changed?.Invoke();
        }
    }

    static ResourceDictionary Generated(string file)
    {
        using var stream = File.OpenRead(TestPaths.GeneratedTheme(file));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    /// <summary>Lets what the dispatcher holds run, as it would between frames.</summary>
    static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    // ---- data stays while it refreshes ----

    sealed class Driver : IRefreshDriver
    {
        public void Start(Action tick) { }

        public void Stop() { }
    }

    [Fact]
    public void A_background_refresh_keeps_the_data_drawn_and_never_shows_the_skeleton_then_draws_the_new_data()
    {
        var clock = new ManualClock();
        PageRenderer.InPage("Dark", () =>
        {
            var page = new TestPage { Body = "two slots, one waiting" }.Data();
            var frame = new PageFrame { Clock = clock, DataContext = page };
            TestPage.AddTemplate(frame);
            return frame;
        }, host =>
        {
            var frame = ShellSupport.Under<PageFrame>(host).Single();
            var page = (TestPage)frame.DataContext;
            var now = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
            var scheduler = new RefreshScheduler(() => now, new Driver());
            var ends = new List<TaskCompletionSource<string>>();
            page.OnLoad = _ =>
            {
                var end = new TaskCompletionSource<string>();
                ends.Add(end);
                return end.Task;
            };
            page.Polling(scheduler, Shells.Log(), TimeSpan.FromSeconds(5));
            page.Show(); // the first read starts, over data already drawn
            PageRenderer.Settle(host);

            for (var ms = 0; ms < 500; ms += 50) // a 500 ms read: the data is drawn throughout, and no skeleton
            {
                clock.Advance(50);
                PageRenderer.Settle(host);
                Assert.Equal(PageState.Data, page.State);
                Assert.False(frame.Skeleton.Revealed);
                Assert.Contains("two slots, one waiting", PageRenderer.TextsOf(frame));
            }
            ends[0].SetResult("two slots, none waiting");
            Drain();
            PageRenderer.Settle(host);
            Assert.Contains("two slots, none waiting", PageRenderer.TextsOf(frame));
            Assert.DoesNotContain("two slots, one waiting", PageRenderer.TextsOf(frame));

            now = now.AddSeconds(5); // the 5 s poll: the same again
            scheduler.Tick();
            clock.Advance(500);
            PageRenderer.Settle(host);
            Assert.False(frame.Skeleton.Revealed);
            Assert.Contains("two slots, none waiting", PageRenderer.TextsOf(frame));
            page.Hide();
            return true;
        });
    }

    // ---- a page change ----

    /// <summary>A shell of three test pages with a motion gate in its view's resources, and the test.</summary>
    static void WithGate(bool animates, Action<ShellView, ShellViewModel, Border, FakeAnimation> test) =>
        ShellSupport.InShell(null, (view, shell, host) =>
        {
            var windows = new FakeAnimation(animates);
            var theme = Generated("Dark.xaml");
            using var gate = new Motion(theme, windows);
            view.Resources[Motion.GateKey] = gate;
            test(view, shell, host, windows);
        }, make: () => Shells.WithTestPages().Shell);

    [Fact]
    public void Going_to_another_page_fades_it_in_and_raises_it_over_the_normal_length_and_ends_with_nothing_running()
    {
        WithGate(true, (view, shell, host, _) =>
        {
            var frame = view.PageHost;
            Assert.False(frame.IsMoving); // the first page is simply there

            shell.Show(shell.Sections[1]);
            Assert.True(frame.IsMoving);
            Assert.True(StartupSupport.Pump(() => !frame.IsMoving, TimeSpan.FromSeconds(3)), "the move never ended");
            Assert.Equal((1.0, 0.0), (frame.Body.Opacity, frame.Rise.Y));
            Assert.False(frame.Body.HasAnimatedProperties || frame.Rise.HasAnimatedProperties); // no clock left
        });
    }

    [Fact]
    public void A_change_of_the_same_page_s_state_or_data_does_not_move_it()
    {
        WithGate(true, (view, shell, host, _) =>
        {
            var page = (TestPage)shell.Page;
            page.Loading();
            page.Data();
            page.Body = "three slots";
            page.Empty();
            PageRenderer.Settle(host);
            Assert.False(view.PageHost.IsMoving);
        });
    }

    [Fact]
    public void With_motion_off_a_new_page_is_simply_there()
    {
        WithGate(false, (view, shell, host, _) =>
        {
            shell.Show(shell.Sections[1]);
            Assert.False(view.PageHost.IsMoving);
            Assert.Equal((1.0, 0.0), (view.PageHost.Body.Opacity, view.PageHost.Rise.Y));
        });
    }

    [Fact]
    public void Motion_turned_off_during_a_move_ends_it_at_once()
    {
        WithGate(true, (view, shell, host, windows) =>
        {
            shell.Show(shell.Sections[1]);
            Assert.True(view.PageHost.IsMoving);
            windows.Set(false);
            Assert.False(view.PageHost.IsMoving);
            Assert.Equal((1.0, 0.0), (view.PageHost.Body.Opacity, view.PageHost.Rise.Y));
        });
    }

    [Fact]
    public void A_page_that_leaves_the_screen_during_a_move_ends_it_and_is_simply_there_when_it_is_back()
    {
        WithGate(true, (view, shell, host, _) =>
        {
            shell.Show(shell.Sections[1]);
            Assert.True(view.PageHost.IsMoving);
            shell.WindowHidden(); // to the tray
            Assert.False(view.PageHost.IsMoving);
            shell.WindowShown();
            Assert.False(view.PageHost.IsMoving); // not a navigation: nothing moves
            Assert.Equal((1.0, 0.0), (view.PageHost.Body.Opacity, view.PageHost.Rise.Y));
        });
    }

    [Fact]
    public void A_frame_with_no_gate_changes_pages_at_once()
    {
        ShellSupport.InShell(null, (view, shell, host) =>
        {
            shell.Show(shell.Sections[1]);
            Assert.False(view.PageHost.IsMoving);
        }, make: () => Shells.WithTestPages().Shell);
    }

    // ---- the application's gate ----

    [Fact]
    public void The_application_s_gate_is_found_by_a_frame_listens_to_Windows_follows_a_change_and_stops_when_disposed()
    {
        Sta.Run(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(Generated("Light.xaml"));
            var on = true;
            var windows = new WindowsAnimation(() => on);
            var gate = Motion.Install(resources, windows);

            var frame = new PageFrame();
            var holder = new Border { Child = frame };
            holder.Resources = resources;
            Assert.Same(gate, frame.TryFindResource(Motion.GateKey));
            Assert.True(windows.Listening);
            Assert.True(gate.Animates);

            on = false;
            windows.PropertyChanged(nameof(SystemParameters.ClientAreaAnimation));
            Assert.False(gate.Animates);
            Assert.Equal(TimeSpan.Zero, gate.Normal.TimeSpan);

            gate.Dispose();
            Assert.False(windows.Listening);
            Assert.False(resources.Contains(Motion.GateKey));
            return 0;
        });
    }

    // ---- a value that can't be read ----

    [Fact]
    public void A_value_that_can_t_be_read_says_Not_available_and_why_and_never_0_or_nothing()
    {
        var texts = PageRenderer.InPage("Dark", () =>
        {
            var panel = new StackPanel();
            var unknown = new ContentControl { Content = null };
            unknown.SetResourceReference(FrameworkElement.StyleProperty, "Value");
            var stopped = new ContentControl { Content = new NotAvailable("WSL is stopped") };
            stopped.SetResourceReference(FrameworkElement.StyleProperty, "Value");
            panel.Children.Add(unknown);
            panel.Children.Add(stopped);
            return panel;
        }, host => PageRenderer.TextsOf(host));
        Assert.Equal(["Not available", "It could not be read.", "Not available", "WSL is stopped"], texts);
        Assert.Throws<ArgumentException>(() => new NotAvailable("  "));
    }

    // ---- stale data ----

    [Fact]
    public void Stale_data_says_its_age_again_on_each_refresh_on_showing_again_and_on_coming_back_and_goes_with_current_data()
    {
        var clock = new ManualClock();
        PageRenderer.InPage("Dark", () =>
        {
            var page = new TestPage("Instances", "Every dev instance.") { Clock = clock };
            var frame = new PageFrame { DataContext = page };
            TestPage.AddTemplate(frame);
            return frame;
        }, host =>
        {
            var frame = ShellSupport.Under<PageFrame>(host).Single();
            var page = (TestPage)frame.DataContext;
            var readAt = clock.GetUtcNow() - TimeSpan.FromMinutes(12);
            page.Show();
            page.LastKnown("WSL is stopped", readAt);
            PageRenderer.Settle(host);
            Assert.Equal(PageState.Data, page.State);
            Assert.Contains("WSL is stopped. Showing what was read 12 minutes ago.", PageRenderer.TextsOf(frame));
            Assert.Contains(page.Body, PageRenderer.TextsOf(frame)); // the data stays under it

            clock.Advance(TimeSpan.FromMinutes(3));
            page.LastKnown("WSL is stopped", readAt); // the next refresh, still stopped
            Assert.Equal("WSL is stopped. Showing what was read 15 minutes ago.", page.StaleNote);

            page.Hide();
            clock.Advance(TimeSpan.FromMinutes(5)); // no timer of its own while hidden
            Assert.Equal("WSL is stopped. Showing what was read 15 minutes ago.", page.StaleNote);
            page.Show();
            Assert.Equal("WSL is stopped. Showing what was read 20 minutes ago.", page.StaleNote);

            clock.Advance(TimeSpan.FromHours(1));
            page.ComeBack();
            Assert.Equal("WSL is stopped. Showing what was read 1 hour ago.", page.StaleNote);

            page.Data();
            PageRenderer.Settle(host);
            Assert.Null(page.StaleNote);
            Assert.DoesNotContain(PageRenderer.TextsOf(frame), t => t.Contains("Showing what was read", StringComparison.Ordinal));
            page.Hide();
            return true;
        });
    }

    [Theory]
    [InlineData(0, "less than a minute")]
    [InlineData(59, "less than a minute")]
    [InlineData(60, "1 minute")]
    [InlineData(12 * 60, "12 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(3 * 3600 + 59 * 60, "3 hours")]
    [InlineData(2 * 86400, "2 days")]
    public void An_age_is_said_in_words(int seconds, string words) => Assert.Equal(words, PageBase.Age(TimeSpan.FromSeconds(seconds)));
}

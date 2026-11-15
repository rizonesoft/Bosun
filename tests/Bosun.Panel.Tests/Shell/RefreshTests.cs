using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Tray;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// A page's refresh (D02 T02 §8, design/SPEC.md §1): it runs while the page is on screen and not otherwise, a
/// failure is the page's failed state and a later success takes it out again, the Refresh button runs it once, and
/// a panel that exits stops it. Over the tests' own page; time is a clock the test moves, except where the real
/// driver is what is proved.
/// </summary>
public class RefreshTests
{
    private static void OnUi(Action test) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        test();
    });

    /// <summary>A driver that only writes down whether it is ticking.</summary>
    private sealed class Driver : IRefreshDriver
    {
        public bool Ticking { get; private set; }

        public void Start(Action tick) => Ticking = true;

        public void Stop() => Ticking = false;
    }

    /// <summary>A shell whose first page polls, with a second page to go to, a clock, and each load ended by the test.</summary>
    private sealed class Bench
    {
        private readonly List<(TaskCompletionSource<string> Ends, CancellationToken Token)> _loads = [];

        public DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        public Driver Driver { get; } = new();
        public RefreshScheduler Scheduler { get; }
        public ActivityLog Log { get; } = Shells.Log();
        public TestPage Queue { get; }
        public TestPage Other { get; } = new TestPage("Memory", "Live headroom.").Data();
        public ShellViewModel Shell { get; }

        public Bench(bool windowShown = true)
        {
            Scheduler = new RefreshScheduler(() => Now, Driver);
            Queue = new TestPage("Queue", "Who holds the build slots.").Data();
            Queue.OnLoad = token =>
            {
                var ends = new TaskCompletionSource<string>();
                _loads.Add((ends, token));
                return ends.Task;
            };
            Queue.Polling(Scheduler, Log);
            Shell = new ShellViewModel(
            [
                new Section("queue", "Queue", Sections.Monitor, Queue),
                new Section("memory", "Memory", Sections.Monitor, Other),
            ], Log, null, windowShown);
        }

        public int Loads => _loads.Count;

        public bool Cancelled(int load) => _loads[load].Token.IsCancellationRequested;

        public void End(int load, string body)
        {
            _loads[load].Ends.SetResult(body);
            Drain();
        }

        public void Fail(int load, string message)
        {
            _loads[load].Ends.SetException(new InvalidOperationException(message));
            Drain();
        }

        public void After(double seconds)
        {
            Now = Now.AddSeconds(seconds);
            Scheduler.Tick();
            Drain();
        }

        /// <summary>
        /// A run's end comes back through the dispatcher, as it does in the panel: what it queued is let run before
        /// the test reads what it did.
        /// </summary>
        public static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

        public IReadOnlyList<string> Lines(LogSeverity severity)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            return [.. Log.Entries.Where(entry => entry.Severity == severity).Select(entry => entry.Message)];
        }
    }

    // ---- on screen, and not ----

    [Fact]
    public void A_page_refreshes_when_it_is_shown_and_at_its_interval_and_makes_no_call_while_another_page_is_shown()
    {
        OnUi(() =>
        {
            var b = new Bench();
            Assert.Equal(1, b.Loads); // shown, so read at once
            Assert.True(b.Driver.Ticking);
            b.End(0, "two slots, none waiting");
            Assert.Equal((PageState.Data, "two slots, none waiting"), (b.Queue.State, b.Queue.Body));

            b.After(5);
            Assert.Equal(2, b.Loads);
            b.End(1, "two slots, one waiting");
            Assert.Equal("two slots, one waiting", b.Queue.Body);

            b.Shell.Show(b.Shell.Sections[1]); // another page: this one is hidden
            Assert.False(b.Driver.Ticking);    // and with nothing to refresh, nothing ticks
            for (var i = 0; i < 12; i++) b.After(5);
            Assert.Equal(2, b.Loads);

            b.Shell.Show(b.Shell.Sections[0]); // back: read once, at once
            Assert.Equal(3, b.Loads);
            Assert.True(b.Driver.Ticking);
        });
    }

    [Fact]
    public void A_page_in_a_window_that_is_hidden_to_the_tray_or_minimised_makes_no_call_and_refreshes_once_when_it_is_back()
    {
        // The plan review: a minimised window never told the shell, so its page went on polling behind it.
        OnUi(() =>
        {
            var b = new Bench();
            b.End(0, "one");

            b.Shell.WindowHidden(); // closed to the tray
            for (var i = 0; i < 12; i++) b.After(5);
            Assert.Equal(1, b.Loads);
            b.Shell.WindowShown();
            Assert.Equal(2, b.Loads);
            b.End(1, "two");

            var window = new FakeWindow();
            window.Show();
            WindowActivation.TellShell(b.Shell, window); // on screen: nothing changes
            Assert.Equal(2, b.Loads);

            window.Minimise();
            WindowActivation.TellShell(b.Shell, window);
            Assert.False(b.Queue.IsShown);
            for (var i = 0; i < 12; i++) b.After(5);
            Assert.Equal(2, b.Loads);

            window.Restore();
            WindowActivation.TellShell(b.Shell, window);
            Assert.True(b.Queue.IsShown);
            Assert.Equal(3, b.Loads); // once
            WindowActivation.TellShell(b.Shell, window);
            Assert.Equal(3, b.Loads);

            window.Hide();
            WindowActivation.TellShell(b.Shell, window);
            Assert.False(b.Queue.IsShown);
        });
    }

    [Fact]
    public void A_shell_made_for_a_start_in_the_tray_refreshes_nothing_until_its_window_is_opened()
    {
        // The plan review: the shell showed its first page as it was made, before the tray kept the window hidden.
        OnUi(() =>
        {
            var b = new Bench(windowShown: false);
            Assert.Equal((0, false, 0), (b.Loads, b.Queue.IsShown, b.Queue.ShownCount));
            Assert.False(b.Driver.Ticking);
            for (var i = 0; i < 12; i++) b.After(5);
            Assert.Equal(0, b.Loads);

            b.Shell.WindowShown();
            Assert.Equal((1, true), (b.Loads, b.Queue.IsShown));

            // The panel's own shell, made the same way.
            var f = new Settings.SettingsFixture();
            var store = new PanelSettingsStore(Settings.SettingsFixture.FilePath, f.Files);
            var shell = WindowStart.NewShell(Dispatcher.CurrentDispatcher, store, new StartupEntry(f.Key, Settings.SettingsFixture.Program), () => { }, windowShown: false);
            Assert.False(shell.Page.IsShown);
            shell.WindowShown();
            Assert.True(shell.Page.IsShown);
        });
    }

    [Fact]
    public void A_run_that_was_stopped_never_changes_what_the_page_shows()
    {
        OnUi(() =>
        {
            var b = new Bench();
            b.End(0, "current");

            b.After(5); // a poll under way
            b.Shell.Show(b.Shell.Sections[1]);
            Assert.True(b.Cancelled(1));
            b.End(1, "read after the page was hidden"); // the tool ignored its cancellation
            Bench.Drain();
            Assert.Equal((PageState.Data, "current"), (b.Queue.State, b.Queue.Body));

            b.After(5);
            b.Shell.Show(b.Shell.Sections[0]); // back: a fresh read, and that is what shows
            Assert.Equal(3, b.Loads);
            b.Shell.Show(b.Shell.Sections[1]);
            b.Fail(2, "bosun: interrupted"); // a stale failure is no failure of the page
            Assert.Equal((PageState.Data, "current"), (b.Queue.State, b.Queue.Body));
            Assert.Empty(b.Lines(LogSeverity.Warn));
        });
    }

    // ---- failing, and working again ----

    [Fact]
    public void A_failed_refresh_is_the_pages_failed_state_said_once_and_the_next_one_that_works_brings_the_page_back()
    {
        OnUi(() =>
        {
            var b = new Bench();
            b.End(0, "two slots");

            b.After(5);
            b.Fail(1, "bosun: the queue file is damaged at line 3");
            Assert.Equal((PageState.Failed, "bosun: the queue file is damaged at line 3"), (b.Queue.State, b.Queue.FailureMessage));
            Assert.Same(b.Queue.RefreshNow, b.Queue.Retry);
            Assert.Equal(["Queue could not be refreshed: bosun: the queue file is damaged at line 3 Trying again in 10 s, and less often while it keeps failing."], b.Lines(LogSeverity.Warn));

            b.After(9);
            Assert.Equal(2, b.Loads); // backed off
            b.After(1);
            b.Fail(2, "bosun: the queue file is damaged at line 3");
            Assert.Single(b.Lines(LogSeverity.Warn)); // still failing: not said again

            b.After(20); // and by itself, when the tool works again
            b.End(3, "two slots, one waiting");
            Assert.Equal((PageState.Data, "two slots, one waiting"), (b.Queue.State, b.Queue.Body));
            Assert.Equal(["Queue is refreshing again"], b.Lines(LogSeverity.Info));
            b.After(5);
            Assert.Equal(5, b.Loads); // back at its interval
            b.End(4, "two slots, one waiting");
            Assert.Single(b.Lines(LogSeverity.Info));
        });
    }

    [Fact]
    public void Retry_runs_the_refresh_once_and_a_retry_that_works_brings_the_page_back()
    {
        OnUi(() =>
        {
            var b = new Bench();
            b.Fail(0, "wsl: the distribution is not running");
            Assert.Equal(PageState.Failed, b.Queue.State);

            b.Queue.Retry!.Execute(null);
            Assert.Equal(2, b.Loads); // once, now, not at the backed-off time
            b.Fail(1, "wsl: the distribution is not running");
            Assert.True(StartupSupport.Pump(() => b.Lines(LogSeverity.Fail).Count == 1));
            Assert.Equal("Refresh Queue: wsl: the distribution is not running", b.Lines(LogSeverity.Fail)[0]);
            Assert.Single(b.Lines(LogSeverity.Warn));
            Assert.True(StartupSupport.Pump(() => b.Queue.Retry!.CanExecute(null)));

            b.Queue.Retry!.Execute(null);
            Assert.Equal(3, b.Loads);
            b.End(2, "two slots");
            Assert.True(StartupSupport.Pump(() => b.Queue.State == PageState.Data));
            Assert.Equal("two slots", b.Queue.Body);
            Assert.True(StartupSupport.Pump(() => b.Lines(LogSeverity.Info).Contains("Refresh Queue")));
            Assert.Contains("Queue is refreshing again", b.Lines(LogSeverity.Info));
        });
    }

    [Fact]
    public void A_page_has_one_refresh()
    {
        OnUi(() =>
        {
            var b = new Bench();
            Assert.Throws<InvalidOperationException>(() => b.Queue.Polling(b.Scheduler, b.Log));
            Assert.Null(b.Other.RefreshNow); // and a page with none has no command
        });
    }

    // ---- the Refresh button ----

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_refresh_button_makes_one_extra_call_joins_a_poll_under_way_and_each_press_is_a_line(string theme)
    {
        Bench b = null!;
        PageFrame frame = null!;
        PageRenderer.InPage(theme, () =>
        {
            b = new Bench();
            frame = new PageFrame { DataContext = b.Queue };
            TestPage.AddTemplate(frame);
            return frame;
        }, host =>
        {
            var button = ShellSupport.Under<Button>(frame).Single(found => found.Name == "Refresh");
            Assert.True(button.IsVisible);
            Assert.Equal("Refresh", button.Content);
            Assert.Equal("Refresh", AutomationProperties.GetName(button));
            Assert.Equal("Reads this page's data again now", button.ToolTip);
            Assert.Same(host.FindResource("Btn"), button.Style);
            Assert.True(button.ActualHeight >= 32, $"{button.ActualHeight} px high"); // design/SPEC.md §1: targets of 32 px or more
            b.End(0, "two slots");

            PageRenderer.Settle(host);
            ShellSupport.Invoke(button); // between two polls: one extra call
            Assert.True(StartupSupport.Pump(() => b.Loads == 2), "the press did not run the refresh"); // a press through UI Automation arrives through the dispatcher
            Bench.Drain();
            Assert.Equal(2, b.Loads);
            b.End(1, "two slots, one waiting");
            Assert.True(StartupSupport.Pump(() => b.Lines(LogSeverity.Info).Count == 1));
            Assert.Equal("two slots, one waiting", b.Queue.Body);

            b.After(5); // a poll under way
            Assert.Equal(3, b.Loads);
            PageRenderer.Settle(host);
            ShellSupport.Invoke(button); // joins it: no second call
            Assert.True(StartupSupport.Pump(() => ((Bosun.Panel.Mvvm.AsyncCommand)b.Queue.RefreshNow!).IsRunning), "the press did not arrive");
            Assert.Equal(3, b.Loads);
            b.End(2, "three slots");
            Assert.True(StartupSupport.Pump(() => b.Lines(LogSeverity.Info).Count == 2));
            Assert.Equal(["Refresh Queue", "Refresh Queue"], b.Lines(LogSeverity.Info));
            Assert.Equal(b.Now.AddSeconds(5), b.Scheduler.Refreshes.Single().NextDue); // the poll's time, kept
            return true;
        }, 900, 400);
    }

    [Fact]
    public void A_page_with_no_refresh_has_no_refresh_button()
    {
        PageFrame frame = null!;
        PageRenderer.InPage("Dark", () =>
        {
            frame = new PageFrame { DataContext = new TestPage().Data() };
            TestPage.AddTemplate(frame);
            return frame;
        }, _ =>
        {
            Assert.False(ShellSupport.Under<Button>(frame).Single(found => found.Name == "Refresh").IsVisible);
            Assert.DoesNotContain("Refresh", PageRenderer.TextsOf(frame));
            return true;
        }, 900, 400);
    }

    // ---- the real driver ----

    [Fact]
    public void The_real_driver_ticks_the_scheduler_on_the_dispatcher_and_is_off_while_nothing_is_active()
    {
        // The plan review: ticks by hand prove the scheduler, not that anything ticks it in the panel.
        OnUi(() =>
        {
            var ui = Environment.CurrentManagedThreadId;
            var driver = new DispatcherRefreshDriver(Dispatcher.CurrentDispatcher, TimeSpan.FromMilliseconds(15));
            var scheduler = new RefreshScheduler(driver: driver); // the real clock
            var log = Shells.Log();
            var loads = 0;
            var changedOn = new List<int>();
            var page = new TestPage("Queue", "Who holds the build slots.").Data();
            page.OnLoad = async _ =>
            {
                var load = ++loads;
                await Task.Run(() => Thread.Sleep(5)); // the tool is read on another thread
                return $"read {load}";
            };
            page.PropertyChanged += (_, _) => changedOn.Add(Environment.CurrentManagedThreadId);
            page.Polling(scheduler, log, TimeSpan.FromMilliseconds(40));
            Assert.False(driver.IsTicking); // not on screen yet: the timer is off

            var other = new TestPage("Memory", "Live headroom.").Data();
            var shell = new ShellViewModel([new Section("queue", "Queue", Sections.Monitor, page), new Section("memory", "Memory", Sections.Monitor, other)], log);
            Assert.True(driver.IsTicking);

            Assert.True(StartupSupport.Pump(() => loads >= 4 && page.Body.StartsWith("read ", StringComparison.Ordinal)), "the driver did not tick the scheduler");
            Assert.NotEmpty(changedOn);
            Assert.All(changedOn, thread => Assert.Equal(ui, thread)); // the page changes on the UI thread

            shell.Show(shell.Sections[1]);
            Assert.False(driver.IsTicking);
            StartupSupport.Pump(() => !scheduler.Refreshes[0].IsRunning);
            var after = loads;
            StartupSupport.Pump(() => false, TimeSpan.FromMilliseconds(250));
            Assert.Equal(after, loads); // and nothing is read behind the hidden page

            scheduler.Dispose();
            shell.Show(shell.Sections[0]);
            Assert.False(driver.IsTicking); // a stopped scheduler starts nothing
        });
    }

    // ---- the panel exiting ----

    [Fact]
    public void Exit_through_the_tray_stops_every_refresh_and_an_exit_that_is_refused_stops_nothing()
    {
        OnUi(() =>
        {
            var now = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
            var driver = new Driver();
            var scheduler = new RefreshScheduler(() => now, driver);
            var tokens = new List<CancellationToken>();
            var pending = new TaskCompletionSource();
            var check = scheduler.Register("Memory pressure check", RefreshScheduler.Slow, token =>
            {
                tokens.Add(token);
                return tokens.Count == 1 ? Task.CompletedTask : pending.Task;
            });
            var f = new TrayFixture(refreshes: scheduler).Start(inTray: true);
            check.Start(); // the check that runs in the tray belongs to no page
            Assert.Single(tokens);

            void Tick()
            {
                now = now.AddSeconds(30);
                scheduler.Tick();
            }

            // Refused three ways: the person stays, the save fails, the question can't be put. Nothing is stopped.
            f.Overview.Edits = "typed";
            f.Answer(LeaveChoice.Stay);
            f.Controller.ExitAsync().Wait();
            f.Answer(LeaveChoice.Save);
            f.Overview.OnSave = () => Task.FromResult(ActionOutcome.Failed("the disk is full"));
            f.Controller.ExitAsync().Wait();
            f.Shell.AskToLeave = _ => throw new InvalidOperationException("the dialog could not be shown");
            f.Controller.ExitAsync().Wait();
            Assert.Empty(f.Exits);
            Assert.Equal((false, true, true), (scheduler.IsStopped, check.IsActive, driver.Ticking));
            Tick();
            Assert.Equal(2, tokens.Count); // still going, and now under way

            f.Answer(LeaveChoice.Discard);
            f.Controller.ExitAsync().Wait();

            Assert.Equal([0], f.Exits);
            Assert.True(scheduler.IsStopped);
            Assert.True(tokens[1].IsCancellationRequested); // work under way is cancelled
            Assert.Equal((false, false), (check.IsActive, driver.Ticking));
            pending.SetCanceled();
            check.Start();
            Tick();
            Assert.Equal(2, tokens.Count); // and nothing starts after
        });
    }
}

/// <summary>Rules over the application's own source (D02 T02 §8): one scheduler, ticked by the dispatcher, stopped on exit.</summary>
public class RefreshRulesTests
{
    [Fact]
    public void The_application_makes_the_one_scheduler_with_the_dispatchers_driver_hands_it_to_the_tray_and_stops_it_on_exit()
    {
        var app = File.ReadAllText(Path.Combine(TestPaths.Panel(), "App.xaml.cs"));
        Assert.Equal(1, Count(app, "new RefreshScheduler("));
        Assert.Contains("_refreshes = new RefreshScheduler(driver: new DispatcherRefreshDriver(Dispatcher));", app, StringComparison.Ordinal);
        Assert.Contains("refreshes: refreshes)", app, StringComparison.Ordinal);

        var exit = app[app.IndexOf("protected override void OnExit", StringComparison.Ordinal)..];
        Assert.Contains("_refreshes?.Dispose()", exit[..exit.IndexOf('}')], StringComparison.Ordinal);

        // No timer of a page's own: every refresh goes through the scheduler.
        var strays = Directory.EnumerateFiles(TestPaths.Panel(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(file => Path.GetFileName(file) != "DispatcherRefreshDriver.cs")
            .Where(file => File.ReadAllText(file).Contains("new DispatcherTimer(", StringComparison.Ordinal) || File.ReadAllText(file).Contains("new System.Timers.Timer(", StringComparison.Ordinal))
            .Select(Path.GetFileName);
        Assert.Empty(strays);
    }

    [Fact]
    public void The_window_tells_the_shell_when_it_is_minimised_and_when_it_is_back()
    {
        var window = File.ReadAllText(Path.Combine(TestPaths.Panel(), "Shell", "MainWindow.xaml.cs"));
        var state = window[window.IndexOf("private void ShowState()", StringComparison.Ordinal)..];
        Assert.Contains("WindowActivation.TellShell(shell, this)", state[..state.IndexOf("Shell.SetMaximised", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("StateChanged += (_, _) => ShowState();", window, StringComparison.Ordinal);
    }

    private static int Count(string text, string what)
    {
        var (count, at) = (0, 0);
        while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += what.Length;
        }
        return count;
    }
}

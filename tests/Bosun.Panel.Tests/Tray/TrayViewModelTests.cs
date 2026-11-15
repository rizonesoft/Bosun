using System.IO;
using System.Windows.Threading;
using Bosun.Panel.Shell;
using Bosun.Panel.Tray;
using Xunit;

namespace Bosun.Panel.Tests.Tray;

/// <summary>
/// The tray's state, words and menu (D02 T02 §7, design/SPEC.md §17), and the one way a notification is raised.
/// Nothing here touches Windows' tray.
/// </summary>
public class TrayViewModelTests
{
    internal static void OnUi(Action test) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        test();
    });

    [Fact]
    public void Each_state_has_its_icon_and_says_itself_in_words_and_pressure_with_something_waiting_says_both()
    {
        OnUi(() =>
        {
            var f = new TrayFixture();
            var tray = f.Controller.Tray;
            var changes = 0;
            tray.Changed += () => changes++;

            Assert.Equal((TrayState.Normal, "Bosun Panel"), (tray.State, tray.Tooltip));

            f.Needs.Count = 1; // the source says so, and the tray follows
            Assert.Equal(1, changes);
            Assert.Equal((TrayState.NeedsYou, "Bosun Panel: 1 thing needs you"), (tray.State, tray.Tooltip));
            f.Needs.Count = 3;
            Assert.Equal((TrayState.NeedsYou, "Bosun Panel: 3 things need you"), (tray.State, tray.Tooltip));

            tray.Pressure = true; // the more urgent of the two takes the icon, and the words keep both
            Assert.Equal((TrayState.Pressure, "Bosun Panel: memory pressure, and 3 things need you"), (tray.State, tray.Tooltip));
            f.Needs.Count = 0;
            Assert.Equal((TrayState.Pressure, "Bosun Panel: memory pressure"), (tray.State, tray.Tooltip));
            tray.Pressure = true; // said again: nothing changed
            Assert.Equal(4, changes);
            tray.Pressure = false;
            Assert.Equal((TrayState.Normal, "Bosun Panel", 5), (tray.State, tray.Tooltip, changes));
        });
    }

    [Theory]
    [InlineData(TrayState.Normal, "tray-normal.ico")]
    [InlineData(TrayState.Pressure, "tray-pressure.ico")]
    [InlineData(TrayState.NeedsYou, "tray-needs.ico")]
    public void Each_state_draws_its_own_icon_file_and_the_panel_holds_that_file(TrayState state, string file)
    {
        Assert.Equal(file, TrayViewModel.IconFile(state));
        using var held = typeof(TrayViewModel).Assembly.GetManifestResourceStream("Bosun.Panel.Tray." + file);
        Assert.NotNull(held);
        using var bytes = new MemoryStream();
        held.CopyTo(bytes);
        Assert.Equal(File.ReadAllBytes(Path.Combine(TestPaths.Repo(), "resources", file)), bytes.ToArray());
    }

    [Fact]
    public void The_menu_is_open_the_queue_in_words_pause_and_exit_and_each_line_does_its_work()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start(inTray: true);
            var tray = f.Controller.Tray;

            // No queue to read yet: Not available, never an empty queue.
            Assert.Equal(["Open", "Queue: Not available", "Pause notifications for 1 hour", "Exit"], tray.Menu().Select(item => item.Text));
            Assert.Equal([TrayViewModel.Open, TrayViewModel.Queue, TrayViewModel.Pause, TrayViewModel.Exit], tray.Menu().Select(item => item.Id));
            f.QueueWords.Words = "1 running, 2 waiting";
            Assert.Equal("Queue: 1 running, 2 waiting", tray.Menu()[1].Text);

            Assert.False(f.Window.IsVisible);
            f.Icon.Choose(TrayViewModel.Open).Wait();
            Assert.Equal(["show", "activate"], f.Window.Calls);
            Assert.Same(f.Overview, f.Shell.Page);

            f.Window.Calls.Clear();
            f.Icon.Choose(TrayViewModel.Queue).Wait();
            Assert.Equal(["activate"], f.Window.Calls); // brought forward
            Assert.Same(f.Queue, f.Shell.Page);         // and the Queue page shown

            f.Icon.Choose(TrayViewModel.Pause).Wait();
            Assert.Equal(f.Now.AddHours(1), f.Notifier.PausedUntil);
            Assert.Equal("Resume notifications (paused until 10:10)", tray.Menu()[2].Text);
            f.Icon.Choose(TrayViewModel.Pause).Wait(); // the same line is now Resume
            Assert.Null(f.Notifier.PausedUntil);
            Assert.Equal("Pause notifications for 1 hour", tray.Menu()[2].Text);
            // Review round 2: both are actions, and every action is a line in the Activity pane (design/SPEC.md §1).
            Assert.Equal(["Pause notifications until 10:10", "Resume notifications"], f.Lines(LogSeverity.Info));

            Assert.Empty(f.Exits);
            f.Icon.Choose(TrayViewModel.Exit).Wait();
            Assert.Equal([0], f.Exits);
        });
    }

    [Fact]
    public void A_pause_that_has_run_out_is_over_and_the_menu_offers_a_new_hour()
    {
        // The plan review: only Resume put the menu's words back, so an expired pause still read as Resume.
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Icon.Choose(TrayViewModel.Pause).Wait();
            f.Now = f.Now.AddMinutes(59);
            Assert.StartsWith("Resume notifications", f.Controller.Tray.Menu()[2].Text);

            f.Now = f.Now.AddMinutes(1); // the hour is up
            Assert.Null(f.Notifier.PausedUntil);
            Assert.Equal("Pause notifications for 1 hour", f.Controller.Tray.Menu()[2].Text);

            f.Icon.Choose(TrayViewModel.Pause).Wait(); // and choosing it starts a new hour from now, not a resume
            Assert.Equal(f.Now.AddHours(1), f.Notifier.PausedUntil);
            Assert.Equal("Resume notifications (paused until 11:10)", f.Controller.Tray.Menu()[2].Text);
        });
    }

    // ---- notifications ----

    [Fact]
    public void A_type_that_is_turned_off_raises_nothing_and_says_so_in_the_pane_and_raises_again_once_turned_on()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            Assert.True(f.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "4 GB free of 64 GB"));
            Assert.Equal([("Memory pressure", "4 GB free of 64 GB")], f.Icon.Notifications);

            Assert.True(f.Store.Update(current => current with { NotifyMemoryPressure = false }).Succeeded);
            Assert.False(f.Notifier.IsOn(NotificationType.MemoryPressure));
            Assert.False(f.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "3 GB free of 64 GB"));
            Assert.Single(f.Icon.Notifications); // nothing raised

            Assert.True(f.Store.Update(current => current with { NotifyMemoryPressure = true }).Succeeded);
            Assert.True(f.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "2 GB free of 64 GB"));
            Assert.Equal(2, f.Icon.Notifications.Count);

            // Each one is a line in the Activity pane, raised or not, saying which.
            Assert.Equal(
            [
                "Memory pressure: 4 GB free of 64 GB",
                "Memory pressure: 3 GB free of 64 GB (not notified: memory pressure notifications are turned off)",
                "Memory pressure: 2 GB free of 64 GB",
            ], f.Lines(LogSeverity.Warn));
        });
    }

    [Fact]
    public void A_paused_notifier_raises_nothing_until_its_hour_is_up_and_raises_again_after()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Notifier.PauseForAnHour();

            Assert.False(f.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "first"));
            f.Now = f.Now.AddMinutes(59).AddSeconds(59);
            Assert.False(f.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "second"));
            Assert.Empty(f.Icon.Notifications);

            f.Now = f.Now.AddSeconds(1);
            Assert.True(f.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "third"));
            Assert.Equal([("Memory pressure", "third")], f.Icon.Notifications);
            Assert.Equal(
            [
                "Memory pressure: first (not notified: notifications are paused until 10:10)",
                "Memory pressure: second (not notified: notifications are paused until 10:10)",
                "Memory pressure: third",
            ], f.Lines(LogSeverity.Warn));
        });
    }

    [Fact]
    public void A_notification_windows_does_not_show_is_still_a_line_in_the_pane_saying_so()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Icon.FailNotify = true;
            Assert.False(f.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "4 GB free"));
            Assert.Equal(["Memory pressure: 4 GB free (not notified: Windows did not show it)"], f.Lines(LogSeverity.Warn));

            // And with no tray at all there is nowhere to show one.
            var bare = new TrayFixture();
            bare.Icon.Refuse = true;
            bare.Start();
            Assert.False(bare.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "4 GB free"));
            Assert.Contains("Memory pressure: 4 GB free (not notified: Windows did not show it)", bare.Lines(LogSeverity.Warn));
        });
    }
}

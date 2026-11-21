using System.IO;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Shell;
using Bosun.Panel.Tray;
using Xunit;

namespace Bosun.Panel.Tests.Tray;

/// <summary>
/// The panel living in the tray (D02 T02 §7): started with and without <c>--tray</c>, closed to the tray with a
/// word said once, left through Exit with the question about unsaved edits, and never out of sight when Windows
/// refuses the icon. Over a window, an icon and settings of the tests' own.
/// </summary>
public class TrayControllerTests
{
    private static void OnUi(Action test) => TrayViewModelTests.OnUi(test);

    // ---- starting ----

    [Fact]
    public void Started_as_usual_the_icon_is_made_and_the_window_and_its_page_are_shown()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            Assert.True(f.Controller.HasTray);
            Assert.Equal((true, TrayState.Normal, "Bosun Panel: normal"), (f.Icon.Installed, f.Icon.State, f.Icon.Tooltip));
            Assert.True(f.Window.IsVisible);
            Assert.Equal((true, 1, 0), (f.Overview.IsShown, f.Overview.ShownCount, f.Overview.HiddenCount));

            f.Controller.Tray.Pressure = true; // the state is the icon's from then on
            Assert.Equal((TrayState.Pressure, "Bosun Panel: memory pressure"), (f.Icon.State, f.Icon.Tooltip));
        });
    }

    [Fact]
    public void Started_without_activation_the_window_is_shown_and_left_behind_and_a_later_open_brings_it_forward()
    {
        // D02 T02 §12: a start asking that the window not be activated (LaunchShow) shows it without taking the
        // foreground; the person opening it later from the tray still brings it forward.
        OnUi(() =>
        {
            var f = new TrayFixture().Start(activate: false);
            Assert.Equal(["show"], f.Window.Calls);
            Assert.True(f.Overview.IsShown);
            f.Controller.Open();
            Assert.Equal(["show", "activate"], f.Window.Calls);
        });
    }

    [Fact]
    public void Started_for_the_tray_there_is_an_icon_and_no_window_until_it_is_asked_for_and_the_page_is_hidden_with_it()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start(inTray: true);
            Assert.True(f.Icon.Installed);
            Assert.Empty(f.Window.Calls); // never shown
            Assert.Equal((false, 1), (f.Overview.IsShown, f.Overview.HiddenCount)); // told once

            f.Icon.Choose(TrayViewModel.Open).Wait(); // the menu's Open
            Assert.True(f.Window.IsVisible);
            Assert.Equal((true, 2, 1), (f.Overview.IsShown, f.Overview.ShownCount, f.Overview.HiddenCount));

            Assert.True(f.Controller.TakeClose()); // closed to the tray: the page goes with the window
            Assert.False(f.Window.IsVisible);
            Assert.Equal((false, 2), (f.Overview.IsShown, f.Overview.HiddenCount));

            f.Icon.Click(); // a click on the icon
            Assert.True(f.Window.IsVisible);
            Assert.Equal((true, 3), (f.Overview.IsShown, f.Overview.ShownCount));
            f.Icon.Click(); // already there: brought forward, and the page is not told again
            Assert.Equal((3, 2), (f.Overview.ShownCount, f.Overview.HiddenCount));

            Assert.True(f.Controller.TakeClose());
            f.Controller.Open(); // a second launch's activation
            Assert.True(f.Window.IsVisible);
            Assert.Equal((true, 4, 3), (f.Overview.IsShown, f.Overview.ShownCount, f.Overview.HiddenCount));
        });
    }

    [Fact]
    public void A_page_chosen_while_the_window_is_hidden_is_shown_only_when_the_window_is()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start(inTray: true);
            f.Shell.Show(f.Section("queue"));
            Assert.Equal((false, 0), (f.Queue.IsShown, f.Queue.ShownCount));
            Assert.Equal(1, f.Overview.HiddenCount); // not told a second time that it is hidden

            f.Controller.Open();
            Assert.Equal((true, 1), (f.Queue.IsShown, f.Queue.ShownCount));
            Assert.False(f.Overview.IsShown);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_icon_windows_refuses_leaves_no_tray_so_the_window_is_shown_and_its_close_asks_and_exits(bool inTray)
    {
        // The plan review: with --tray the window stayed hidden, and with no icon nothing could bring it back.
        OnUi(() =>
        {
            var f = new TrayFixture();
            f.Icon.Refuse = true;
            f.Start(inTray);

            Assert.False(f.Controller.HasTray);
            Assert.True(f.Window.IsVisible); // shown after all
            Assert.Equal((true, 0), (f.Overview.IsShown, f.Overview.HiddenCount));
            Assert.Equal(["Windows did not add Bosun Panel's icon to the notification area, so closing the window exits the panel."], f.Lines(LogSeverity.Warn));

            // The close is the window's own, as before there was a tray: asked about, and then really closed.
            Assert.False(f.Controller.TakeClose());
            Assert.True(f.Window.IsVisible);
            var guard = new ExitGuard(f.Shell, f.Window);
            f.Window.Closing = guard.MayCloseNow;
            f.Overview.Edits = "typed";
            f.Answer(LeaveChoice.Stay);
            f.Window.Close();
            Assert.Equal(0, f.Window.Closed);
            f.Answer(LeaveChoice.Discard);
            f.Window.Close();
            Assert.Equal(1, f.Window.Closed);
            Assert.Empty(f.Icon.Notifications); // and nothing was said about a tray there is not
        });
    }

    [Theory]
    [InlineData(true, "--tray")]
    [InlineData(true, "C:\\src\\Bosun\\Bosun.Panel.exe", "--TRAY")]
    [InlineData(false, "C:\\src\\Bosun\\Bosun.Panel.exe")]
    [InlineData(false, "--trayed")]
    [InlineData(false)]
    public void The_tray_argument_is_what_windows_starts_the_panel_with(bool inTray, params string[] arguments)
    {
        Assert.Equal(inTray, TrayController.StartsInTray(arguments));
        Assert.Equal("--tray", TrayController.TrayArgument);
        // The Run value passes the same word (D02 T02 §5).
        Assert.EndsWith(" " + TrayController.TrayArgument, new Bosun.Core.StartupEntry(new Settings.FakeRunKey(), "C:\\src\\Bosun\\Bosun.Panel.exe").Command);
    }

    // ---- closing the window ----

    [Fact]
    public void Closing_the_window_hides_it_and_says_once_ever_that_the_panel_is_still_running()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Overview.Edits = "typed"; // nothing is lost by hiding, so nothing is asked
            f.Shell.AskToLeave = _ => throw new InvalidOperationException("closing to the tray asks nothing");

            Assert.True(f.Controller.TakeClose());
            Assert.Equal(["show", "activate", "hide"], f.Window.Calls); // hidden, not closed
            Assert.Equal(0, f.Window.Closed);
            Assert.Equal([(TrayController.StillRunningTitle, TrayController.StillRunningText)], f.Icon.Notifications);
            Assert.Equal("Bosun Panel is still running", TrayController.StillRunningTitle);
            Assert.Contains("choose Exit", TrayController.StillRunningText);
            Assert.True(f.Store.Current.TrayNoticeSaid);
            Assert.Equal("typed", f.Overview.Edits);
            Assert.Empty(f.Exits);
            Assert.Contains("Bosun Panel is still running: " + TrayController.StillRunningText, f.Lines(LogSeverity.Info));

            f.Controller.Open();
            Assert.True(f.Controller.TakeClose()); // again in the same run
            Assert.Single(f.Icon.Notifications);

            var next = new TrayFixture(f.Files).Start(); // and in a new run over the same settings
            Assert.True(next.Controller.TakeClose());
            Assert.False(next.Window.IsVisible);
            Assert.Empty(next.Icon.Notifications);
        });
    }

    [Fact]
    public void The_first_close_notice_is_said_whatever_the_notification_settings_and_the_pause_say()
    {
        // The plan review: it went through the notifier that a pause and a turned-off type silence.
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            Assert.True(f.Store.Update(current => current with { NotifyMemoryPressure = false }).Succeeded);
            f.Notifier.PauseForAnHour();

            Assert.True(f.Controller.TakeClose());
            Assert.Equal([(TrayController.StillRunningTitle, TrayController.StillRunningText)], f.Icon.Notifications);
            Assert.False(f.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "4 GB free")); // which stay as they were
        });
    }

    [Fact]
    public void A_notice_windows_did_not_show_is_not_marked_said_and_the_next_close_tries_again()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Icon.FailNotify = true;

            Assert.True(f.Controller.TakeClose()); // still hidden: the close is not undone
            Assert.False(f.Window.IsVisible);
            Assert.False(f.Store.Current.TrayNoticeSaid);
            Assert.Equal([$"{TrayController.StillRunningTitle}: {TrayController.StillRunningText} (Windows did not show this as a notification.)"], f.Lines(LogSeverity.Warn));

            f.Icon.FailNotify = false;
            f.Controller.Open();
            Assert.True(f.Controller.TakeClose());
            Assert.Single(f.Icon.Notifications);
            Assert.True(f.Store.Current.TrayNoticeSaid);
        });
    }

    [Fact]
    public void A_said_flag_that_could_not_be_saved_is_a_warning_and_no_second_notice_in_the_same_run()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Files.FailWrite = new IOException("There is not enough space on the disk.");

            Assert.True(f.Controller.TakeClose());
            Assert.Single(f.Icon.Notifications);
            Assert.False(f.Store.Current.TrayNoticeSaid);
            var warning = Assert.Single(f.Lines(LogSeverity.Warn));
            Assert.StartsWith("That the tray notice was shown could not be saved, so it will be shown once more:", warning);
            Assert.Contains("There is not enough space on the disk.", warning);

            f.Controller.Open();
            Assert.True(f.Controller.TakeClose());
            Assert.Single(f.Icon.Notifications); // not twice in one run
            Assert.Single(f.Lines(LogSeverity.Warn));
        });
    }

    [Fact]
    public void A_notice_windows_accepted_and_never_showed_is_not_marked_said_and_is_said_again_in_the_next_run()
    {
        // The completion review: the flag was saved when Windows accepted the notification. Windows can queue or
        // suppress one (do not disturb), and the explanation was then lost for good without ever being seen.
        OnUi(() =>
        {
            var f = new TrayFixture();
            f.Icon.ConfirmsDisplay = false;
            f.Start();

            Assert.True(f.Controller.TakeClose());
            Assert.Single(f.Icon.Notifications);       // handed to Windows
            Assert.False(f.Store.Current.TrayNoticeSaid); // and not yet seen
            Assert.Empty(f.Lines(LogSeverity.Warn));

            f.Controller.Open();
            Assert.True(f.Controller.TakeClose());
            Assert.Single(f.Icon.Notifications); // not handed over twice in one run

            var next = new TrayFixture(f.Files).Start(); // the next run says it again, and this time it is seen
            Assert.True(next.Controller.TakeClose());
            Assert.Single(next.Icon.Notifications);
            Assert.True(next.Store.Current.TrayNoticeSaid);

            var last = new TrayFixture(f.Files).Start();
            Assert.True(last.Controller.TakeClose());
            Assert.Empty(last.Icon.Notifications);
        });
    }

    [Fact]
    public void A_notification_shown_with_no_notice_waiting_marks_nothing()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            Assert.True(f.Notifier.Raise(NotificationType.MemoryPressure, "Memory pressure", "4 GB free")); // shown, and not the notice
            Assert.False(f.Store.Current.TrayNoticeSaid);
        });
    }

    [Fact]
    public void A_change_of_the_icon_that_windows_refuses_is_said_once_and_the_state_is_not_taken_for_shown()
    {
        // The completion review: a refused update was cached as delivered, so a Pressure icon could stay Normal.
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Icon.RefuseUpdate = true;

            f.Controller.Tray.Pressure = true;
            Assert.Equal(TrayState.Normal, f.Icon.State); // Windows still shows the old state
            Assert.Equal(["Windows did not change the tray icon, so it may show an older state. It should say: Bosun Panel: memory pressure"], f.Lines(LogSeverity.Warn));

            f.Needs.Count = 1; // still refused: not said a second time
            Assert.Single(f.Lines(LogSeverity.Warn));

            f.Icon.RefuseUpdate = false;
            f.Needs.Count = 2;
            Assert.Equal((TrayState.Pressure, "Bosun Panel: memory pressure, and 2 things need you"), (f.Icon.State, f.Icon.Tooltip));

            f.Icon.RefuseUpdate = true; // and a new failure is a new line
            f.Controller.Tray.Pressure = false;
            Assert.Equal(2, f.Lines(LogSeverity.Warn).Count);
        });
    }

    // ---- Exit ----

    [Fact]
    public void Exit_with_nothing_unsaved_removes_the_icon_and_exits_at_once()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start(inTray: true);
            f.Shell.AskToLeave = _ => throw new InvalidOperationException("nothing is unsaved, so nothing is asked");

            f.Icon.Choose(TrayViewModel.Exit).Wait();

            Assert.Equal([0], f.Exits);
            Assert.Equal((false, 1), (f.Icon.Installed, f.Icon.Disposed));
            Assert.False(f.Controller.HasTray);
            Assert.Empty(f.Window.Calls); // the window was never needed
            Assert.False(f.Controller.TakeClose()); // on its way out, a close is a close

            f.Controller.ExitAsync().Wait(); // asked twice: exits once
            f.Controller.Dispose();
            Assert.Equal([0], f.Exits);
            Assert.Equal(1, f.Icon.Disposed);
        });
    }

    [Fact]
    public void Exit_with_unsaved_edits_brings_the_window_forward_and_asks_and_only_leaves_when_the_page_may_be_left()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Shell.Show(f.Section("settings"));
            f.Settings.Edits = "theme Light";
            Assert.True(f.Controller.TakeClose()); // in the tray, with edits on the page
            f.Window.Calls.Clear();

            // Stay: the question was on screen, and the panel goes on.
            f.Answer(LeaveChoice.Stay);
            f.Icon.Choose(TrayViewModel.Exit).Wait();
            Assert.Equal(["show", "activate"], f.Window.Calls);
            Assert.True(f.Settings.IsShown);
            Assert.Empty(f.Exits);
            Assert.Equal((true, "theme Light"), (f.Icon.Installed, f.Settings.Edits));

            // A save that fails: the panel goes on, with the edits and the reason.
            f.Answer(LeaveChoice.Save);
            f.Settings.OnSave = () => Task.FromResult(ActionOutcome.Failed("panel.json is read-only"));
            f.Icon.Choose(TrayViewModel.Exit).Wait();
            Assert.Empty(f.Exits);
            Assert.Equal((true, "theme Light", "Not saved: panel.json is read-only"), (f.Icon.Installed, f.Settings.Edits, f.Settings.SaveFailure));

            // A save that was done: the panel exits, and the icon goes.
            f.Settings.OnSave = () => Task.FromResult(ActionOutcome.Done);
            f.Icon.Choose(TrayViewModel.Exit).Wait();
            Assert.Equal([0], f.Exits);
            Assert.Equal(("theme Light", false, 1), (f.Settings.Saved, f.Icon.Installed, f.Icon.Disposed));
        });
    }

    [Fact]
    public void Exit_after_discard_exits_and_a_question_that_cannot_be_put_keeps_the_panel_and_says_why()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Overview.Edits = "typed";

            f.Shell.AskToLeave = _ => throw new InvalidOperationException("the dialog could not be shown");
            f.Controller.ExitAsync().Wait();
            Assert.Empty(f.Exits);
            Assert.True(f.Icon.Installed);
            Assert.Equal("typed", f.Overview.Edits);
            Assert.Equal(["Exit Bosun Panel: the dialog could not be shown"], f.Lines(LogSeverity.Fail));

            f.Answer(LeaveChoice.Discard);
            f.Controller.ExitAsync().Wait();
            Assert.Equal([0], f.Exits);
            Assert.Equal((1, (string?)null, false), (f.Overview.DiscardCount, f.Overview.Edits, f.Icon.Installed));
        });
    }

    // ---- the Queue line ----

    [Fact]
    public void The_queue_line_from_a_hidden_window_asks_about_unsaved_edits_on_screen_before_it_goes()
    {
        // The plan review: the Queue page was shown unconditionally, past the question the rail asks.
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Shell.Show(f.Section("settings"));
            f.Settings.Edits = "theme Light";
            Assert.True(f.Controller.TakeClose());
            f.Window.Calls.Clear();

            var visibleWhenAsked = new List<bool>();
            void Answer(LeaveChoice choice) => f.Shell.AskToLeave = _ =>
            {
                visibleWhenAsked.Add(f.Window.IsVisible);
                return Task.FromResult(choice);
            };

            Answer(LeaveChoice.Stay);
            f.Icon.Choose(TrayViewModel.Queue).Wait();
            Assert.Equal([true], visibleWhenAsked); // the window came first
            Assert.Same(f.Settings, f.Shell.Page);  // and the page and its edits stayed
            Assert.Equal(("theme Light", 0), (f.Settings.Edits, f.Queue.ShownCount));

            Answer(LeaveChoice.Save);
            f.Settings.OnSave = () => Task.FromResult(ActionOutcome.Failed("panel.json is read-only"));
            f.Icon.Choose(TrayViewModel.Queue).Wait();
            Assert.Same(f.Settings, f.Shell.Page);
            Assert.Equal(("theme Light", "Not saved: panel.json is read-only"), (f.Settings.Edits, f.Settings.SaveFailure));

            f.Settings.OnSave = () => Task.FromResult(ActionOutcome.Done);
            f.Icon.Choose(TrayViewModel.Queue).Wait();
            Assert.Same(f.Queue, f.Shell.Page);
            Assert.Equal(("theme Light", true), (f.Settings.Saved, f.Queue.IsShown));

            // And after Discard, from the tray again.
            f.Shell.Show(f.Section("settings"));
            f.Settings.Edits = "theme Dark";
            Assert.True(f.Controller.TakeClose());
            Answer(LeaveChoice.Discard);
            f.Icon.Choose(TrayViewModel.Queue).Wait();
            Assert.Same(f.Queue, f.Shell.Page);
            Assert.Equal(((string?)null, true, true), (f.Settings.Edits, f.Queue.IsShown, f.Window.IsVisible));
            Assert.Empty(f.Exits);
        });
    }

    [Fact]
    public void An_icon_lost_when_the_taskbar_restarted_is_no_tray_so_the_close_is_the_windows_own_until_it_is_back()
    {
        // Review round 1: the controller remembered at its start that there was a tray. With the icon refused after
        // a taskbar restart, closing the window hid it, and the panel ran with no window, no icon and no Exit.
        OnUi(() =>
        {
            var windows = new FakeTrayShell();
            var f = new TrayFixture(icon: new NativeTrayIcon(windows));
            var window = f.Window;
            window.Closing = () => !f.Controller.TakeClose() && new ExitGuard(f.Shell, window).MayCloseNow();
            f.Start(inTray: true);
            Assert.True(f.Controller.HasTray);
            Assert.False(window.IsVisible);

            windows.RefuseAdd = true;
            windows.RestartTaskbar(); // the icon is gone and Windows will not take it back
            Assert.False(f.Controller.HasTray);
            Assert.True(window.IsVisible); // the panel came forward
            Assert.True(f.Overview.IsShown);

            f.Overview.Edits = "typed";
            f.Answer(LeaveChoice.Stay);
            window.Close(); // not hidden: asked, as a window with no tray is
            Assert.Equal((true, 0), (window.IsVisible, window.Closed));
            Assert.DoesNotContain("hide", window.Calls);

            windows.RefuseAdd = false;
            windows.RestartTaskbar(); // the taskbar takes the icon back: there is a tray again
            Assert.True(f.Controller.HasTray);
            window.Close();
            Assert.Equal((false, 0), (window.IsVisible, window.Closed));

            f.Controller.Open();
            windows.RefuseAdd = true;
            windows.RestartTaskbar();
            f.Answer(LeaveChoice.Discard);
            window.Close(); // and with none, a close the person agrees to is a real close
            Assert.Equal(1, window.Closed);
        });
    }

    // ---- the whole of it, as the application drives it ----

    [Fact]
    public void Driven_as_the_application_does_over_the_real_icon_class_from_start_to_exit_nothing_is_left_installed()
    {
        OnUi(() =>
        {
            var windows = new FakeTrayShell();
            var f = new TrayFixture(icon: new NativeTrayIcon(windows));
            var window = f.Window;
            window.Closing = () => !f.Controller.TakeClose() && new ExitGuard(f.Shell, window).MayCloseNow(); // as MainWindow.OnClosing asks

            f.Start(inTray: true);
            Assert.Equal((true, TrayState.Normal, "Bosun Panel: normal"), (windows.Installed, windows.State, windows.Tooltip));
            Assert.False(window.IsVisible);

            windows.Send(FakeTrayShell.Callback, NativeTrayIcon.NIN_SELECT); // a click on the real icon
            Assert.True(window.IsVisible);

            f.Needs.Count = 2; // what needs the operator reaches Windows
            Assert.Equal((TrayState.NeedsYou, "Bosun Panel: 2 things need you"), (windows.State, windows.Tooltip));

            window.Close(); // the close button: hidden, said once, through the real icon
            Assert.Equal((false, 0), (window.IsVisible, window.Closed));
            Assert.Equal([(TrayController.StillRunningTitle, TrayController.StillRunningText)], windows.Notifications);

            windows.Chooses = 2; // the Queue line of the native menu
            windows.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
            Assert.Equal(["Open", "Queue: Not available", "Pause notifications for 1 hour", "Exit"], windows.Menus.Single().Select(line => line.Text));
            Assert.True(window.IsVisible);
            Assert.Same(f.Queue, f.Shell.Page);

            windows.Chooses = 3; // Pause
            windows.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
            Assert.NotNull(f.Notifier.PausedUntil);
            Assert.Equal("Pause notifications for 1 hour", windows.Menus[1][2].Text); // as it read when it was opened
            Assert.StartsWith("Resume notifications", f.Controller.Tray.Menu()[2].Text);

            windows.Chooses = 4; // Exit
            windows.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
            Assert.Equal([0], f.Exits);
            Assert.False(windows.Installed);
            Assert.Equal(1, windows.Calls.Count(call => call == "delete"));

            window.Close(); // the application shutting its window: now a real close
            Assert.Equal(1, window.Closed);
        });
    }
}

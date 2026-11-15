using Bosun.Panel.Tray;
using Xunit;

namespace Bosun.Panel.Tests.Tray;

/// <summary>
/// The real tray icon class (D02 T02 §7) over a tray shell of the tests' own: what it adds, changes and removes,
/// what each message from Windows does, and which menu line runs. The real calls are tested only where they touch no
/// screen: a menu built and read back, an icon loaded. No test adds an icon to the tray.
/// </summary>
public class NativeTrayIconTests
{
    private const int MouseMove = 0x0200;

    private static (NativeTrayIcon Icon, FakeTrayShell Shell) Made()
    {
        var shell = new FakeTrayShell();
        return (new NativeTrayIcon(shell), shell);
    }

    [Fact]
    public void The_icon_is_added_with_its_state_and_words_and_a_change_changes_it_and_the_same_again_changes_nothing()
    {
        var (icon, shell) = Made();
        Assert.False(icon.IsInstalled);

        Assert.True(icon.Install(TrayState.Normal, "Bosun Panel"));
        Assert.True(icon.IsInstalled);
        Assert.Equal((true, TrayState.Normal, "Bosun Panel"), (shell.Installed, shell.State, shell.Tooltip));

        icon.Update(TrayState.Pressure, "Bosun Panel: memory pressure");
        Assert.Equal((TrayState.Pressure, "Bosun Panel: memory pressure"), (shell.State, shell.Tooltip));
        icon.Update(TrayState.Pressure, "Bosun Panel: memory pressure"); // nothing to tell Windows
        Assert.Equal(["add", "modify"], shell.Calls);

        icon.Update(TrayState.Pressure, "Bosun Panel: memory pressure, and 1 thing needs you"); // the words alone
        Assert.Equal(["add", "modify", "modify"], shell.Calls);
    }

    [Theory]
    [InlineData(NativeTrayIcon.NIN_SELECT)]       // a click
    [InlineData(NativeTrayIcon.NIN_KEYSELECT)]    // Enter or Space on the icon
    [InlineData(NativeTrayIcon.WM_LBUTTONDBLCLK)] // a double click
    public void A_click_a_double_click_and_the_keyboard_each_ask_for_the_panel(int what)
    {
        var (icon, shell) = Made();
        var opened = 0;
        icon.Opened += () => opened++;
        icon.Install(TrayState.Normal, "Bosun Panel");

        shell.Send(FakeTrayShell.Callback, what | (1 << 16)); // the icon's id rides in the high word, and is not the event
        Assert.Equal(1, opened);
        Assert.DoesNotContain("menu", shell.Calls);
    }

    [Fact]
    public void A_message_that_is_not_the_icons_or_means_nothing_does_nothing()
    {
        var (icon, shell) = Made();
        var opened = 0;
        icon.Opened += () => opened++;
        icon.Menu = () => [new TrayMenuItem("open", "Open", () => Task.CompletedTask)];
        icon.Install(TrayState.Normal, "Bosun Panel");

        shell.Send(FakeTrayShell.Callback, MouseMove);                       // the pointer over the icon
        shell.Send(FakeTrayShell.Callback + 1, NativeTrayIcon.NIN_SELECT);   // another message with the same lParam
        shell.Send(0x0010, NativeTrayIcon.WM_CONTEXTMENU);
        Assert.Equal(0, opened);
        Assert.Equal(["add"], shell.Calls);
    }

    [Theory]
    [InlineData(0x8001, NativeTrayIcon.NIN_SELECT, TrayEvent.Open)]
    [InlineData(0x8001, NativeTrayIcon.NIN_KEYSELECT, TrayEvent.Open)]
    [InlineData(0x8001, NativeTrayIcon.WM_LBUTTONDBLCLK, TrayEvent.Open)]
    [InlineData(0x8001, NativeTrayIcon.WM_CONTEXTMENU, TrayEvent.Menu)]
    [InlineData(0x8001, NativeTrayIcon.NIN_BALLOONSHOW, TrayEvent.NotificationShown)]
    [InlineData(0x8001, NativeTrayIcon.NIN_BALLOONUSERCLICK, TrayEvent.Open)]
    [InlineData(0x8001, 0x0200, TrayEvent.None)]
    [InlineData(0x8002, NativeTrayIcon.NIN_SELECT, TrayEvent.None)]
    [InlineData(0xC123, 0, TrayEvent.TaskbarCreated)]
    public void Each_message_is_read_as_what_it_means(int message, int lParam, TrayEvent means)
    {
        Assert.Equal(means, NativeTrayIcon.Decode(message, new IntPtr(lParam), 0x8001, 0xC123));
        // With no taskbar message known, 0 is never taken for it.
        Assert.Equal(TrayEvent.None, NativeTrayIcon.Decode(0, IntPtr.Zero, 0x8001, 0));
    }

    [Fact]
    public void A_right_click_shows_the_menu_as_it_reads_now_and_the_line_chosen_runs_and_no_other()
    {
        var (icon, shell) = Made();
        var ran = new List<string>();
        var pause = "Pause notifications for 1 hour";
        icon.Menu = () =>
        [
            new TrayMenuItem("open", "Open", () => Ran("open")),
            new TrayMenuItem("queue", "Queue: Not available", () => Ran("queue")),
            new TrayMenuItem("pause", pause, () => Ran("pause")),
            new TrayMenuItem("exit", "Exit", () => Ran("exit")),
        ];
        icon.Install(TrayState.Normal, "Bosun Panel");

        shell.Chooses = 3;
        shell.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
        Assert.Equal([(1, "Open"), (2, "Queue: Not available"), (3, "Pause notifications for 1 hour"), (4, "Exit")], shell.Menus.Single());
        Assert.Equal(["pause"], ran);

        pause = "Resume notifications (paused until 10:10)"; // asked for afresh each time it is opened
        shell.Chooses = null;                                // and dismissed: nothing runs
        shell.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
        Assert.Equal("Resume notifications (paused until 10:10)", shell.Menus[1][2].Text);
        Assert.Equal(["pause"], ran);

        shell.Chooses = 9; // not a line of this menu
        shell.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
        Assert.Equal(["pause"], ran);

        shell.Chooses = 4;
        shell.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
        Assert.Equal(["pause", "exit"], ran);

        Task Ran(string id)
        {
            ran.Add(id);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void A_menu_that_is_cancelled_gives_the_focus_back_to_the_notification_area_and_one_that_is_used_does_not()
    {
        // The completion review: after Escape the keyboard's focus was left on a window nobody can see.
        var (icon, shell) = Made();
        icon.Menu = () => [new TrayMenuItem("open", "Open", () => Task.CompletedTask)];
        icon.Install(TrayState.Normal, "Bosun Panel");

        shell.Chooses = 1;
        shell.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
        Assert.Equal(["add", "menu"], shell.Calls);

        shell.Chooses = null;
        shell.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
        Assert.Equal(["add", "menu", "menu", "focus"], shell.Calls);
    }

    [Fact]
    public void A_change_windows_refuses_is_reported_and_tried_again_at_the_next_change_or_message()
    {
        // The completion review: the new state was remembered before Windows took it, so the same state asked for
        // again was skipped, and the icon could show Normal under pressure for as long as the panel ran.
        var (icon, shell) = Made();
        icon.Install(TrayState.Normal, "Bosun Panel");

        shell.RefuseModify = true;
        Assert.False(icon.Update(TrayState.Pressure, "Bosun Panel: memory pressure"));
        Assert.Equal(TrayState.Normal, shell.State);
        Assert.False(icon.Update(TrayState.Pressure, "Bosun Panel: memory pressure")); // the same again is not skipped
        Assert.Equal(["add", "modify", "modify"], shell.Calls);

        shell.RefuseModify = false;
        shell.Send(FakeTrayShell.Callback, 0x0200); // the pointer passes over the icon: it is alive, and is tried again
        Assert.Equal((TrayState.Pressure, "Bosun Panel: memory pressure"), (shell.State, shell.Tooltip));

        Assert.True(icon.Update(TrayState.Pressure, "Bosun Panel: memory pressure")); // and now there is nothing to tell
        Assert.Equal(["add", "modify", "modify", "modify"], shell.Calls);
    }

    [Fact]
    public void Windows_saying_a_notification_is_on_screen_is_passed_on_and_a_click_on_one_asks_for_the_panel()
    {
        var (icon, shell) = Made();
        var (shown, opened) = (0, 0);
        icon.NotificationShown += () => shown++;
        icon.Opened += () => opened++;
        icon.Install(TrayState.Normal, "Bosun Panel");

        Assert.True(icon.Notify("Memory pressure", "4 GB free"));
        Assert.Equal(0, shown); // accepted is not shown

        shell.Send(FakeTrayShell.Callback, NativeTrayIcon.NIN_BALLOONSHOW);
        Assert.Equal((1, 0), (shown, opened));
        shell.Send(FakeTrayShell.Callback, NativeTrayIcon.NIN_BALLOONUSERCLICK);
        Assert.Equal((1, 1), (shown, opened));
    }

    [Fact]
    public void A_notification_goes_to_the_installed_icon_and_nowhere_when_there_is_none()
    {
        var (icon, shell) = Made();
        Assert.False(icon.Notify("Memory pressure", "4 GB free")); // no icon yet
        Assert.Empty(shell.Calls);

        icon.Install(TrayState.Normal, "Bosun Panel");
        Assert.True(icon.Notify("Memory pressure", "4 GB free"));
        Assert.Equal([("Memory pressure", "4 GB free")], shell.Notifications);

        shell.RefuseNotify = true;
        Assert.False(icon.Notify("Memory pressure", "3 GB free")); // Windows said no, and that is what is reported
    }

    [Fact]
    public void A_taskbar_that_restarts_gets_the_icon_again_as_it_stood()
    {
        var (icon, shell) = Made();
        var opened = 0;
        icon.Opened += () => opened++;
        icon.Install(TrayState.Normal, "Bosun Panel");
        icon.Update(TrayState.NeedsYou, "Bosun Panel: 1 thing needs you");

        shell.RestartTaskbar();
        Assert.Equal((true, TrayState.NeedsYou, "Bosun Panel: 1 thing needs you"), (shell.Installed, shell.State, shell.Tooltip));
        Assert.True(icon.IsInstalled);
        Assert.Equal(0, opened);

        // A taskbar that will not take it back: the panel is asked for, so it is never running out of reach.
        shell.RefuseAdd = true;
        shell.RestartTaskbar();
        Assert.False(icon.IsInstalled);
        Assert.Equal(1, opened);
        Assert.False(icon.Notify("Memory pressure", "4 GB free"));

        // And the next restart is tried again.
        shell.RefuseAdd = false;
        shell.RestartTaskbar();
        Assert.True(icon.IsInstalled);
        Assert.True(shell.Installed);
    }

    [Fact]
    public void An_icon_windows_refuses_is_reported_refused_and_nothing_is_left_or_tried_again()
    {
        var (icon, shell) = Made();
        shell.RefuseAdd = true;
        Assert.False(icon.Install(TrayState.Normal, "Bosun Panel"));
        Assert.False(icon.IsInstalled);
        Assert.False(shell.Installed);

        shell.RefuseAdd = false;
        shell.RestartTaskbar(); // the panel went on without a tray: one does not appear later behind its back
        icon.Update(TrayState.Pressure, "Bosun Panel: memory pressure");
        Assert.Equal(["add"], shell.Calls);

        icon.Dispose();
        Assert.Equal(["add", "dispose"], shell.Calls); // nothing to remove
    }

    [Fact]
    public void Disposing_removes_the_icon_once_and_stops_listening()
    {
        var (icon, shell) = Made();
        var opened = 0;
        icon.Opened += () => opened++;
        icon.Install(TrayState.Normal, "Bosun Panel");

        icon.Dispose();
        icon.Dispose();
        Assert.False(shell.Installed);
        Assert.Equal(1, shell.Calls.Count(call => call == "delete"));

        shell.Send(FakeTrayShell.Callback, NativeTrayIcon.NIN_SELECT);
        shell.RestartTaskbar();
        Assert.Equal(0, opened);
        Assert.False(shell.Installed);
    }

    // ---- the real calls, where they touch no screen ----

    [Fact]
    public void A_long_tooltip_is_cut_at_a_word_to_what_windows_keeps()
    {
        var text = "Bosun Panel: memory pressure, and " + string.Join(" ", Enumerable.Repeat("something needs you", 14));
        Assert.True(text.Length >= 300);

        var cut = NativeTrayIcon.Cut(text);
        Assert.True(cut.Length <= NativeTrayIcon.MaxTooltip, $"{cut.Length} characters");
        Assert.EndsWith("…", cut);
        Assert.StartsWith(cut[..^1], text);
        Assert.Equal(' ', text[cut.Length - 1]); // the cut fell between two words
        Assert.Equal("Bosun Panel", NativeTrayIcon.Cut("  Bosun Panel "));

        var (icon, shell) = Made();
        icon.Install(TrayState.Normal, text);
        Assert.Equal(cut, shell.Tooltip);
    }

    [Fact]
    public void The_view_models_menu_built_as_a_native_menu_reads_back_its_four_lines()
    {
        TrayViewModelTests.OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Notifier.PauseForAnHour();
            var lines = f.Controller.Tray.Menu().Select((item, index) => (index + 1, item.Text)).ToList();

            using var menu = new NativeMenu(lines);
            Assert.Equal([(1, "Open"), (2, "Queue: Not available"), (3, "Resume notifications (paused until 10:10)"), (4, "Exit")], menu.Items());
        });
    }

    [Theory]
    [InlineData(TrayState.Normal)]
    [InlineData(TrayState.Pressure)]
    [InlineData(TrayState.NeedsYou)]
    public void The_icon_for_each_state_loads_at_the_small_icon_size(TrayState state)
    {
        Assert.InRange(Win32TrayShell.SmallIconSize, 16, 64);
        using var icon = Win32TrayShell.LoadIcon(state, 16);
        Assert.Equal((16, 16), (icon.Width, icon.Height));
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
    }
}

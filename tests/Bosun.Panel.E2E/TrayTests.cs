using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// The real icon in the real notification area (D02 T02 §7 proves only what touches no screen): its tooltip, its
/// menu and each line's work, a click on it, and the notification the first close raises.
/// </summary>
public class TrayTests
{
    private const string Tooltip = "Bosun Panel: normal"; // TrayViewModel.NormalTooltip
    private const string Pause = "Pause notifications for 1 hour";

    [Fact]
    public async Task The_icon_its_menu_a_click_and_the_first_close_s_notification_each_do_their_work()
    {
        using var panel = PanelProcess.Start("--tray");
        using var pointer = RealInput.KeepPointer();
        Ui.Until(() => Native.WindowsOf(panel.Id).Any(w => Native.TitleOf(w) == "Bosun Panel tray"), Ui.Short, "the panel's tray window");
        Thread.Sleep(1000);
        Assert.Null(Ui.TopLevel(panel.Id, "Bosun Panel")); // started in the tray: no window

        // The icon, with its tooltip, and the one Bosun Panel icon there is.
        var icon = Icon(panel);
        Assert.Equal(Tooltip, icon.Current.Name);

        // The menu lists its four lines; Open shows the hidden window.
        var lines = OpenMenu(panel);
        Assert.Equal(4, lines.Count);
        Assert.Equal("Open", lines[0].Current.Name);
        Assert.StartsWith("Queue: ", lines[1].Current.Name);
        Assert.Equal(Pause, lines[2].Current.Name);
        Assert.Equal("Exit", lines[3].Current.Name);
        Choose(panel, lines[0]);
        var window = Shown(panel);

        // The first close hides the window and raises the still-running notification, which Windows confirms showing.
        Hide(panel, window);
        Ui.Until(() => Stored(panel, "tray_notice_said"), TimeSpan.FromSeconds(15), "Windows to show the still-running notification");

        // A click on the icon shows the hidden window.
        RealInput.ClickIcon(Icon(panel));
        window = Shown(panel);
        Hide(panel, window);

        // The Queue line shows the Queue page.
        Choose(panel, OpenMenu(panel)[1]);
        window = Shown(panel);
        RailTests.Settle(window, "Queue");
        Hide(panel, window);

        // Pause becomes Resume, and Resume becomes Pause again.
        Choose(panel, OpenMenu(panel)[2]);
        var paused = OpenMenu(panel)[2];
        Assert.StartsWith("Resume notifications (paused until ", paused.Current.Name);
        Choose(panel, paused);
        var exit = OpenMenu(panel);
        Assert.Equal(Pause, exit[2].Current.Name);

        // Exit ends the panel and takes its icon away.
        Choose(panel, exit[3]);
        var ended = await Task.WhenAny(panel.Exited, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.True(ended == panel.Exited, "Exit ends the panel within 15 s");
        Assert.Equal(0, panel.ExitCode());
        Ui.Until(() => BosunIcons().Count == 0, Ui.Short, "the icon to leave the notification area");
    }

    /// <summary>
    /// This panel's icon, established before any input reaches the notification area. Two panels' icons say the same
    /// thing, so the icon is this panel's only when no other Bosun Panel process runs: that is checked first, and the
    /// test refuses, sending nothing, when one does. Any other Bosun Panel icon then belongs to a process that has gone,
    /// and is pointed at, which makes the notification area drop it.
    /// </summary>
    internal static AutomationElement Icon(PanelProcess owner)
    {
        var others = OtherPanels(owner.Id);
        if (others.Count > 0)
            throw new RealInputRefused($"another Bosun Panel is running (process {string.Join(", ", others)}), and its icon can't be told from this panel's: close it and run the smoke again");
        if (!owner.Running) throw new RealInputRefused("the panel under test is not running, so no icon is its own");
        var icons = BosunIcons();
        if (icons.Count > 1)
        {
            foreach (var each in icons) RealInput.MoveOnIcon(each);
            Thread.Sleep(500);
            icons = BosunIcons();
        }
        if (icons.Count != 1)
            throw new RealInputRefused($"the notification area holds {icons.Count} Bosun Panel icons where the panel under test should have one");
        // A notification can lie over the icon for a while (the still-running one does, over the overflow): wait for it
        // to go rather than send the pointer to it.
        var icon = icons[0];
        Ui.Until(() =>
        {
            var (x, y) = Ui.Centre(icon);
            return Native.ClassOf(Native.RootAt(x, y)) is "Shell_TrayWnd" or "TopLevelWindowForOverflowXamlIsland";
        }, TimeSpan.FromSeconds(30), "nothing to lie over the icon");
        return icon;
    }

    /// <summary>
    /// Clicks a line of the panel's menu once nothing lies over it: a notification can cover the menu's corner of the
    /// screen for some seconds, and the click must land on the line, never on the notification.
    /// </summary>
    private static void Choose(PanelProcess panel, AutomationElement line)
    {
        Ui.Until(() =>
        {
            var (x, y) = Ui.Centre(line);
            var under = Native.RootAt(x, y);
            return Native.ClassOf(under) == "#32768" && Native.ProcessOf(under) == panel.Id;
        }, TimeSpan.FromSeconds(30), $"nothing to lie over the menu line '{line.Current.Name}'");
        RealInput.ClickMenuLine(panel.Id, line);
    }

    /// <summary>The running Bosun.Panel processes in this session other than <paramref name="mine"/>.</summary>
    private static List<int> OtherPanels(int mine)
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var found = new List<int>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("Bosun.Panel"))
            using (process)
                if (process.Id != mine && process.SessionId == session) found.Add(process.Id);
        return found;
    }

    [Fact]
    public void An_icon_is_refused_before_any_input_when_another_panel_runs()
    {
        // The other panel stands for the operator's own: it has an icon. The program under test has none.
        using var other = PanelProcess.Start("--tray");
        Ui.Until(() => Native.WindowsOf(other.Id).Any(w => Native.TitleOf(w) == "Bosun Panel tray"), Ui.Short, "the other panel's tray window");
        using var owner = PanelProcess.StartProgram(PanelProcess.TestChildExe, "sleep", "30000");
        var pointer = Native.Cursor();
        var refused = Assert.Throws<RealInputRefused>(() => Icon(owner));
        Assert.Contains($"process {other.Id}", refused.Message);
        Assert.Equal((pointer.X, pointer.Y), (Native.Cursor().X, Native.Cursor().Y)); // nothing was pointed at
        Assert.Null(AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, other.Id),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu)))); // and no menu opened
    }

    /// <summary>Every Bosun Panel icon in the notification area, its overflow opened when the icons are hidden there.</summary>
    private static List<AutomationElement> BosunIcons()
    {
        var root = AutomationElement.RootElement;
        // UI Automation can miss the taskbar for a moment while a window closes: asked again until it answers.
        var taskbar = Ui.Until(() => root.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd")), Ui.Short, "the taskbar");
        var overflow = root.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland"));
        if (overflow is null || !Native.IsWindowVisible(new IntPtr(overflow.Current.NativeWindowHandle)) || overflow.Current.IsOffscreen)
        {
            // The button that opens the overflow, found without its name, which is in the shell's language: the first
            // of the tray's own buttons, the leftmost (class SystemTray.NormalButton, id SystemTrayIcon).
            if (taskbar.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.NormalButton"),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"))) is { } chevron)
            {
                Ui.Invoke(chevron);
                overflow = Ui.Until(() => root.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland")) is { } open
                    && Native.IsWindowVisible(new IntPtr(open.Current.NativeWindowHandle)) ? open : null, Ui.Short, "the notification area's overflow to open");
                Thread.Sleep(300);
            }
        }
        var found = new List<AutomationElement>();
        foreach (var scope in new[] { taskbar, overflow })
            if (scope is not null)
                found.AddRange(scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>().Where(b => b.Current.Name.StartsWith("Bosun Panel", StringComparison.Ordinal) && !b.Current.BoundingRectangle.IsEmpty));
        return found;
    }

    /// <summary>The icon's menu, opened with a right click, as its lines.</summary>
    private static List<AutomationElement> OpenMenu(PanelProcess panel)
    {
        RealInput.RightClickIcon(Icon(panel));
        var menu = Ui.Until(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, panel.Id),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu))), Ui.Short, "the icon's menu");
        return Ui.Until(() => menu.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
            .Cast<AutomationElement>().ToList() is { Count: 4 } lines ? lines : null, Ui.Short, "the menu's four lines");
    }

    private static AutomationElement Shown(PanelProcess panel)
    {
        var window = panel.Window();
        Ui.Until(() => Native.IsWindowVisible(new IntPtr(window.Current.NativeWindowHandle)), Ui.Short, "the window to be shown");
        return window;
    }

    /// <summary>Closes the window, which the tray takes and hides.</summary>
    private static void Hide(PanelProcess panel, AutomationElement window)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Ui.Until(() => !Native.IsWindowVisible(handle), Ui.Short, "the closed window to be hidden");
        Assert.True(panel.Running, "closing the window leaves the panel running in the tray");
    }

    private static bool Stored(PanelProcess panel, string key)
    {
        try
        {
            using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(panel.DataFolder, "panel.json")));
            return file.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return false; // not written yet, or being replaced
        }
    }
}

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
        var icon = Icon();
        Assert.Equal(Tooltip, icon.Current.Name);

        // The menu lists its four lines; Open shows the hidden window.
        var lines = OpenMenu(panel);
        Assert.Equal(4, lines.Count);
        Assert.Equal("Open", lines[0].Current.Name);
        Assert.StartsWith("Queue: ", lines[1].Current.Name);
        Assert.Equal(Pause, lines[2].Current.Name);
        Assert.Equal("Exit", lines[3].Current.Name);
        RealInput.ClickMenuLine(panel.Id, lines[0]);
        var window = Shown(panel);

        // The first close hides the window and raises the still-running notification, which Windows confirms showing.
        Hide(panel, window);
        Ui.Until(() => Stored(panel, "tray_notice_said"), TimeSpan.FromSeconds(15), "Windows to show the still-running notification");

        // A click on the icon shows the hidden window.
        RealInput.ClickIcon(Icon());
        window = Shown(panel);
        Hide(panel, window);

        // The Queue line shows the Queue page.
        RealInput.ClickMenuLine(panel.Id, OpenMenu(panel)[1]);
        window = Shown(panel);
        RailTests.Settle(window, "Queue");
        Hide(panel, window);

        // Pause becomes Resume, and Resume becomes Pause again.
        RealInput.ClickMenuLine(panel.Id, OpenMenu(panel)[2]);
        var paused = OpenMenu(panel)[2];
        Assert.StartsWith("Resume notifications (paused until ", paused.Current.Name);
        RealInput.ClickMenuLine(panel.Id, paused);
        var exit = OpenMenu(panel);
        Assert.Equal(Pause, exit[2].Current.Name);

        // Exit ends the panel and takes its icon away.
        RealInput.ClickMenuLine(panel.Id, exit[3]);
        var ended = await Task.WhenAny(panel.Exited, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.True(ended == panel.Exited, "Exit ends the panel within 15 s");
        Assert.Equal(0, panel.ExitCode());
        Ui.Until(() => BosunIcons().Count == 0, Ui.Short, "the icon to leave the notification area");
    }

    /// <summary>
    /// This panel's icon. Every other Bosun Panel icon is pointed at first, which makes the notification area drop
    /// one whose process has gone; one that stays belongs to another running panel, and the two can't be told apart.
    /// </summary>
    private static AutomationElement Icon()
    {
        var icons = BosunIcons();
        if (icons.Count > 1)
        {
            foreach (var each in icons) RealInput.MoveOnIcon(each);
            Thread.Sleep(500);
            icons = BosunIcons();
        }
        if (icons.Count != 1)
            throw new Xunit.Sdk.XunitException($"the notification area holds {icons.Count} Bosun Panel icons, so this panel's can't be told from another's: close every other Bosun Panel and run the smoke again");
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

    /// <summary>Every Bosun Panel icon in the notification area, its overflow opened when the icons are hidden there.</summary>
    private static List<AutomationElement> BosunIcons()
    {
        var root = AutomationElement.RootElement;
        // UI Automation can miss the taskbar for a moment while a window closes: asked again until it answers.
        var taskbar = Ui.Until(() => root.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd")), Ui.Short, "the taskbar");
        var overflow = root.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland"));
        if (overflow is null || !Native.IsWindowVisible(new IntPtr(overflow.Current.NativeWindowHandle)) || overflow.Current.IsOffscreen)
        {
            if (taskbar.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Show Hidden Icons")) is { } chevron)
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
        RealInput.RightClickIcon(Icon());
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

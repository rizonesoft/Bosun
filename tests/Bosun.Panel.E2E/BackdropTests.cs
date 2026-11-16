using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// The window's backdrop on the real panel (D02 T03 §2), started as the application starts it: DWM reads back Mica
/// (<c>DWMSBT_MAINWINDOW</c>) where this machine allows it, worked out here from Windows itself (the build, the
/// person's transparency setting, a contrast theme), independently of the panel's own policy; and the same when the
/// panel started in the tray and its window was asked for later, by a second launch.
/// </summary>
public class BackdropTests
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;

    /// <summary>Whether this machine, now, should show Mica: Windows 11 22H2 or later, transparency on, no contrast theme.</summary>
    private static bool MicaHere()
    {
        var transparency = Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", null);
        return Environment.OSVersion.Version.Build >= 22621 && transparency is not 0 && !SystemParameters.HighContrast;
    }

    private static int Backdrop(IntPtr window)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, DWMWA_SYSTEMBACKDROP_TYPE, out var value, sizeof(int)));
        return value;
    }

    [Fact]
    public void The_shown_window_has_Mica_where_this_machine_allows_it_tinted_as_its_theme()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Handle();
        var mica = MicaHere();
        Assert.True(Ui.Wait(() => (Backdrop(window) == DWMSBT_MAINWINDOW) == mica, TimeSpan.FromSeconds(5)),
            $"DWM reads back backdrop {Backdrop(window)}, and this machine {(mica ? "allows" : "doesn't allow")} Mica");
        if (!mica) return;
        // A new panel follows Windows' app mode: dark Mica unless apps are set light.
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null) is 1;
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, DWMWA_USE_IMMERSIVE_DARK_MODE, out var dark, sizeof(int)));
        Assert.Equal(light ? 0 : 1, dark);
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [Fact]
    public async Task A_window_started_in_the_tray_has_it_once_it_is_asked_for()
    {
        using var panel = PanelProcess.Start("--tray");
        Ui.Until(() => Native.WindowsOf(panel.Id).Any(w => Native.TitleOf(w) == "Bosun Panel tray"), Ui.Short, "the panel's tray window");
        using (var second = panel.StartAgain()) // a second launch asks the first for its window, then exits
        {
            var ended = await Task.WhenAny(second.Exited, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.True(ended == second.Exited, "the second launch exits within 15 s");
        }
        var window = panel.Handle();
        var mica = MicaHere();
        Assert.True(Ui.Wait(() => (Backdrop(window) == DWMSBT_MAINWINDOW) == mica, TimeSpan.FromSeconds(5)),
            $"DWM reads back backdrop {Backdrop(window)}, and this machine {(mica ? "allows" : "doesn't allow")} Mica");
    }

    private const int VK_MENU = 0x12, VK_SPACE = 0x20, VK_F4 = 0x73, VK_ESCAPE = 0x1B;

    [Fact]
    public void Each_caption_mark_is_drawn_once_and_the_keyboard_loses_nothing()
    {
        // Found on the first capture: in Mica Windows drew its own caption buttons under the panel's. Without the system
        // menu style it draws none; and the keys that style gave still work: Alt+Space opens the system menu, Alt+F4
        // closes the window (the first close hides it in the tray).
        using var panel = PanelProcess.Start();
        var front = panel.Front();
        var window = new IntPtr(front.Current.NativeWindowHandle);
        var mica = MicaHere();
        Assert.Equal(mica ? 0 : WS_SYSMENU, GetWindowLongPtr(window, GWL_STYLE).ToInt64() & WS_SYSMENU);

        // A right click on the title bar opens the system menu (WindowChrome's), with or without the style.
        var frame = Native.Frame(window);
        RealInput.RightClickOnWindow(window, frame.Left + 200, frame.Top + 10);
        SystemMenu(panel, "a right click on the title bar");
        Ui.Until(() => Native.GetForegroundWindow() == window, Ui.Short, "the window to have the foreground again");

        // Alt+Space opens it too. Posted to the window itself: a launcher holding Alt+Space as a global hotkey (PowerToys
        // Run does, on the machine this was built on) takes the real keystroke before any window sees it.
        PostMessage(window, WM_SYSKEYDOWN, new IntPtr(VK_SPACE), new IntPtr(0x20390001));
        PostMessage(window, WM_SYSKEYUP, new IntPtr(VK_SPACE), new IntPtr(unchecked((int)0xE0390001)));
        SystemMenu(panel, "Alt+Space");
        Ui.Until(() => Native.GetForegroundWindow() == window, Ui.Short, "the window to have the foreground again");

        RealInput.Chord(window, VK_MENU, VK_F4);
        Ui.Until(() => !Native.IsWindowVisible(window), Ui.Short, "Alt+F4 to close the window into the tray");
        Assert.True(panel.Running);
    }

    /// <summary>Waits for the panel's system menu to open, then closes it with Escape, which goes to the menu.</summary>
    private static void SystemMenu(PanelProcess panel, string by)
    {
        Ui.Until(() => System.Windows.Automation.AutomationElement.RootElement.FindFirst(System.Windows.Automation.TreeScope.Children,
            new System.Windows.Automation.AndCondition(
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ProcessIdProperty, panel.Id),
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Menu))),
            Ui.Short, $"the system menu {by} opens");
        Native.Key(VK_ESCAPE);
    }

    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private const int GWL_STYLE = -16;
    private const long WS_SYSMENU = 0x00080000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}

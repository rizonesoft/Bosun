using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// Real keys on the shown window: what an off-screen test can't prove, because a control reads the real keyboard and
/// mouse as well as the event it is given, and a drop-down is a window of its own.
/// </summary>
public class KeyboardTests
{
    [Fact]
    public void Tab_moves_the_keyboard_and_draws_the_focus_ring()
    {
        using var panel = PanelProcess.Start();
        var (window, handle) = OnSettings(panel);
        var theme = Ui.Get(window, "ThemeBox");
        theme.SetFocus();
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, theme), Ui.Short, "the theme box to have focus");

        var next = Ui.Get(window, "StartWithWindows");
        var area = Around(next);
        var before = AccentPixels(area);
        RealInput.Key(handle, Native.VK_TAB);
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, next) && f.Current.HasKeyboardFocus, Ui.Short, "Tab to give Start with Windows the keyboard");
        // The ring is a 2 px accent outline 2 px outside the control (FocusRing in src/Bosun.Panel/Theme/Controls.xaml):
        // on screen, it adds at least half the outline's length in accent pixels around the control.
        var box = Ui.Box(next);
        var outline = (int)(box.Width + box.Height);
        int after = 0;
        Ui.Until(() => (after = AccentPixels(area)) - before >= outline, Ui.Short, $"the focus ring to be drawn ({before} accent pixels before Tab)");
    }

    [Fact]
    public void Space_toggles_a_focused_check_box()
    {
        using var panel = PanelProcess.Start();
        var (window, handle) = OnSettings(panel);
        var notify = Ui.Get(window, "NotifyMemoryPressure");
        var was = Ui.Toggle(notify);
        notify.SetFocus();
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, notify), Ui.Short, "the check box to have focus");
        RealInput.Key(handle, Native.VK_SPACE);
        Ui.Until(() => Ui.Toggle(notify) != was, Ui.Short, "Space to toggle the check box");
        RealInput.Key(handle, Native.VK_SPACE);
        Ui.Until(() => Ui.Toggle(notify) == was, Ui.Short, "Space to toggle it back");
    }

    [Fact]
    public void Down_selects_a_closed_combo_box_s_next_item_and_its_list_opens_named_and_closes_on_Esc()
    {
        using var panel = PanelProcess.Start();
        var (window, handle) = OnSettings(panel);
        var theme = Ui.Get(window, "ThemeBox");
        Assert.Equal(ExpandCollapseState.Collapsed, Ui.Expansion(theme));
        Assert.Equal("Follow Windows", Ui.Selected(theme)?.Current.Name);
        theme.SetFocus();
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, theme), Ui.Short, "the theme box to have focus");
        RealInput.Key(handle, Native.VK_DOWN);
        Ui.Until(() => Ui.Selected(theme)?.Current.Name == "Dark", Ui.Short, "Down to select the next theme, Dark");
        Assert.Equal(ExpandCollapseState.Collapsed, Ui.Expansion(theme));

        ((ExpandCollapsePattern)theme.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Ui.Until(() => Ui.Expansion(theme) == ExpandCollapseState.Expanded, Ui.Short, "the list to open");
        var items = theme.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().Select(item => item.Current.Name).ToList();
        Assert.Equal(["Follow Windows", "Dark", "Light"], items);
        Assert.Empty(Ui.Nameless(theme));
        RealInput.Key(handle, Native.VK_ESCAPE);
        Ui.Until(() => Ui.Expansion(theme) == ExpandCollapseState.Collapsed, Ui.Short, "Esc to close the list");
        Assert.Equal("Dark", Ui.Selected(theme)?.Current.Name);
    }

    /// <summary>The panel in front, on its Settings page.</summary>
    internal static (AutomationElement Window, IntPtr Handle) OnSettings(PanelProcess panel)
    {
        var window = panel.Front();
        Ui.Select(RailTests.Row(Ui.Get(window, "Rail"), "Settings"));
        RailTests.Settle(window, "Settings");
        return (window, new IntPtr(window.Current.NativeWindowHandle));
    }

    private static Rectangle Around(AutomationElement element)
    {
        var box = Ui.Box(element);
        return Rectangle.FromLTRB((int)box.Left - 8, (int)box.Top - 8, (int)box.Right + 8, (int)box.Bottom + 8);
    }

    /// <summary>Pixels on screen, in an area, within a small distance of either theme's accent colour.</summary>
    private static int AccentPixels(Rectangle area)
    {
        using var shot = new Bitmap(area.Width, area.Height);
        using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
        var accents = Accents();
        var count = 0;
        for (var y = 0; y < shot.Height; y++)
            for (var x = 0; x < shot.Width; x++)
            {
                var pixel = shot.GetPixel(x, y);
                if (accents.Any(a => Math.Abs(a.R - pixel.R) + Math.Abs(a.G - pixel.G) + Math.Abs(a.B - pixel.B) <= 40)) count++;
            }
        return count;
    }

    /// <summary>The accent colour of each theme, from the generated theme files the panel is built with.</summary>
    private static List<Color> Accents()
    {
        var generated = Path.Combine(Repo(), "src", "Bosun.Panel", "Theme", "Generated");
        return [.. new[] { "Dark.xaml", "Light.xaml" }.Select(file =>
        {
            var match = Regex.Match(File.ReadAllText(Path.Combine(generated, file)), "x:Key=\"C\\.Accent\">#([0-9A-Fa-f]{6})<");
            return match.Success ? ColorTranslator.FromHtml("#" + match.Groups[1].Value) : throw new InvalidDataException($"no C.Accent in {file}");
        })];
    }

    internal static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Bosun.slnx")) && File.Exists(Path.Combine(dir.FullName, "design", "tokens", "colors.json")))
                return dir.FullName;
        throw new InvalidOperationException("no checkout above " + AppContext.BaseDirectory);
    }
}

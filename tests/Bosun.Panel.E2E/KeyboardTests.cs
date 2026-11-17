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
        // The theme choice's selected segment holds the keyboard (a segmented control, D02 T03 §4).
        var theme = Ui.Selected(Ui.Get(window, "ThemeBox"))!;
        theme.SetFocus();
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, theme), Ui.Short, "the theme choice to have focus");

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
    public void A_field_given_the_keyboard_by_Tab_shows_one_outline_not_two()
    {
        // D02 T04 §1: the field turned its own border accent as well as drawing the ring 2 px outside it, two outlines.
        // On screen, after a real Tab: the ring above the field is accent, the field's own top border is not.
        using var panel = PanelProcess.Start();
        var (window, handle) = OnSettings(panel);
        var start = Ui.Get(window, "StartWithWindows");
        start.SetFocus();
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, start), Ui.Short, "Start with Windows to have focus");
        var field = Ui.Get(window, "PressureFloor");
        for (var press = 0; press < 10 && !(Ui.Focused() is { } f && Automation.Compare(f, field)); press++)
        {
            RealInput.Key(handle, Native.VK_TAB);
            Thread.Sleep(80);
        }
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, field) && f.Current.HasKeyboardFocus, Ui.Short, "Tab to give the threshold field the keyboard");
        var box = Ui.Box(field);
        var middle = Rectangle.FromLTRB((int)(box.Left + box.Width * 0.2), 0, (int)(box.Right - box.Width * 0.2), 0);
        // The field's own top border: its first row, along its straight middle.
        var border = Rectangle.FromLTRB(middle.Left, (int)box.Top, middle.Right, (int)box.Top + 1);
        // The ring: the rows just above the field, where FocusRing draws 2 px outside it.
        var ring = Rectangle.FromLTRB(middle.Left, (int)box.Top - 6, middle.Right, (int)box.Top);
        Ui.Until(() => AccentPixels(ring) >= middle.Width, Ui.Short, "the focus ring to be drawn above the field");
        Assert.True(AccentPixels(border) < middle.Width / 10, $"the field's own border is accent too ({AccentPixels(border)} of {middle.Width} pixels): two outlines");
    }

    [Fact]
    public void A_check_box_with_the_keyboard_and_the_pointer_keeps_its_ring_and_its_check()
    {
        // D02 T04 §1: hover turns the box's border accent and focus draws the ring outside the whole control; both at
        // once still show the one ring, and the check stays drawn.
        using var panel = PanelProcess.Start();
        var (window, handle) = OnSettings(panel);
        var notify = Ui.Get(window, "NotifyMemoryPressure");
        var start = Ui.Get(window, "StartWithWindows");
        start.SetFocus();
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, start), Ui.Short, "Start with Windows to have focus");
        for (var press = 0; press < 10 && !(Ui.Focused() is { } f && Automation.Compare(f, notify)); press++)
        {
            RealInput.Key(handle, Native.VK_TAB);
            Thread.Sleep(80);
        }
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, notify) && f.Current.HasKeyboardFocus, Ui.Short, "Tab to give the check box the keyboard");
        var was = Ui.Toggle(notify);
        var box = Ui.Box(notify);
        RealInput.MoveOnWindow(handle, (int)(box.Left + 10), (int)(box.Top + box.Height / 2));
        Thread.Sleep(300);
        var middle = Rectangle.FromLTRB((int)(box.Left + box.Width * 0.2), 0, (int)(box.Right - box.Width * 0.2), 0);
        var ring = Rectangle.FromLTRB(middle.Left, (int)box.Top - 6, middle.Right, (int)box.Top);
        Ui.Until(() => AccentPixels(ring) >= middle.Width, Ui.Short, "the focus ring to stay drawn with the pointer over the check box");
        Assert.Equal(was, Ui.Toggle(notify));
        if (was == ToggleState.On)
        {
            // The checked box is filled with the accent: the check is still drawn under the pointer.
            var check = Rectangle.FromLTRB((int)box.Left + 2, (int)(box.Top + box.Height / 2 - 6), (int)box.Left + 18, (int)(box.Top + box.Height / 2 + 6));
            Assert.True(AccentPixels(check) > 20, "the checked box is no longer drawn checked");
        }
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

    /// <summary>
    /// The theme choice is a segmented control (D02 T03 §4): its three choices named side by side, and Right, Left, Home
    /// and End move the selection and the keyboard by real keys alone. The panel has no combo box any more, so this
    /// replaces the closed combo box test; the combo box's style keeps its own tests (ControlStylesTests).
    /// </summary>
    [Fact]
    public void The_arrow_keys_Home_and_End_move_the_theme_choice_by_keyboard_alone()
    {
        using var panel = PanelProcess.Start();
        var (window, handle) = OnSettings(panel);
        var theme = Ui.Get(window, "ThemeBox");
        var items = theme.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().Select(item => item.Current.Name).ToList();
        Assert.Equal(["Follow Windows", "Dark", "Light"], items);
        Assert.Empty(Ui.Nameless(theme));
        Assert.Equal("Follow Windows", Ui.Selected(theme)?.Current.Name);
        var first = Ui.Selected(theme)!;
        first.SetFocus();
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, first), Ui.Short, "the theme choice to have focus");

        foreach (var (key, then) in new[] { (Native.VK_RIGHT, "Dark"), (Native.VK_RIGHT, "Light"), (Native.VK_RIGHT, "Light"), (Native.VK_LEFT, "Dark"),
                     (Native.VK_HOME, "Follow Windows"), (Native.VK_END, "Light") })
        {
            RealInput.Key(handle, key);
            Ui.Until(() => Ui.Selected(theme)?.Current.Name == then && Ui.Focused()?.Current.Name == then, Ui.Short, $"the key to select and focus {then}");
        }
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

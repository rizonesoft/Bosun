using System.Drawing;
using System.IO;
using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// Contrast on the shown window (D02 T03 §11): the panel started with a preview of Windows' Night sky theme
/// (<c>hc2.theme</c>), read from the screen's pixels on the test screen. A real Tab draws the focus ring in the
/// highlight; the chosen theme segment and the command palette's selected row are the highlight with its text. What
/// an off-screen drawing can't prove, without turning the whole desktop to a contrast theme.
/// </summary>
public class ContrastTests
{
    // Night sky's colours, as hc2.theme gives them (and tests/Bosun.Panel.Tests/Helpers/ContrastPalettes.cs holds).
    static readonly Color Window = Color.FromArgb(0, 0, 0), Highlight = Color.FromArgb(214, 180, 253), HighlightText = Color.FromArgb(43, 43, 43);

    static PanelProcess Previewing() => PanelProcess.StartWith(new Dictionary<string, string?>
    {
        ["BOSUN_PANEL_CONTRAST"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Resources", "Ease of Access Themes", "hc2.theme"),
    });

    static Rectangle Box(AutomationElement element, int grow = 0)
    {
        var b = Ui.Box(element);
        return Rectangle.FromLTRB((int)b.Left - grow, (int)b.Top - grow, (int)b.Right + grow, (int)b.Bottom + grow);
    }

    /// <summary>Pixels in an area within a small distance of a colour.</summary>
    static int Count(Rectangle area, Color colour)
    {
        using var shot = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        var n = 0;
        for (var y = 0; y < shot.Height; y++)
            for (var x = 0; x < shot.Width; x++)
            {
                var p = shot.GetPixel(x, y);
                if (Math.Abs(p.R - colour.R) + Math.Abs(p.G - colour.G) + Math.Abs(p.B - colour.B) <= 24) n++;
            }
        return n;
    }

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void A_real_Tab_draws_the_focus_ring_in_the_highlight_and_the_chosen_segment_is_highlight_on_highlight()
    {
        using var panel = Previewing();
        var (window, handle) = KeyboardTests.OnSettings(panel);
        var whole = Box(window);
        Assert.True(Count(new Rectangle(whole.Left + whole.Width / 2, whole.Bottom - 40, 40, 20), Window) > 600, "the window is drawn in the theme's window colour");

        var theme = Ui.Get(window, "ThemeBox");
        var chosen = Ui.Selected(theme)!;
        var segment = Box(chosen);
        Assert.True(Count(segment, Highlight) > segment.Width * segment.Height / 3, "the chosen segment is the highlight");
        Assert.True(Count(segment, HighlightText) > 10, "its words are the highlight's text");

        chosen.SetFocus();
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, chosen), Ui.Short, "the theme choice to have focus");
        var next = Ui.Get(window, "StartWithWindows");
        var around = Box(next, 8);
        var before = Count(around, Highlight);
        RealInput.Key(handle, Native.VK_TAB);
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, next) && f.Current.HasKeyboardFocus, Ui.Short, "Tab to give Start with Windows the keyboard");
        var outline = (int)(Ui.Box(next).Width + Ui.Box(next).Height);
        var after = 0;
        Ui.Until(() => (after = Count(around, Highlight)) - before >= outline, Ui.Short, $"the focus ring in the highlight ({before} highlight pixels before Tab)");
    }

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void The_command_palettes_selected_row_is_highlight_on_highlight()
    {
        using var panel = Previewing();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        RailTests.Settle(window, "Overview");
        RealInput.Chord(handle, Native.VK_CONTROL, Native.VK_K);
        var results = Ui.Get(window, "Results");
        var row = Ui.Until(() => Ui.Selected(results), Ui.Short, "a selected row");
        var box = Box(row);
        Ui.Until(() => Count(box, Highlight) > box.Width * box.Height / 3, Ui.Short, "the selected row to be the highlight");
        Assert.True(Count(box, HighlightText) > 10, "its words are the highlight's text");
        RealInput.Key(handle, Native.VK_ESCAPE);
    }
}

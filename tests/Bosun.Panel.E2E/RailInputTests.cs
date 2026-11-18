using System.Drawing;
using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// The rail moved by real input on the real panel (D02 T03 §3, completion review): a click on a row and the Down
/// arrow each select the next page, show it, and take the rail's marker to its row, read off the screen where the bar
/// is drawn. Everything here happens on the test screen.
/// </summary>
public class RailInputTests
{
    private const int VK_DOWN = 0x28;

    static AutomationElement RowNamed(AutomationElement window, string name) =>
        Ui.Until(() => window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.NameProperty, name))), Ui.Short, $"the rail row {name}");

    static bool Selected(AutomationElement row) => ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;

    /// <summary>The colour on screen where a row's marker bar is drawn: 8 DIPs in from the row's left, half way down.</summary>
    static Color MarkerSpot(AutomationElement row, double scale)
    {
        var r = row.Current.BoundingRectangle;
        var x = (int)Math.Round(r.Left + (8 + 1.5) * scale);
        var y = (int)Math.Round(r.Top + r.Height / 2);
        using var shot = new Bitmap(1, 1);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
        return shot.GetPixel(0, 0);
    }

    /// <summary>Whether two screen colours are plainly different (the bar against the row's own fill).</summary>
    static bool Differs(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 60;

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void A_click_and_the_Down_arrow_each_select_a_page_show_it_and_take_the_marker_to_its_row()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var scale = TestScreen.Current.Dpi / 96.0;
        var overview = RowNamed(window, "Overview");
        var memory = RowNamed(window, "Memory");
        var instances = RowNamed(window, "Instances");
        Ui.Until(() => Selected(overview), Ui.Short, "Overview to be selected at start");
        Thread.Sleep(500);
        var bare = MarkerSpot(memory, scale); // no bar there yet: the row's own colour

        // A real click on Memory.
        var m = memory.Current.BoundingRectangle;
        RealInput.ClickOnWindow(handle, (int)(m.Left + m.Width / 2), (int)(m.Top + m.Height / 2));
        Ui.Until(() => Selected(memory), Ui.Short, "Memory to be selected by the click");
        // Memory is built (D03 T01 §2): its page is known by its table, not a placeholder's words.
        Ui.Until(() => Ui.Find(window, "ByRepo") is { Current.IsOffscreen: false } table ? table : null, Ui.Short, "the Memory page to be shown");
        Thread.Sleep(500); // past the marker's slide
        Assert.True(Differs(MarkerSpot(memory, scale), bare), $"the marker is drawn on the Memory row ({MarkerSpot(memory, scale)} against {bare})");
        var onMemory = MarkerSpot(memory, scale);

        // The Down arrow, with the rail holding the keyboard after the click.
        RealInput.Key(handle, VK_DOWN);
        Ui.Until(() => Selected(instances), Ui.Short, "Instances to be selected by the Down arrow");
        Ui.Until(() => Ui.Find(window, "Table") is { Current.IsOffscreen: false } table ? table : null, Ui.Short, "the Instances page to be shown");
        Thread.Sleep(500);
        Assert.True(Differs(MarkerSpot(instances, scale), MarkerSpot(memory, scale)),
            $"the marker left Memory for Instances ({MarkerSpot(instances, scale)} on Instances, {MarkerSpot(memory, scale)} on Memory)");
        Assert.True(Differs(MarkerSpot(memory, scale), onMemory), "the bar is gone from Memory");
    }
}

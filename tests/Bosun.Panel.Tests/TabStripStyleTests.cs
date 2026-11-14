using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace Bosun.Panel.Tests;

/// <summary>
/// The tab strip's contract (design/SPEC.md §1), after Steward's TabStripStyleTests: an unselected tab's words are
/// dim, the selected one's are in the accent over a 2 px underline, a disabled one is faded with its tooltip still
/// shown, and each is tall enough to touch. Read from Theme/Controls.xaml itself, so a changed setter fails here.
/// The colours are the header's, not the tab's, so the page a tab holds does not inherit them (D02 T01 §4).
/// </summary>
public class TabStripStyleTests
{
    static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    static string Controls() => File.ReadAllText(Path.Combine(TestPaths.Panel(), "Theme", "Controls.xaml"));

    /// <summary>What is wrong with the tab's style in a Controls.xaml text; nothing when it keeps the contract.</summary>
    static List<string> Problems(string xaml)
    {
        var problems = new List<string>();
        void Want(bool holds, string what) { if (!holds) problems.Add(what); }

        var styles = XDocument.Parse(xaml).Descendants(Presentation + "Style").ToList();
        var item = styles.SingleOrDefault(s => (string?)s.Attribute(Xaml + "Key") == "TabStrip.Item");
        if (item is null) return ["there is no TabStrip.Item style"];

        string? Setter(XElement scope, string property, string? target = null) =>
            (string?)scope.Elements(Presentation + "Setter")
                .SingleOrDefault(s => (string?)s.Attribute("Property") == property && (string?)s.Attribute("TargetName") == target)?.Attribute("Value");
        XElement? Trigger(string property) => item.Descendants(Presentation + "Trigger").SingleOrDefault(t => (string?)t.Attribute("Property") == property);

        Want(double.TryParse(Setter(item, "MinHeight"), NumberStyles.Float, CultureInfo.InvariantCulture, out var height) && height >= 44, "a tab is at least 44 px high");
        Want(Setter(item, "ToolTipService.ShowOnDisabled") == "True", "a disabled tab still shows its tooltip");
        // Set on the tab, the header's colour and weight would reach the tab's content, which is its child: the tab
        // carries the page's text colour for that content, and the header carries its own.
        Want(Setter(item, "Foreground") == "{DynamicResource B.Text}" && Setter(item, "FontWeight") is null, "the header's colour and weight are not set on the tab, whose content would inherit them");

        var header = item.Descendants(Presentation + "ContentPresenter").SingleOrDefault(c => (string?)c.Attribute("ContentSource") == "Header");
        Want(header is not null && (string?)header.Attribute(Xaml + "Name") == "header", "the header is the template's named 'header' presenter");
        Want((string?)header?.Attribute("TextElement.Foreground") == "{DynamicResource B.TextDim}", "an unselected tab's words are B.TextDim");

        var selected = Trigger("IsSelected");
        Want(selected is not null && Setter(selected, "TextElement.Foreground", "header") == "{DynamicResource B.Accent}", "the selected tab's words are B.Accent");
        Want(selected is not null && Setter(selected, "BorderBrush", "bd") == "{DynamicResource B.Accent}", "the selected tab's underline is B.Accent");
        Want(item.Descendants(Presentation + "Border").Any(b => (string?)b.Attribute(Xaml + "Name") == "bd" && (string?)b.Attribute("BorderThickness") == "0,0,0,2"), "the underline is 2 px, under the tab");

        var disabled = Trigger("IsEnabled");
        Want(disabled is not null && (string?)disabled.Attribute("Value") == "False" && Setter(disabled, "Opacity", "bd") == "0.4", "a disabled tab is at 0.4 opacity");

        var strip = styles.SingleOrDefault(s => (string?)s.Attribute(Xaml + "Key") == "TabStrip");
        Want(strip is not null && Setter(strip, "ItemContainerStyle") == "{StaticResource TabStrip.Item}", "the strip's tabs take TabStrip.Item");
        return problems;
    }

    [Fact]
    public void Tabs_select_in_the_accent_with_an_underline_dim_otherwise_and_are_tall_enough_to_touch()
    {
        var problems = Problems(Controls());
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Theory]
    [InlineData("<Setter Property=\"MinHeight\" Value=\"44\"/>", "<Setter Property=\"MinHeight\" Value=\"30\"/>", "a tab is at least 44 px high")]
    [InlineData("TextElement.Foreground=\"{DynamicResource B.TextDim}\"", "TextElement.Foreground=\"{DynamicResource B.Text}\"", "an unselected tab's words are B.TextDim")]
    [InlineData("<Setter TargetName=\"header\" Property=\"TextElement.Foreground\" Value=\"{DynamicResource B.Accent}\"/>", "<Setter TargetName=\"header\" Property=\"TextElement.Foreground\" Value=\"{DynamicResource B.Text}\"/>", "the selected tab's words are B.Accent")]
    [InlineData("BorderThickness=\"0,0,0,2\"", "BorderThickness=\"0\"", "the underline is 2 px, under the tab")]
    [InlineData("<Setter Property=\"Foreground\" Value=\"{DynamicResource B.Text}\"/>", "<Setter Property=\"Foreground\" Value=\"{DynamicResource B.TextDim}\"/>", "the header's colour and weight are not set on the tab, whose content would inherit them")]
    public void A_changed_setter_fails_by_name(string from, string to, string names)
    {
        var text = Controls();
        // The first place the text stands is the tab's own style or template, for each of these.
        var at = text.IndexOf(from, text.IndexOf("x:Key=\"TabStrip.Item\"", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(at >= 0, $"the file no longer holds: {from}");
        Assert.Contains(names, Problems(text[..at] + to + text[(at + from.Length)..]));
    }

    [Fact]
    public void A_disabled_tab_that_hides_its_tooltip_or_is_not_faded_fails()
    {
        var text = Controls();
        var start = text.IndexOf("x:Key=\"TabStrip.Item\"", StringComparison.Ordinal);
        var end = text.IndexOf("x:Key=\"TabStrip\"", start, StringComparison.Ordinal);
        string InItem(string from, string to)
        {
            var at = text.IndexOf(from, start, StringComparison.Ordinal);
            Assert.InRange(at, start, end);
            return text[..at] + to + text[(at + from.Length)..];
        }
        Assert.Contains("a disabled tab still shows its tooltip", Problems(InItem("<Setter Property=\"ToolTipService.ShowOnDisabled\" Value=\"True\"/>", "")));
        Assert.Contains("a disabled tab is at 0.4 opacity", Problems(InItem("<Setter TargetName=\"bd\" Property=\"Opacity\" Value=\"0.4\"/>", "<Setter TargetName=\"bd\" Property=\"Opacity\" Value=\"1\"/>")));
    }
}

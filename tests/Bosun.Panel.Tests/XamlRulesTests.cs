using System.IO;
using Xunit;

namespace Bosun.Panel.Tests;

/// <summary>
/// No colour or font size is written outside Theme/Generated, in the panel's XAML or its code (D02 T01 §2), and the
/// rule that says so fails each way a stray one can be written.
/// </summary>
public class XamlRulesTests
{
    const string Open = "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";
    const string Close = "</Grid>";

    static List<Violation> Xaml(string inner) => XamlRules.Violations("View.xaml", Open + "\n" + inner + "\n" + Close);
    static List<Violation> Code(string line) => XamlRules.Violations("View.xaml.cs", "class V {\n    void M() {\n        " + line + "\n    }\n}\n");

    [Fact]
    public void A_Lucide_kind_is_named_only_through_the_icon_map()
    {
        // D02 T03 §3: a kind written in a view or named in code elsewhere fails, by its file and line.
        const string icons = "xmlns:icons=\"http://metro.mahapps.com/winfx/xaml/iconpacks\"";
        var view = XamlRules.Violations("View.xaml", $"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" {icons}>\n<icons:PackIconLucide Kind=\"Gauge\"/>\n</Grid>");
        Assert.Equal("Lucide kind Gauge written in a view: name it through Theme/Glyphs.cs", Assert.Single(view).What);
        Assert.Empty(XamlRules.Violations("View.xaml", $"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" {icons}>\n<icons:PackIconLucide Kind=\"{{Binding Key, Converter={{StaticResource PageGlyph}}}}\"/>\n</Grid>"));
        Assert.Equal("Lucide kind PackIconLucideKind.Gauge named in code: name it through Theme/Glyphs.cs", Assert.Single(Code("var k = PackIconLucideKind.Gauge;")).What);
        Assert.Empty(XamlRules.Violations("Theme/Glyphs.cs", "class G { object k = PackIconLucideKind.Gauge; }"));
    }

    [Fact]
    public void Focus_is_a_control_s_focus_visual_alone_never_a_trigger_beside_it()
    {
        // D02 T04 §1: Field turned its border accent on focus as well as drawing its ring, a double outline.
        foreach (var property in new[] { "IsKeyboardFocused", "IsFocused", "IsKeyboardFocusWithin" })
        {
            var trigger = Xaml($"<Grid.Style><Style><Style.Triggers><Trigger Property=\"{property}\" Value=\"True\"><Setter Property=\"Tag\" Value=\"x\"/></Trigger></Style.Triggers></Style></Grid.Style>");
            Assert.Equal($"a trigger on {property}: focus is the control's FocusVisualStyle alone, never a second change beside it", Assert.Single(trigger).What);
            var condition = Xaml($"<Grid.Style><Style><Style.Triggers><MultiTrigger><MultiTrigger.Conditions><Condition Property=\"{property}\" Value=\"True\"/></MultiTrigger.Conditions></MultiTrigger></Style.Triggers></Style></Grid.Style>");
            Assert.Single(condition);
        }
        Assert.Empty(Xaml("<Grid.Style><Style><Style.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\"><Setter Property=\"Tag\" Value=\"x\"/></Trigger></Style.Triggers></Style></Grid.Style>"));
    }

    [Fact]
    public void A_status_pill_is_never_written_without_its_word()
    {
        // D02 T03 §4: a pill with no word, or an empty one, fails by its file and line; one with a word or a binding passes.
        const string controls = "xmlns:c=\"clr-namespace:Bosun.Panel.Controls\"";
        List<Violation> View(string pill) => XamlRules.Violations("View.xaml", $"<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" {controls}>\n{pill}\n</Grid>");
        Assert.Equal("a StatusPill with no Word: status is never colour alone, so give it its word", Assert.Single(View("<c:StatusPill Kind=\"Ok\"/>")).What);
        Assert.Equal(2, Assert.Single(View("<c:StatusPill Kind=\"Ok\"/>")).Line);
        Assert.Equal("a StatusPill with an empty Word: status is never colour alone, so give it its word", Assert.Single(View("<c:StatusPill Kind=\"Ok\" Word=\" \"/>")).What);
        Assert.Empty(View("<c:StatusPill Kind=\"Ok\" Word=\"Running\"/>"));
        Assert.Empty(View("<c:StatusPill Kind=\"Ok\" Word=\"{Binding State}\"/>"));
    }

    [Fact]
    public void The_panel_writes_no_colour_or_font_size_outside_the_generated_theme()
    {
        var (violations, read) = XamlRules.Scan(TestPaths.Panel());
        Assert.True(violations.Count == 0, string.Join("\n", violations));
        // The scan saw the panel: its XAML, its code, and not the one place colours live.
        Assert.Contains("App.xaml", read);
        Assert.Contains(read, f => f.EndsWith(".cs", StringComparison.Ordinal));
        Assert.DoesNotContain(read, f => f.Contains("Theme/Generated", StringComparison.Ordinal));
        Assert.True(File.Exists(TestPaths.GeneratedTheme("Dark.xaml")), "the generated theme is there to be left out");
    }

    [Theory]
    [InlineData("<Border Background=\"#1D242E\"/>", "colour #1D242E")]
    [InlineData("<Border Background=\"#FFF\"/>", "colour #FFF")]
    [InlineData("<Border BorderBrush=\"#801D242E\"/>", "colour #801D242E")]
    [InlineData("<TextBlock Foreground=\"Red\"/>", "colour Red")]
    [InlineData("<TextBlock TextElement.Foreground=\"White\"/>", "colour White")]
    [InlineData("<Path Fill=\"Black\" Stroke=\"{StaticResource B.Line}\"/>", "colour Black")]
    [InlineData("<Border><Border.Background>Red</Border.Background></Border>", "colour Red")]
    [InlineData("<Border><Border.Background><SolidColorBrush Color=\"#102030\"/></Border.Background></Border>", "colour #102030")]
    [InlineData("<Grid.Resources><Color x:Key=\"Mine\">#102030</Color></Grid.Resources>", "colour #102030")]
    [InlineData("<Grid.Resources><Color x:Key=\"Mine\">Red</Color></Grid.Resources>", "colour Red")]
    [InlineData("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"Foreground\" Value=\"#FFFFFF\"/></Style></Grid.Resources>", "colour #FFFFFF")]
    [InlineData("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"Foreground\" Value=\"Red\"/></Style></Grid.Resources>", "colour Red")]
    [InlineData("<Grid.Resources><Style TargetType=\"Border\"><Setter Property=\"Background\"><Setter.Value>#102030</Setter.Value></Setter></Style></Grid.Resources>", "colour #102030")]
    [InlineData("<Rectangle><Rectangle.Fill><LinearGradientBrush><GradientStop Color=\"#000000\" Offset=\"0\"/></LinearGradientBrush></Rectangle.Fill></Rectangle>", "colour #000000")]
    [InlineData("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"Foreground\"><Setter.Value>Red</Setter.Value></Setter></Style></Grid.Resources>", "colour Red")]
    [InlineData("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"FontSize\"><Setter.Value>13</Setter.Value></Setter></Style></Grid.Resources>", "font size 13")]
    // A size with a unit is as hard-coded as one without (completion review): WPF reads px, pt, in and cm.
    [InlineData("<TextBlock FontSize=\"12pt\"/>", "font size 12pt")]
    [InlineData("<TextBlock FontSize=\"16 px\"/>", "font size 16 px")]
    [InlineData("<TextBlock FontSize=\"0.5cm\"/>", "font size 0.5cm")]
    [InlineData("<TextBlock><TextBlock.FontSize>1in</TextBlock.FontSize></TextBlock>", "font size 1in")]
    [InlineData("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"FontSize\" Value=\"10PT\"/></Style></Grid.Resources>", "font size 10PT")]
    [InlineData("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"FontSize\"><Setter.Value>11pt</Setter.Value></Setter></Style></Grid.Resources>", "font size 11pt")]
    [InlineData("<TextBlock FontSize=\"13\"/>", "font size 13")]
    [InlineData("<TextBlock TextElement.FontSize=\"12.5\"/>", "font size 12.5")]
    [InlineData("<TextBlock><TextBlock.FontSize>13</TextBlock.FontSize></TextBlock>", "font size 13")]
    [InlineData("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"FontSize\" Value=\"11\"/></Style></Grid.Resources>", "font size 11")]
    [InlineData("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"TextElement.FontSize\" Value=\"11\"/></Style></Grid.Resources>", "font size 11")]
    // D02 T03 §1: a rounding, a shadow's blur and an animation's length are the theme's too.
    [InlineData("<Border CornerRadius=\"8\"/>", "CornerRadius 8 written in CornerRadius: use an R.* resource")]
    [InlineData("<Border CornerRadius=\"8,8,0,0\"/>", "CornerRadius 8,8,0,0")]
    [InlineData("<Border><Border.CornerRadius>12</Border.CornerRadius></Border>", "CornerRadius 12")]
    [InlineData("<Grid.Resources><Style TargetType=\"Border\"><Setter Property=\"CornerRadius\" Value=\"6\"/></Style></Grid.Resources>", "CornerRadius 6 written in a Setter")]
    [InlineData("<Border><Border.Effect><DropShadowEffect BlurRadius=\"20\" ShadowDepth=\"{StaticResource S.M}\"/></Border.Effect></Border>", "BlurRadius 20 written in BlurRadius: use an Elevation.* resource")]
    [InlineData("<Grid.Resources><Storyboard x:Key=\"Fade\"><DoubleAnimation Duration=\"0:0:0.2\" To=\"1\"/></Storyboard></Grid.Resources>", "Duration 0:0:0.2 written in Duration: use Theme/Motion.cs")]
    public void A_stray_colour_or_font_size_in_a_view_fails(string inner, string names)
    {
        var violations = Xaml(inner);
        Assert.Single(violations);
        Assert.Contains(names, violations[0].What, StringComparison.Ordinal);
        Assert.Equal("View.xaml", violations[0].File);
        Assert.Equal(2, violations[0].Line);  // the fixture's second line
    }

    [Theory]
    [InlineData("<TextBlock Foreground=\"{StaticResource B.Text}\" FontSize=\"{StaticResource T.Body}\"/>")]
    [InlineData("<TextBlock Foreground=\"{DynamicResource B.TextDim}\" FontSize=\"{DynamicResource T.Body}\"/>")]
    [InlineData("<Border Background=\"Transparent\" BorderBrush=\"{x:Null}\"/>")]
    [InlineData("<TextBlock Text=\"Issue #123\"/>")]
    [InlineData("<TextBlock Text=\"#123\"/>")]
    [InlineData("<TextBlock>#123</TextBlock>")]
    [InlineData("<Button Content=\"#abc\" ToolTip=\"#abcdef\"/>")]
    [InlineData("<Border><Border.Background><SolidColorBrush Color=\"{StaticResource C.Base}\"/></Border.Background></Border>")]
    [InlineData("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"FontSize\" Value=\"{StaticResource T.Caption}\"/><Setter Property=\"Margin\" Value=\"13\"/></Style></Grid.Resources>")]
    [InlineData("<Grid.RowDefinitions><RowDefinition Height=\"44\"/></Grid.RowDefinitions>")]
    // No rounding and no motion are not token values; a resource is the token.
    [InlineData("<Border CornerRadius=\"0\"/>")]
    [InlineData("<Border CornerRadius=\"0,0,0,0\" Effect=\"{StaticResource Elevation.Card}\"/>")]
    [InlineData("<Border CornerRadius=\"{DynamicResource R.Card}\"/>")]
    [InlineData("<Grid.Resources><Storyboard x:Key=\"Now\"><DoubleAnimation Duration=\"0:0:0\" To=\"1\"/><DoubleAnimation Duration=\"Automatic\" To=\"1\"/></Storyboard></Grid.Resources>")]
    public void The_theme_s_resources_and_words_for_people_pass(string inner) => Assert.Empty(Xaml(inner));

    [Theory]
    [InlineData("var c = (Color)ColorConverter.ConvertFromString(\"#1D242E\");", 2)]  // the converter and the string
    [InlineData("var b = new SolidColorBrush(Color.FromRgb(1, 2, 3));", 1)]
    [InlineData("var b = new SolidColorBrush(Color.FromArgb(128, 1, 2, 3));", 1)]
    [InlineData("label.Foreground = Brushes.Red;", 1)]
    [InlineData("var c = Colors.White;", 1)]
    [InlineData("var hex = \"#FFAABBCC\";", 1)]
    [InlineData("label.FontSize = 13;", 1)]
    [InlineData("var t = new TextBlock { FontSize = 12.5, Text = \"x\" };", 1)]
    [InlineData("label.SetValue(TextBlock.FontSizeProperty, 13.0);", 1)]
    [InlineData("style.Setters.Add(new Setter(Control.FontSizeProperty, 11d));", 1)]
    [InlineData("var shadow = new DropShadowEffect { BlurRadius = 20 };", 1)]
    [InlineData("card.CornerRadius = new CornerRadius(12);", 1)]
    [InlineData("var fade = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(220)));", 1)]
    [InlineData("shadow.SetValue(DropShadowEffect.BlurRadiusProperty, 20.0);", 1)]
    [InlineData("style.Setters.Add(new Setter(Border.CornerRadiusProperty, 8d));", 1)]
    // Completion review: the forms C# writes most, a TimeSpan given where a Duration goes and a target-typed radius.
    [InlineData("fade.Duration = TimeSpan.FromMilliseconds(220);", 1)]
    [InlineData("var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));", 1)]
    [InlineData("card.CornerRadius = new(12);", 1)]
    public void A_stray_colour_or_font_size_in_code_fails(string line, int count)
    {
        var violations = Code(line);
        Assert.Equal(count, violations.Count);
        Assert.All(violations, v => Assert.Equal(3, v.Line));  // the fixture's third line
        Assert.All(violations, v => Assert.Equal("View.xaml.cs", v.File));
    }

    [Theory]
    [InlineData("label.Foreground = (Brush)FindResource(\"B.Text\");")]
    [InlineData("label.FontSize = (double)FindResource(\"T.Body\");")]
    [InlineData("label.SetResourceReference(TextBlock.FontSizeProperty, \"T.Body\");")]
    [InlineData("label.Background = Brushes.Transparent; var c = Colors.Transparent;")]
    [InlineData("if (label.FontSize >= 13 || label.FontSize == 11) return;")]
    [InlineData("var text = \"Issue #123 is fixed\"; // was #FF0000, or Brushes.Red")]
    [InlineData("var MinFontSize = 5; var url = \"https://example.org/#fff\";")]
    [InlineData("var square = new CornerRadius(0); var now = new Duration(TimeSpan.Zero); var none = new Duration(TimeSpan.FromMilliseconds(0));")]
    [InlineData("shadow.SetValue(DropShadowEffect.BlurRadiusProperty, 0.0); card.SetResourceReference(Border.CornerRadiusProperty, \"R.Card\");")]
    [InlineData("fade.Duration = TimeSpan.FromMilliseconds(0); var now = new DoubleAnimation(0, 1, TimeSpan.Zero); card.CornerRadius = new(0);")]
    [InlineData("var fade = new DoubleAnimation(0, 1, motion.Normal); var every = TimeSpan.FromSeconds(5);")]
    [InlineData("var fade = new DoubleAnimation(1, motion.Normal); card.Effect = (Effect)FindResource(\"Elevation.Card\");")]
    public void Theme_lookups_comparisons_and_comments_pass(string line) => Assert.Empty(Code(line));

    [Theory]
    [InlineData("label.FontSize =\n            13;", "font size")]
    [InlineData("label.Foreground = Brushes.\n            Red;", "colour Brushes.Red")]
    [InlineData("var b = new SolidColorBrush(Color\n            .FromRgb(1, 2, 3));", "colour made in code")]
    [InlineData("label.SetValue(TextBlock.FontSizeProperty,\n            13.0);", "font size")]
    public void A_statement_broken_over_lines_still_fails_on_the_line_it_starts(string statement, string names)
    {
        var violations = Code(statement);
        Assert.Single(violations);
        Assert.Contains(names, violations[0].What, StringComparison.Ordinal);
        Assert.Equal(3, violations[0].Line);
    }

    // Completion review: a block comment neither hides a literal nor makes commented-out code count.
    [Theory]
    [InlineData("label.FontSize = /* caption */ 13;", "font size")]
    [InlineData("label.Foreground = Brushes./* stop */Red;", "colour Brushes.Red")]
    [InlineData("var s = \"/* not a comment\"; label.FontSize = 13; // */", "font size")]
    [InlineData("var q = '\"'; label.FontSize = 13;", "font size")]
    [InlineData("var v = @\"C:\\\"; label.FontSize = 13;", "font size")]
    public void A_block_comment_or_a_string_does_not_hide_a_literal(string statement, string names)
    {
        var violations = Code(statement);
        Assert.Single(violations);
        Assert.Contains(names, violations[0].What, StringComparison.Ordinal);
        Assert.Equal(3, violations[0].Line);
    }

    [Theory]
    [InlineData("/* old: label.FontSize = 13; label.Foreground = Brushes.Red; */")]
    [InlineData("/* old:\n            label.FontSize = 13;\n            var c = \"#FF0000\";\n        */ label.FontSize = (double)FindResource(\"T.Body\");")]
    [InlineData("var s = \"a // and a /* inside a string start no comment\"; var size = (double)FindResource(\"T.Body\");")]
    public void Commented_out_code_is_not_code(string statement) => Assert.Empty(Code(statement));

    [Fact]
    public void A_literal_after_a_block_comment_is_reported_on_its_own_line()
    {
        var violations = Code("/* one\n           two */\n        label.FontSize = 13;");
        Assert.Single(violations);
        Assert.Equal(5, violations[0].Line);  // the comment's two lines come first
    }

    [Fact]
    public void A_setter_s_value_element_with_a_margin_or_a_resource_passes()
    {
        Assert.Empty(Xaml("<Grid.Resources><Style TargetType=\"TextBlock\"><Setter Property=\"Margin\"><Setter.Value>13</Setter.Value></Setter><Setter Property=\"Foreground\"><Setter.Value><StaticResource ResourceKey=\"B.Text\"/></Setter.Value></Setter></Style></Grid.Resources>"));
    }

    [Fact]
    public void XAML_that_is_not_well_formed_is_a_violation_not_a_pass()
    {
        var violations = XamlRules.Violations("Broken.xaml", "<Grid><Border Background=\"#000000\"></Grid>");
        Assert.Single(violations);
        Assert.Contains("not well-formed", violations[0].What, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scan_reads_nested_views_and_code_and_leaves_out_the_generated_theme()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosun-xamlrules-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Put(string relative, string text)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            var view = Open + "\n<Border Background=\"#102030\"/>\n" + Close;
            Put("Views/Deep/A.xaml", view);
            Put("Shell/B.cs", "class B {\n    object o = Brushes.Red;\n}\n");
            Put("Theme/Generated/Dark.xaml", view);        // the one place colours live
            Put("obj/Debug/G.g.cs", "class G { object o = Brushes.Red; }\n");  // build output
            Put("Views/Clean.xaml", Open + "<Border Background=\"{StaticResource B.Base}\"/>" + Close);
            Put("Notes.txt", "#102030");

            var (violations, read) = XamlRules.Scan(root);

            Assert.Equal(["Shell/B.cs", "Views/Clean.xaml", "Views/Deep/A.xaml"], read);
            Assert.Equal(2, violations.Count);
            Assert.Contains(violations, v => v.File == "Views/Deep/A.xaml" && v.Line == 2 && v.What.Contains("#102030", StringComparison.Ordinal));
            Assert.Contains(violations, v => v.File == "Shell/B.cs" && v.Line == 2 && v.What.Contains("Brushes.Red", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

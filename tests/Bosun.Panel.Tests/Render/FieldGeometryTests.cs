using System.Windows;
using System.Windows.Controls;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Render;

/// <summary>
/// Fields line up (D02 T04 §2): a field's text and a combo box's text start the same distance inside their borders, and
/// a field label sits the space scale's gap above its control, in every theme.
/// </summary>
public class FieldGeometryTests
{
    /// <summary>The first column, from the control's left edge, where the middle rows hold text: not the fill, not the border.</summary>
    static int TextStart(uint[] pixels, int width, Rect box)
    {
        var left = (int)Math.Round(box.Left);
        var right = (int)Math.Round(box.Right);
        var mid = (int)Math.Round(box.Top + box.Height / 2);
        var fill = pixels[mid * width + left + 4];
        static int Diff(uint a, uint b) =>
            Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF)) + Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)) + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
        for (var x = left + 3; x < right - 3; x++)
            for (var y = mid - 5; y <= mid + 5; y++)
                if (Diff(pixels[y * width + x], fill) > 120) return x - left;
        throw new Xunit.Sdk.XunitException("no text found in the control");
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("HighContrast")]
    public void A_field_s_text_and_a_combo_box_s_start_at_one_inset(string theme)
    {
        PageRenderer.InPage(theme, () =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Top };
            var field = new TextBox { Text = "Windows", Width = 160 };
            field.SetResourceReference(FrameworkElement.StyleProperty, "Field");
            panel.Children.Add(field);
            panel.Children.Add(new ComboBox { ItemsSource = new[] { "Windows" }, SelectedIndex = 0, Width = 160, Margin = new Thickness(12, 0, 0, 0) });
            return panel;
        }, page =>
        {
            var pixels = PageRenderer.Pixels(page);
            var field = ShellSupport.Under<TextBox>(page).Single();
            var combo = ShellSupport.Under<ComboBox>(page).Single();
            int Start(FrameworkElement c) => TextStart(pixels, (int)page.Width, c.TransformToAncestor(page).TransformBounds(new Rect(c.RenderSize)));
            var (inField, inCombo) = (Start(field), Start(combo));
            Assert.True(Math.Abs(inField - inCombo) <= 1, $"the field's text starts {inField} px in and the combo box's {inCombo} px in");
            return 0;
        });
    }

    static uint Edge(Border page, FrameworkElement field)
    {
        var box = field.TransformToAncestor(page).TransformBounds(new Rect(field.RenderSize));
        var pixels = PageRenderer.Pixels(page);
        return pixels[(int)Math.Round(box.Top) * (int)page.Width + (int)Math.Round(box.Left + box.Width / 2)];
    }

    static uint Colour(Border page, string key) => ((System.Windows.Media.SolidColorBrush)page.FindResource(key)).Color is var c
        ? (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B) : 0;

    static bool Near(uint a, uint b) =>
        Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF)) + Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)) + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) <= 30;

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("HighContrast")]
    public void An_invalid_field_draws_its_border_in_the_danger_colour_and_clears_it(string theme)
    {
        PageRenderer.InPage(theme, () =>
        {
            var field = new TextBox { Text = "lots", Width = 160, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            field.SetResourceReference(FrameworkElement.StyleProperty, "Field");
            return field;
        }, page =>
        {
            var field = (TextBox)page.Child;
            Assert.True(Near(Edge(page, field), Colour(page, "B.Line")), "a valid field's border is the line colour");
            Bosun.Panel.Theme.FieldState.SetInvalid(field, true);
            PageRenderer.Settle(page);
            Assert.True(Near(Edge(page, field), Colour(page, "B.Danger")), $"an invalid field's border is the danger colour in {theme}");
            Bosun.Panel.Theme.FieldState.SetInvalid(field, false);
            PageRenderer.Settle(page);
            Assert.True(Near(Edge(page, field), Colour(page, "B.Line")), "and the line colour again once it is valid");
            return 0;
        });
    }

    [Fact]
    public void The_threshold_field_turns_danger_as_it_is_typed_wrong_and_back_as_it_is_corrected_or_discarded()
    {
        var fixture = new Settings.SettingsFixture().Open();
        PageRenderer.InPage("Dark", () => new Bosun.Panel.Settings.SettingsView { DataContext = fixture.Page }, page =>
        {
            var field = ShellSupport.Under<TextBox>(page).Single(t => t.Name == "PressureFloor");
            var stored = field.Text;
            field.Text = "lots";
            PageRenderer.Settle(page);
            Assert.True(Bosun.Panel.Theme.FieldState.GetInvalid(field));
            Assert.True(Near(Edge(page, field), Colour(page, "B.Danger")), "typed wrong: the danger border");
            field.Text = "4096";
            PageRenderer.Settle(page);
            Assert.False(Bosun.Panel.Theme.FieldState.GetInvalid(field));
            Assert.True(Near(Edge(page, field), Colour(page, "B.Line")), "corrected: the line colour");
            field.Text = "lots";
            PageRenderer.Settle(page);
            fixture.Page.Discard();
            PageRenderer.Settle(page);
            Assert.Equal(stored, field.Text);
            Assert.True(Near(Edge(page, field), Colour(page, "B.Line")), "discarded: the line colour, the stored value back");
            return 0;
        }, 900, 700);
    }

    [Fact]
    public void A_field_label_is_the_scale_s_gap_above_its_control_on_the_Settings_page()
    {
        PageRenderer.InPage("Dark", () => new Bosun.Panel.Settings.SettingsView { DataContext = new Settings.SettingsFixture().Page }, page =>
        {
            var label = ShellSupport.Under<TextBlock>(page).Single(t => t.Name == "FloorLabel");
            var field = ShellSupport.Under<TextBox>(page).Single(t => t.Name == "PressureFloor");
            var gap = field.TransformToAncestor(page).Transform(new Point()).Y - label.TransformToAncestor(page).Transform(new Point(0, label.ActualHeight)).Y;
            Assert.Equal((double)page.FindResource("S.S"), gap, 0.5);
            return 0;
        }, 900, 700);
    }
}

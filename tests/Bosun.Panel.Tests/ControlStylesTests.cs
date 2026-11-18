using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using Bosun.Panel.Tests.Render;
using Xunit;

namespace Bosun.Panel.Tests;

/// <summary>
/// The control styles of Theme/Controls.xaml (D02 T01 §4): each is applied to a live control in each theme, in a page
/// that is never shown, and read back: its colours are the theme's, and the control still works in it (a press, a
/// refusal while disabled, the keyboard). The rules over the file's text (every style a 32 px target with a focus
/// ring, every theme key real) are proved to fail on a copy that breaks them.
/// </summary>
public partial class ControlStylesTests
{
    // ---- helpers ----

    static Color Token(string theme, string name)
    {
        using var tokens = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(TestPaths.Repo(), "design", "tokens", "colors.json")));
        var digits = tokens.RootElement.GetProperty(theme.ToLowerInvariant()).GetProperty(name).GetString()!.TrimStart('#');
        var n = uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return digits.Length == 6
            ? Color.FromArgb(0xFF, (byte)(n >> 16), (byte)(n >> 8), (byte)n)
            : Color.FromArgb((byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n);
    }

    static uint Pixel(string theme, string name)
    {
        var c = Token(theme, name);
        return (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
    }

    static string ControlsText() => File.ReadAllText(System.IO.Path.Combine(TestPaths.Panel(), "Theme", "Controls.xaml"));

    /// <summary>A brush's colour; a brush that isn't there, or isn't one flat colour, is null.</summary>
    static Color? Paint(object? brush) => (brush as SolidColorBrush)?.Color;

    static T Part<T>(Control control, string name) where T : class =>
        control.Template.FindName(name, control) as T ?? throw new InvalidOperationException($"{control.GetType().Name}'s template has no {typeof(T).Name} named '{name}'");

    static IEnumerable<T> Under<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var deeper in Under<T>(child)) yield return deeper;
        }
    }

    static T Styled<T>(string style, Action<T>? set = null) where T : FrameworkElement, new()
    {
        var element = new T();
        element.SetResourceReference(FrameworkElement.StyleProperty, style);
        set?.Invoke(element);
        return element;
    }

    /// <summary>What a setter gives in <paramref name="scope"/>: a theme key is looked up there, as the control would.</summary>
    static object? Resolved(FrameworkElement scope, object? value) => value is DynamicResourceExtension key ? scope.TryFindResource(key.ResourceKey) : value;

    static Setter? SetterOf(IEnumerable<SetterBase> setters, DependencyProperty property, string? target = null) =>
        setters.OfType<Setter>().LastOrDefault(s => s.Property == property && s.TargetName == target);

    static Color? ColourOf(FrameworkElement scope, IEnumerable<SetterBase> setters, DependencyProperty property, string? target = null) =>
        SetterOf(setters, property, target) is { } setter ? Paint(Resolved(scope, setter.Value)) : null;

    static Trigger TriggerOn(IEnumerable<TriggerBase> triggers, DependencyProperty property, object value) =>
        triggers.OfType<Trigger>().Single(t => t.Property == property && Equals(t.Value, value));

    /// <summary>The style's own trigger on a property, or the nearest one of the styles it is based on.</summary>
    static Trigger StyleTrigger(Style style, DependencyProperty property, object value)
    {
        for (var s = style; s is not null; s = s.BasedOn)
            if (s.Triggers.OfType<Trigger>().SingleOrDefault(t => t.Property == property && Equals(t.Value, value)) is { } found) return found;
        throw new InvalidOperationException($"no trigger on {property.Name}");
    }

    static void Live(string theme, Func<FrameworkElement> create, Action<Border> check, int width = 480, int height = 320) =>
        PageRenderer.InPage(theme, create, page =>
        {
            check(page);
            return true;
        }, width, height);

    static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("the control is not in a page");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    static T Pattern<T>(UIElement element, PatternInterface pattern) where T : class =>
        UIElementAutomationPeer.CreatePeerForElement(element).GetPattern(pattern) as T ?? throw new InvalidOperationException($"{element.GetType().Name} has no {pattern} pattern");

    public static TheoryData<string> Themes => ["Dark", "Light"];

    // ---- buttons ----

    [Theory]
    [InlineData("Dark", "Btn", "Raised", "Line", "Text", "Hover", "LineStrong")]
    [InlineData("Light", "Btn", "Raised", "Line", "Text", "Hover", "LineStrong")]
    [InlineData("Dark", "Btn.Small", "Raised", "Line", "Text", "Hover", "LineStrong")]
    [InlineData("Light", "Btn.Small", "Raised", "Line", "Text", "Hover", "LineStrong")]
    [InlineData("Dark", "Btn.Primary", "Accent", "Accent", "OnAccent", "Accent", "Text")]
    [InlineData("Light", "Btn.Primary", "Accent", "Accent", "OnAccent", "Accent", "Text")]
    [InlineData("Dark", "Btn.Danger", null, "Danger", "Danger", "DangerSoft", "Danger")]
    [InlineData("Light", "Btn.Danger", null, "Danger", "Danger", "DangerSoft", "Danger")]
    public void A_button_draws_in_its_intents_colours_at_rest_under_the_pointer_pressed_and_disabled(
        string theme, string style, string? fill, string border, string words, string hoverFill, string hoverBorder)
    {
        Button button = null!;
        Live(theme, () => button = Styled<Button>(style, b => { b.Content = "Save"; b.HorizontalAlignment = HorizontalAlignment.Left; b.VerticalAlignment = VerticalAlignment.Top; }), page =>
        {
            Assert.Equal(fill is null ? Colors.Transparent : Token(theme, fill), Paint(button.Background));
            Assert.Equal(Token(theme, border), Paint(button.BorderBrush));
            Assert.Equal(Token(theme, words), Paint(button.Foreground));
            // The template draws what the style says: the border part carries the button's own brushes.
            var bd = Part<Border>(button, "bd");
            Assert.Equal(Paint(button.Background), Paint(bd.Background));
            Assert.Equal(Paint(button.BorderBrush), Paint(bd.BorderBrush));
            Assert.Equal(32, button.ActualHeight);

            var hover = StyleTrigger(button.Style, UIElement.IsMouseOverProperty, true);
            Assert.Equal(Token(theme, hoverFill), ColourOf(button, hover.Setters, Control.BackgroundProperty));
            Assert.Equal(Token(theme, hoverBorder), ColourOf(button, hover.Setters, Control.BorderBrushProperty));

            // Pressed: a wash of the text colour over the button, whatever its intent.
            var wash = Part<Border>(button, "wash");
            Assert.Equal(Token(theme, "Text"), Paint(wash.Background));
            Assert.Equal(0, wash.Opacity);
            var pressed = TriggerOn(button.Template.Triggers, ButtonBase.IsPressedProperty, true);
            Assert.True((double)SetterOf(pressed.Setters, UIElement.OpacityProperty, "wash")!.Value > 0);

            button.IsEnabled = false;
            PageRenderer.Settle(page);
            // Disabled in its own colours at full strength, never faded (D02 T04 §6: at 0.4 a primary read as active).
            Assert.Equal(1.0, Part<Grid>(button, "root").Opacity);
            Assert.Equal(Token(theme, "DisabledFill"), Paint(Part<Border>(button, "bd").Background));
            Assert.Equal(Token(theme, "DisabledLine"), Paint(Part<Border>(button, "bd").BorderBrush));
            Assert.Equal(Cursors.Arrow, button.Cursor);
        });
    }

    [Theory]
    [InlineData("Btn")]
    [InlineData("Btn.Small")]
    [InlineData("Btn.Primary")]
    [InlineData("Btn.Danger")]
    [InlineData("Btn.Chrome")]
    public void A_button_clicks_once_when_invoked_and_refuses_while_disabled_keeping_its_tooltip(string style)
    {
        Button button = null!;
        var clicks = 0;
        Live("Dark", () => button = Styled<Button>(style, b => { b.Content = "Go"; b.ToolTip = "Needs a running queue"; b.Click += (_, _) => clicks++; }), page =>
        {
            Pattern<IInvokeProvider>(button, PatternInterface.Invoke).Invoke();
            PageRenderer.Settle(page);
            Assert.Equal(1, clicks);

            button.IsEnabled = false;
            PageRenderer.Settle(page);
            Assert.Throws<ElementNotEnabledException>(() => Pattern<IInvokeProvider>(button, PatternInterface.Invoke).Invoke());
            PageRenderer.Settle(page);
            Assert.Equal(1, clicks);
            Assert.True(ToolTipService.GetShowOnDisabled(button), "a disabled button still shows why");
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_small_button_has_smaller_words_and_is_still_a_32_px_target(string theme)
    {
        Button small = null!;
        Live(theme, () => small = Styled<Button>("Btn.Small", b => { b.Content = "Details"; b.HorizontalAlignment = HorizontalAlignment.Left; b.VerticalAlignment = VerticalAlignment.Top; }), _ =>
        {
            Assert.Equal((double)small.FindResource("T.Caption"), small.FontSize);
            Assert.Equal(32, small.MinHeight);
            Assert.Equal(32, small.ActualHeight);
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_window_button_is_46_by_44_and_the_close_button_hovers_to_danger_under_the_base_colour(string theme)
    {
        Button close = null!;
        Live(theme, () => close = Styled<Button>("Btn.Chrome", b => { b.Content = "x"; b.Tag = "danger"; }), _ =>
        {
            Assert.Equal((46, 44), (close.ActualWidth, close.ActualHeight));
            Assert.Equal(Token(theme, "TextDim"), Paint(close.Foreground));

            var hover = TriggerOn(close.Template.Triggers, UIElement.IsMouseOverProperty, true);
            Assert.Equal(Token(theme, "Hover"), ColourOf(close, hover.Setters, Border.BackgroundProperty, "bd"));
            Assert.Equal(Token(theme, "Text"), ColourOf(close, hover.Setters, Control.ForegroundProperty));

            // The danger hover: the one multi-trigger on the Tag (beside it is the contrast theme's, D02 T03 §10).
            var danger = close.Template.Triggers.OfType<MultiTrigger>().Single(t => t.Conditions.Any(c => c.Property == FrameworkElement.TagProperty));
            Assert.Contains(danger.Conditions, c => c.Property == FrameworkElement.TagProperty && Equals(c.Value, "danger"));
            Assert.Contains(danger.Conditions, c => c.Property == UIElement.IsMouseOverProperty && Equals(c.Value, true));
            Assert.Equal(Token(theme, "Danger"), ColourOf(close, danger.Setters, Border.BackgroundProperty, "bd"));
            Assert.Equal(Token(theme, "Base"), ColourOf(close, danger.Setters, Control.ForegroundProperty));
        });
    }

    // ---- the rail and the log ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_rails_selected_row_has_its_marker_bar_and_accent_words_and_the_others_have_neither(string theme)
    {
        ListBox rail = null!;
        Live(theme, () => rail = Styled<ListBox>("NavList", l => { l.ItemsSource = new[] { "Overview", "Memory", "Queue" }; l.SelectedIndex = 1; }), page =>
        {
            ListBoxItem Row(int index) => (ListBoxItem)rail.ItemContainerGenerator.ContainerFromIndex(index);
            // The list's one marker bar (D02 T03 §3), beside the selected row: 8 in from its left, 8 down, its height
            // less 16 (the row's own padding and a 2 px inset).
            var marker = Part<Border>(rail, "PART_Marker");
            double TopOf(int index) => Row(index).TranslatePoint(new Point(0, 0), rail).Y + 8;

            Assert.Equal(Token(theme, "Accent"), Paint(marker.Background));
            Assert.Equal(3, marker.ActualWidth);
            Assert.True(marker.ActualHeight > 8, "the marker is a bar, not a dot");
            Assert.Equal(TopOf(1), Bosun.Panel.Theme.RailMarker.Position(rail)!.Value.Now, 1);
            Assert.Equal(Token(theme, "Raised"), Paint(Part<Border>(Row(1), "bd").Background));
            Assert.Equal(Token(theme, "Accent"), Paint(Row(1).Foreground));
            // The words are the row's content, drawn in the row's colour.
            Assert.Equal(Token(theme, "Accent"), Paint(Under<TextBlock>(Row(1)).Single().Foreground));

            foreach (var other in new[] { 0, 2 })
            {
                Assert.Equal(Token(theme, "TextDim"), Paint(Row(other).Foreground));
                Assert.True(Row(other).ActualHeight >= 32);
            }

            // Selecting another row moves the marker (no motion gate here: at once).
            rail.SelectedIndex = 2;
            PageRenderer.Settle(page);
            Assert.Equal(TopOf(2), Bosun.Panel.Theme.RailMarker.Position(rail)!.Value.Now, 1);
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_log_with_room_to_spare_shows_every_line_and_no_scroll_bar(string theme)
    {
        // The LogBox template's bare ScrollViewer showed a bar and its thumb whatever the lines (D02 T04 §9). Every line
        // is checked within the list first, so a bar hidden over lines it clips can't pass.
        Live(theme, ControlsGallery.Gallery, page =>
        {
            var log = Shell.ShellSupport.Under<ListBox>(page).Single(l => l.Name == "GalleryLog");
            var box = log.TransformToAncestor(page).TransformBounds(new Rect(log.RenderSize));
            var rows = Shell.ShellSupport.Under<ListBoxItem>(log).ToList();
            Assert.Equal(5, rows.Count);
            Assert.All(rows, row => Assert.True(box.Contains(row.TransformToAncestor(page).TransformBounds(new Rect(row.RenderSize))), "every line within the list"));
            var scroll = Shell.ShellSupport.Under<ScrollViewer>(log).First();
            Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        }, width: PageRenderer.Width, height: 1400);
    }

    [Fact]
    public void A_log_line_is_drawn_and_is_not_a_tab_stop()
    {
        ListBox log = null!;
        var lines = new[] { "INFO started", "WARN slow", "FAIL stopped" };
        Live("Dark", () => log = Styled<ListBox>("LogBox", l => l.ItemsSource = lines), _ =>
        {
            Assert.Equal(lines, PageRenderer.TextsOf(log));
            // A line takes the page's text colour, not the system's, which the stock list sets.
            Assert.All(Under<TextBlock>(log), line => Assert.Equal(Token("Dark", "Text"), Paint(line.Foreground)));
            foreach (var index in Enumerable.Range(0, lines.Length))
                Assert.False(((ListBoxItem)log.ItemContainerGenerator.ContainerFromIndex(index)).Focusable);
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(log));
            Assert.True(log.Focusable, "the list itself takes the keyboard, to scroll");
        });
    }

    // ---- scroll bars ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void Scroll_bars_are_10_px_with_a_thumb_in_the_themes_line_colour_and_the_horizontal_one_drags_and_pages(string theme)
    {
        ScrollViewer viewer = null!;
        Live(theme, () => viewer = new ScrollViewer
        {
            Width = 300, Height = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 2000, Height = 1500 },
        }, page =>
        {
            var bars = Under<ScrollBar>(viewer).Where(b => b.IsVisible).ToList();
            var vertical = bars.Single(b => b.Orientation == Orientation.Vertical);
            var horizontal = bars.Single(b => b.Orientation == Orientation.Horizontal);

            Assert.Equal(10, vertical.ActualWidth);
            Assert.Equal(viewer.ViewportHeight, vertical.ActualHeight);
            var grip = Under<Thumb>(vertical).Single();
            Assert.Equal(Token(theme, "Line"), Paint(((Border)grip.Template.FindName("th", grip)).Background));
            Assert.DoesNotContain(Under<RepeatButton>(vertical), b => b.Focusable); // no stepper a keyboard would stop on

            // Steward's bar stays 10 px wide when it lies down; this one is as wide as its space.
            Assert.Equal(10, horizontal.ActualHeight);
            Assert.Equal(viewer.ViewportWidth, horizontal.ActualWidth);
            Assert.True(horizontal.ActualWidth > 200);
            var slider = Under<Thumb>(horizontal).Single();
            Assert.True(slider.ActualWidth > 10, "the thumb is laid out along the bar");

            slider.RaiseEvent(new DragDeltaEventArgs(30, 0));
            PageRenderer.Settle(page);
            var dragged = viewer.HorizontalOffset;
            Assert.True(dragged > 0, "dragging the thumb scrolls");
            Assert.Equal(0, viewer.VerticalOffset);

            ScrollBar.PageRightCommand.Execute(null, horizontal);
            PageRenderer.Settle(page);
            Assert.Equal(dragged + viewer.ViewportWidth, viewer.HorizontalOffset, 1.0);

            ScrollBar.PageDownCommand.Execute(null, vertical);
            PageRenderer.Settle(page);
            Assert.Equal(viewer.ViewportHeight, viewer.VerticalOffset, 1.0);
        });
    }

    // ---- tabs ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_tab_strip_shows_the_selected_tabs_content_and_its_trailing_slot_and_the_keyboard_changes_tab(string theme)
    {
        TabControl tabs = null!;
        Live(theme, () => tabs = Styled<TabControl>("TabStrip", t =>
        {
            t.Tag = new TextBlock { Text = "3 waiting" };
            t.Items.Add(new TabItem { Header = "Overview", Content = new TextBlock { Text = "first content" } });
            t.Items.Add(new TabItem { Header = "Logs", Content = new TextBlock { Text = "second content" } });
            t.Items.Add(new TabItem { Header = "Dry run", Content = new TextBlock { Text = "third content" }, IsEnabled = false, ToolTip = "Needs the service" });
        }), page =>
        {
            TabItem Tab(int index) => (TabItem)tabs.Items[index];
            Color? Words(int index) => Paint(Under<TextBlock>(Tab(index)).Single().Foreground);

            Assert.Equal(["Overview", "Logs", "Dry run", "3 waiting", "first content"], PageRenderer.TextsOf(tabs));
            Assert.Equal(Token(theme, "Accent"), Words(0));
            Assert.Equal(Token(theme, "Accent"), Paint(Part<Border>(Tab(0), "bd").BorderBrush));
            Assert.Equal(Token(theme, "TextDim"), Words(1));
            Assert.Equal(Colors.Transparent, Paint(Part<Border>(Tab(1), "bd").BorderBrush));
            Assert.True(Tab(0).ActualHeight >= 44);

            // The page a tab holds is drawn as a page, not as a tab's header: it does not take the header's colour.
            var content = Under<TextBlock>(tabs).Single(t => t.Text == "first content");
            Assert.Equal(Token(theme, "Text"), Paint(content.Foreground));
            Assert.Equal(FontWeights.Normal, content.FontWeight);

            // End goes to the last tab that can be selected: the third is disabled, with its reason still shown.
            Press(tabs, Key.End);
            PageRenderer.Settle(page);
            Assert.Equal(1, tabs.SelectedIndex);
            Assert.Equal(["Overview", "Logs", "Dry run", "3 waiting", "second content"], PageRenderer.TextsOf(tabs));
            Assert.Equal(Token(theme, "Accent"), Words(1));
            Assert.Equal(Token(theme, "TextDim"), Words(0));
            Assert.Equal(0.4, Part<Border>(Tab(2), "bd").Opacity);
            Assert.True(ToolTipService.GetShowOnDisabled(Tab(2)));

            Press(tabs, Key.Home);
            PageRenderer.Settle(page);
            Assert.Equal(0, tabs.SelectedIndex);
        });
    }

    // ---- fields ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_field_draws_in_the_themes_colours_takes_text_and_refuses_it_read_only(string theme)
    {
        TextBox field = null!;
        Live(theme, () => field = Styled<TextBox>("Field", f => { f.VerticalAlignment = VerticalAlignment.Top; f.ToolTip = "Set by the queue"; }), page =>
        {
            Assert.Equal(Token(theme, "Raised"), Paint(field.Background));
            Assert.Equal(Token(theme, "Line"), Paint(field.BorderBrush));
            Assert.Equal(Token(theme, "Text"), Paint(field.Foreground));
            Assert.Equal(Token(theme, "Text"), Paint(field.CaretBrush));
            Assert.Equal(Token(theme, "AccentDim"), Paint(field.SelectionBrush));
            Assert.Equal(32, field.ActualHeight);
            var bd = Part<Border>(field, "bd");
            Assert.Equal(Token(theme, "Line"), Paint(bd.BorderBrush));
            // Focus is the shared ring 2 px outside and nothing else (D02 T04 §1): the border turning accent too drew a
            // second outline, so the template has no focus trigger.
            Assert.Same(field.FindResource("FocusRing"), field.FocusVisualStyle);
            Assert.DoesNotContain(field.Template.Triggers.OfType<Trigger>(), t => t.Property == UIElement.IsKeyboardFocusedProperty);

            // The template still holds a working text host: text set as a person's would be is in the field.
            Pattern<IValueProvider>(field, PatternInterface.Value).SetValue("build Bosun");
            PageRenderer.Settle(page);
            Assert.Equal("build Bosun", field.Text);
            var host = Part<ScrollViewer>(field, "PART_ContentHost");
            Assert.True(host.ExtentWidth > 20, "the text is laid out in the field's own text host");

            field.IsReadOnly = true;
            PageRenderer.Settle(page);
            Assert.Equal(Token(theme, "TextDim"), Paint(field.Foreground));
            Assert.ThrowsAny<Exception>(() => Pattern<IValueProvider>(field, PatternInterface.Value).SetValue("changed"));
            Assert.Equal("build Bosun", field.Text);

            field.IsEnabled = false;
            PageRenderer.Settle(page);
            Assert.Equal(0.6, bd.Opacity);
            Assert.True(ToolTipService.GetShowOnDisabled(field));
        });
    }

    // ---- check boxes ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_check_box_ticks_in_the_accent_its_words_are_its_target_and_it_toggles(string theme)
    {
        CheckBox check = null!;
        Live(theme, () => check = new CheckBox { Content = "Start with Windows", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Set by policy" }, page =>
        {
            var box = Part<Border>(check, "box");
            var tick = Part<System.Windows.Shapes.Path>(check, "tick");
            Assert.Equal((16, 16), (box.ActualWidth, box.ActualHeight));
            Assert.Equal(new CornerRadius((double)((CornerRadius)check.FindResource("R.Mark")).TopLeft), box.CornerRadius);
            Assert.Equal(Token(theme, "Raised"), Paint(box.Background));
            Assert.Equal(Visibility.Collapsed, tick.Visibility);
            Assert.True(check.ActualHeight >= 32);

            // The whole row is the target: the words, and the gap between the box and the words.
            var words = Under<TextBlock>(check).Single();
            var onWords = words.TranslatePoint(new Point(words.ActualWidth / 2, words.ActualHeight / 2), check);
            Assert.Same(words, check.InputHitTest(onWords));
            var gap = box.TranslatePoint(new Point(box.ActualWidth + 4, box.ActualHeight / 2), check);
            Assert.Same(Part<Border>(check, "row"), check.InputHitTest(gap));

            // Toggled as a screen reader toggles it. Not by a Space key raised here: a button asks the real mouse
            // whether its left button is up before it lets Space click, so that test failed whenever a person was
            // using the mouse (it did, on 2026-11-15). The real key is pressed in the shown window, in D02 T02 §10.
            Pattern<IToggleProvider>(check, PatternInterface.Toggle).Toggle();
            PageRenderer.Settle(page);
            Assert.True(check.IsChecked);
            Assert.Equal(Visibility.Visible, tick.Visibility);
            Assert.Equal(Token(theme, "Accent"), Paint(box.Background));
            Assert.Equal(Token(theme, "Accent"), Paint(box.BorderBrush));
            Assert.Equal(Token(theme, "OnAccent"), Paint(tick.Stroke));

            Pattern<IToggleProvider>(check, PatternInterface.Toggle).Toggle();
            PageRenderer.Settle(page);
            Assert.False(check.IsChecked);
            Assert.Equal(Visibility.Collapsed, tick.Visibility);

            check.IsEnabled = false;
            PageRenderer.Settle(page);
            Assert.Throws<ElementNotEnabledException>(() => Pattern<IToggleProvider>(check, PatternInterface.Toggle).Toggle());
            Assert.False(check.IsChecked);
            Assert.Equal(0.4, Part<Border>(check, "row").Opacity);
            Assert.True(ToolTipService.GetShowOnDisabled(check));
        });
    }

    // ---- combo boxes ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_combo_box_shows_its_placeholder_until_something_is_selected_and_then_the_selection(string theme)
    {
        ComboBox combo = null!;
        Live(theme, () => combo = new ComboBox { Tag = "Choose a side", ItemsSource = new[] { "Windows", "WSL" }, Width = 160, VerticalAlignment = VerticalAlignment.Top, ToolTip = "No WSL here" }, page =>
        {
            var placeholder = Part<TextBlock>(combo, "Placeholder");
            Assert.Equal(["Choose a side"], PageRenderer.TextsOf(combo));
            Assert.Equal(Token(theme, "TextMute"), Paint(placeholder.Foreground));
            Assert.Equal(32, combo.ActualHeight);

            // Selected as a binding selects. Not by a Down key raised here: the combo box reads the real keyboard's
            // Alt key with it, and Alt+Down opens the list, a window of its own, whenever a person is switching
            // windows. The real keys are pressed in the shown window, in D02 T02 §10.
            combo.SelectedIndex = 0;
            PageRenderer.Settle(page);
            Assert.Equal(["Windows"], PageRenderer.TextsOf(combo));
            Assert.Equal(Visibility.Collapsed, placeholder.Visibility);

            combo.SelectedIndex = 1;
            PageRenderer.Settle(page);
            Assert.Equal(["WSL"], PageRenderer.TextsOf(combo));

            combo.SelectedIndex = -1;
            PageRenderer.Settle(page);
            Assert.Equal(["Choose a side"], PageRenderer.TextsOf(combo));
            combo.SelectedIndex = 1;
            PageRenderer.Settle(page);
            Assert.False(combo.IsDropDownOpen);

            // The list a person opens: in the theme's colours, scrolling when it is long. Opening it is a window of
            // its own, proved with a real one in D02 T02 §10.
            var popup = Part<Popup>(combo, "PART_Popup");
            var drop = Part<Border>(combo, "Drop");
            Assert.Same(drop, popup.Child);
            // It opens at once: WPF's own popup animations run for a fixed time whatever the Windows animation
            // setting says (design/SPEC.md §19, Motion), so none is used.
            Assert.Equal(PopupAnimation.None, popup.PopupAnimation);
            Assert.Equal(Token(theme, "Raised"), Paint(drop.Background));
            Assert.Equal(Token(theme, "Line"), Paint(drop.BorderBrush));
            Assert.IsType<ItemsPresenter>(Part<ScrollViewer>(combo, "DropScroll").Content);
            var toggle = Part<ToggleButton>(combo, "Toggle");
            Assert.Equal(Token(theme, "Raised"), Paint(((Border)toggle.Template.FindName("bd", toggle)).Background));

            combo.IsEnabled = false;
            PageRenderer.Settle(page);
            Assert.Throws<ElementNotEnabledException>(() => Pattern<IExpandCollapseProvider>(combo, PatternInterface.ExpandCollapse).Expand());
            Assert.False(combo.IsDropDownOpen);
            Assert.Equal(0.4, Part<Grid>(combo, "root").Opacity);
            Assert.True(ToolTipService.GetShowOnDisabled(combo));
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_combo_box_item_is_a_32_px_target_in_the_themes_colours(string theme)
    {
        ComboBoxItem item = null!;
        Live(theme, () => item = new ComboBoxItem { Content = "Windows", VerticalAlignment = VerticalAlignment.Top }, _ =>
        {
            Assert.True(item.ActualHeight >= 32);
            Assert.Equal(Token(theme, "Text"), Paint(item.Foreground));
            var highlighted = TriggerOn(item.Template.Triggers, ComboBoxItem.IsHighlightedProperty, true);
            Assert.Equal(Token(theme, "Hover"), ColourOf(item, highlighted.Setters, Border.BackgroundProperty, "bd"));
            var selected = TriggerOn(item.Template.Triggers, Selector.IsSelectedProperty, true);
            Assert.Equal(Token(theme, "AccentDim"), ColourOf(item, selected.Setters, Border.BackgroundProperty, "bd"));
        });
    }

    // ---- tooltips ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_tooltip_is_a_raised_box_with_caption_words_in_the_themes_colours(string theme)
    {
        // A tooltip lives in a window of its own, so its style is read where the page would resolve it.
        Sta.Run(() =>
        {
            var host = PageRenderer.Page(new Border(), theme, 100, 100);
            var style = (Style)host.FindResource(typeof(ToolTip));
            Assert.Equal(Token(theme, "Raised"), ColourOf(host, style.Setters, Control.BackgroundProperty));
            Assert.Equal(Token(theme, "LineStrong"), ColourOf(host, style.Setters, Control.BorderBrushProperty));
            Assert.Equal(Token(theme, "Text"), ColourOf(host, style.Setters, Control.ForegroundProperty));
            Assert.Equal(host.FindResource("T.Caption"), Resolved(host, SetterOf(style.Setters, Control.FontSizeProperty)!.Value));
            Assert.NotNull(SetterOf(style.Setters, Control.TemplateProperty));
        });
    }

    // ---- tables ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_table_is_read_only_selects_whole_rows_and_lines_its_numbers_up(string theme)
    {
        DataGrid grid = null!;
        Live(theme, () => grid = ControlsGallery.Grid(ControlsGallery.Rows), page =>
        {
            Assert.True(grid.IsReadOnly);
            Assert.False(grid.CanUserAddRows);
            Assert.False(grid.CanUserDeleteRows);
            Assert.Equal(DataGridSelectionUnit.FullRow, grid.SelectionUnit);
            Assert.Equal(ControlsGallery.Rows.Count, grid.Items.Count); // no blank row to add into

            DataGridRow Row(int index) => (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
            var header = Under<DataGridColumnHeader>(grid).Single(h => h.Content as string == "Process");
            Assert.True(header.ActualHeight >= 32);
            Assert.Equal(Token(theme, "TextMute"), Paint(header.Foreground));
            Assert.Equal((double)grid.FindResource("T.Caption"), header.FontSize);

            Assert.True(Row(0).ActualHeight >= 32);
            Assert.Equal(Colors.Transparent, Paint(Row(0).Background));
            Assert.Equal(Token(theme, "Line"), Paint(Row(0).BorderBrush));

            grid.SelectedIndex = 1;
            PageRenderer.Settle(page);
            Assert.Equal(Token(theme, "AccentDim"), Paint(Row(1).Background));
            Assert.Equal(Colors.Transparent, Paint(Row(0).Background));
            var cells = Under<DataGridCell>(Row(1)).ToList();
            Assert.Equal(4, cells.Count);
            foreach (var cell in cells)
            {
                // The row shows the selection: no cell paints the system's highlight over it.
                Assert.True(cell.IsSelected);
                Assert.Equal(Colors.Transparent, Paint(cell.Background));
                Assert.Equal(Token(theme, "Text"), Paint(cell.Foreground));
                Assert.Same(grid.FindResource("FocusRing.Inside"), cell.FocusVisualStyle);
            }

            var name = Under<TextBlock>(cells[0]).Single();
            var megabytes = Under<TextBlock>(cells[2]).Single();
            Assert.Equal("912", megabytes.Text);
            Assert.Equal(HorizontalAlignment.Right, megabytes.HorizontalAlignment);
            Assert.Equal(FontNumeralAlignment.Tabular, System.Windows.Documents.Typography.GetNumeralAlignment(megabytes));
            Assert.Equal(HorizontalAlignment.Stretch, name.HorizontalAlignment);
            // Right-aligned for real: the number ends where its cell's content ends.
            var cellRight = cells[2].TranslatePoint(new Point(cells[2].ActualWidth - cells[2].Padding.Right, 0), grid).X;
            var textRight = megabytes.TranslatePoint(new Point(megabytes.ActualWidth, 0), grid).X;
            Assert.Equal(cellRight, textRight, 1.0);
        }, width: 900, height: 400);
    }

    [Fact]
    public void No_popup_in_the_styles_animates_by_itself()
    {
        // Every popup, the ones added later too: a PopupAnimation is motion outside the one gate (D02 T03 §1).
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        static IEnumerable<string> Animated(string xaml, XNamespace p) => XDocument.Parse(xaml).Descendants(p + "Popup")
            .Select(popup => (string?)popup.Attribute("PopupAnimation") ?? "None").Where(animation => animation != "None");
        var text = ControlsText();
        Assert.NotEmpty(XDocument.Parse(text).Descendants(p + "Popup"));
        Assert.Empty(Animated(text, p));
        Assert.Equal(["Fade"], Animated(text.Replace("PopupAnimation=\"None\"", "PopupAnimation=\"Fade\"", StringComparison.Ordinal), p));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_sorted_column_says_so_with_an_arrow_that_turns_with_the_order(string theme)
    {
        DataGrid grid = null!;
        Live(theme, () => grid = ControlsGallery.Grid(ControlsGallery.Rows), page =>
        {
            var column = grid.Columns[2];
            var header = Under<DataGridColumnHeader>(grid).Single(h => ReferenceEquals(h.Column, column));
            var arrow = Part<System.Windows.Shapes.Path>(header, "sort");
            Assert.Equal(Visibility.Collapsed, arrow.Visibility);

            column.SortDirection = System.ComponentModel.ListSortDirection.Ascending;
            PageRenderer.Settle(page);
            Assert.Equal(Visibility.Visible, arrow.Visibility);
            Assert.Equal(Token(theme, "TextMute"), Paint(arrow.Stroke));
            var up = arrow.Data.ToString(CultureInfo.InvariantCulture);

            column.SortDirection = System.ComponentModel.ListSortDirection.Descending;
            PageRenderer.Settle(page);
            Assert.Equal(Visibility.Visible, arrow.Visibility);
            Assert.NotEqual(up, arrow.Data.ToString(CultureInfo.InvariantCulture));

            // The header still carries the parts the grid resizes a column by.
            Assert.IsType<Thumb>(header.Template.FindName("PART_RightHeaderGripper", header));
            Assert.IsType<Thumb>(header.Template.FindName("PART_LeftHeaderGripper", header));
        }, width: 900, height: 400);
    }

    // ---- the focus ring ----

    [Theory]
    [InlineData("Dark", "FocusRing")]
    [InlineData("Light", "FocusRing")]
    [InlineData("Dark", "FocusRing.Inside")]
    [InlineData("Light", "FocusRing.Inside")]
    public void The_focus_ring_is_drawn_in_the_accent_around_the_control(string theme, string ring)
    {
        // Made as WPF makes a focus visual: a control carrying the style, laid out over the focused control's bounds
        // (here 120 by 32 at 40,40), in the adorner layer, which clips nothing.
        const int left = 40, top = 40, width = 120, height = 32, pageWidth = 240;
        Control visual = null!;
        Live(theme, () =>
        {
            visual = new Control { Width = width, Height = height, Focusable = false };
            visual.SetResourceReference(FrameworkElement.StyleProperty, ring);
            Canvas.SetLeft(visual, left);
            Canvas.SetTop(visual, top);
            var canvas = new Canvas();
            canvas.Children.Add(visual);
            return canvas;
        }, page =>
        {
            var pixels = PageRenderer.Pixels(page);
            uint At(int x, int y) => pixels[y * pageWidth + x];
            var accent = Pixel(theme, "Accent");
            var page_ = Pixel(theme, "Base");
            int midX = left + width / 2, midY = top + height / 2, right = left + width, bottom = top + height;

            // Where the 2 px line is: 2 px outside the control for the ring, on its edge for the inside ring.
            var offset = ring == "FocusRing" ? -4 : 0;
            Assert.Equal(accent, At(left + offset, midY));
            Assert.Equal(accent, At(left + offset + 1, midY));
            Assert.Equal(accent, At(right - offset - 1, midY));
            Assert.Equal(accent, At(right - offset - 2, midY));
            Assert.Equal(accent, At(midX, top + offset));
            Assert.Equal(accent, At(midX, top + offset + 1));
            Assert.Equal(accent, At(midX, bottom - offset - 1));
            Assert.Equal(accent, At(midX, bottom - offset - 2));

            // Nothing over the control itself, and nothing beyond the line.
            Assert.Equal(page_, At(midX, midY));
            Assert.Equal(page_, At(left + offset + 2, midY));
            Assert.Equal(page_, At(left + offset - 1, midY));
            if (ring == "FocusRing")
            {
                // The gap between the control and its ring, so the ring never covers the control's own border.
                Assert.Equal(page_, At(left - 1, midY));
                Assert.Equal(page_, At(left - 2, midY));
            }
        }, width: pageWidth, height: 120);
    }

    // ---- every style is a target with a focus ring ----

    /// <summary>Styles that are not something a person presses, each with why. Everything else must be a target.</summary>
    static readonly Dictionary<string, string> NotTargets = new()
    {
        ["FocusRing"] = "the focus ring itself",
        ["FocusRing.Inside"] = "the focus ring itself",
        ["FocusRing.Segment"] = "the focus ring itself, 2 px inside a segment (D02 T04 §1)",
        ["T.Mono"] = "text",
        ["T.SectionLabel"] = "text",
        ["FieldLabel"] = "text",
        ["Section.Heading"] = "text: a section's heading (D02 T04 §3)",
        ["Hint"] = "text: a control's short explanation (D02 T04 §3)",
        ["Card"] = "a surface that holds controls, never focused itself: its controls are the targets (D02 T04 §3)",
        ["Scroll.Gutter"] = "a page's scroll area, never focused itself (Focusable false): its bar and its content are the targets (D02 T04 §8)",
        ["Cell.Text"] = "text",
        ["Cell.Number"] = "text",
        ["ScrollBar"] = "a scroll bar is driven by the wheel, the keys and its page's own keyboard path, and is 10 px by design",
        ["ScrollThumb"] = "a part of the scroll bar",
        ["ScrollPage"] = "a part of the scroll bar",
        ["LogBox/ListBoxItem"] = "a log line is read, not acted on: it is not focusable",
        ["CountBadge"] = "a rail row's count, drawn inside the row and never focused: the row is the target",
        ["controls:StatusPill"] = "a state's glyph and word, never focused: the control it describes is the target",
        ["controls:SegmentedControl"] = "the track of a segmented control: each segment (Segment) is the 32 px target with the ring",
        ["controls:RadialGauge"] = "a value drawn and said in words, never focused: the card it sits on is read, not operated",
        ["controls:StackedBar"] = "shares drawn and said in words, never focused: the card it sits on is read, not operated",
        ["controls:QueueSlotTile"] = "a slot's state drawn and said in words, never focused: the Queue page's actions are the targets",
        ["controls:WaitingRow"] = "the list of waiting jobs, read and never focused: each line is said in full",
        ["controls:WaitingLineView"] = "one waiting job, said in full, never focused",
        ["shell:ToastCard"] = "a toast's card is never focused: its Dismiss button is the 32 px target with the ring",
        ["PaletteList"] = "the palette's list never takes the keyboard: its search field keeps it, and each row is said in full",
        ["ToolTip"] = "a tooltip can't be pressed or focused",
        ["Value"] = "a value is read, not acted on: it is not focusable (D02 T03 §7)",
        ["ColumnGripper"] = "a part of the column header, which is the target",
        ["DataGrid"] = "the table is as high as its rows; its headers, rows and cells are the targets",
    };

    /// <summary>Styles that carry the ring but not the height, each with why.</summary>
    static readonly Dictionary<string, string> RingOnly = new()
    {
        ["DataGridCell"] = "a cell is as high as its row, which carries the 32 px",
    };

    /// <summary>What is wrong with the styles in a Controls.xaml text: one line per style that is a target without a ring or 32 px.</summary>
    static List<string> TargetProblems(string xaml)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = XDocument.Parse(xaml).Root!;
        string Bare(string? type) => (type ?? "?").Replace("{x:Type ", "", StringComparison.Ordinal).TrimEnd('}');
        string Name(XElement style) =>
            style.Ancestors(p + "Style").FirstOrDefault() is { } outer
                ? Name(outer) + "/" + Bare((string?)style.Attribute("TargetType"))
                : (string?)style.Attribute(x + "Key") ?? Bare((string?)style.Attribute("TargetType"));
        var styles = root.Descendants(p + "Style").ToDictionary(Name);

        string? Setter(XElement style, string property)
        {
            for (var s = style; s is not null;)
            {
                if (s.Elements(p + "Setter").LastOrDefault(e => (string?)e.Attribute("Property") == property) is { } setter) return (string?)setter.Attribute("Value");
                var based = (string?)s.Attribute("BasedOn");
                if (based is null) return null;
                s = styles.GetValueOrDefault(based.Replace("{StaticResource ", "", StringComparison.Ordinal).TrimEnd('}'));
            }
            return null;
        }
        bool Tall(XElement style) => new[] { Setter(style, "MinHeight"), Setter(style, "Height") }
            .Any(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var height) && height >= 32);
        bool Ringed(XElement style) => Setter(style, "FocusVisualStyle") is "{StaticResource FocusRing}" or "{StaticResource FocusRing.Inside}" or "{StaticResource FocusRing.Segment}";

        var problems = new List<string>();
        foreach (var (name, style) in styles)
        {
            if (NotTargets.ContainsKey(name)) continue;
            if (!Ringed(style)) problems.Add($"{name}: no FocusVisualStyle of the focus ring");
            if (!RingOnly.ContainsKey(name) && !Tall(style)) problems.Add($"{name}: no MinHeight or Height of 32 or more");
        }
        foreach (var listed in NotTargets.Keys.Concat(RingOnly.Keys))
            if (!styles.ContainsKey(listed)) problems.Add($"{listed}: listed as not a target, but no such style is in the file");
        return problems;
    }

    [Fact]
    public void Every_style_is_a_32_px_target_with_the_focus_ring_or_is_named_as_not_a_target()
    {
        var text = ControlsText();
        var problems = TargetProblems(text);
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        // The walk sees every style there is, the nested ones too.
        const string end = "</ResourceDictionary>";
        Assert.Contains("NavRow: no MinHeight or Height of 32 or more",
            TargetProblems(text.Replace("<Setter Property=\"MinHeight\" Value=\"36\"/>", "", StringComparison.Ordinal)));

        // A style added without either fails, whatever it is called: nothing is a target by being on a list.
        var added = TargetProblems(text.Replace(end, "<Style x:Key=\"Btn.New\" TargetType=\"Button\"/>" + end, StringComparison.Ordinal));
        Assert.Equal(["Btn.New: no FocusVisualStyle of the focus ring", "Btn.New: no MinHeight or Height of 32 or more"], added);
        // One based on a target is one, by what it inherits.
        Assert.Empty(TargetProblems(text.Replace(end, "<Style x:Key=\"Btn.New\" TargetType=\"Button\" BasedOn=\"{StaticResource Btn}\"/>" + end, StringComparison.Ordinal)));
        // A target made shorter, or with its ring taken away, fails by name.
        Assert.Contains("Btn: no MinHeight or Height of 32 or more",
            TargetProblems(text.Replace("<Setter Property=\"Padding\" Value=\"13,0\"/>\n        <Setter Property=\"MinHeight\" Value=\"32\"/>".ReplaceLineEndings(Eol(text)), "<Setter Property=\"MinHeight\" Value=\"24\"/>", StringComparison.Ordinal)));
        Assert.Contains("CheckBox: no FocusVisualStyle of the focus ring",
            TargetProblems(text.Replace("<Setter Property=\"Cursor\" Value=\"Hand\"/>\n        <Setter Property=\"FocusVisualStyle\" Value=\"{StaticResource FocusRing}\"/>\n        <Setter Property=\"ToolTipService.ShowOnDisabled\" Value=\"True\"/>\n        <Setter Property=\"Template\">\n            <Setter.Value>\n                <ControlTemplate TargetType=\"CheckBox\">".ReplaceLineEndings(Eol(text)),
                "<Setter Property=\"Template\"><Setter.Value><ControlTemplate TargetType=\"CheckBox\">", StringComparison.Ordinal)));
        // A stale name on the list is a failure too.
        Assert.Contains(TargetProblems(text.Replace("<Style TargetType=\"ToolTip\">", "<Style x:Key=\"Tip\" TargetType=\"ToolTip\">", StringComparison.Ordinal)),
            problem => problem.StartsWith("ToolTip: listed as not a target", StringComparison.Ordinal));
    }

    static string Eol(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    // ---- every key is real ----

    [GeneratedRegex(@"\{(DynamicResource|StaticResource)\s+([^}\s]+)\s*\}")]
    private static partial Regex ResourceUse();

    [GeneratedRegex("x:Key=\"([^\"]+)\"")]
    private static partial Regex KeyDeclared();

    /// <summary>
    /// What is wrong with the keys a Controls.xaml text names: a theme key is named with DynamicResource and is in
    /// both themes; a key of the file's own is named with StaticResource, after it is declared.
    /// </summary>
    static List<string> KeyProblems(string xaml, IReadOnlySet<string> dark, IReadOnlySet<string> light)
    {
        var own = KeyDeclared().Matches(xaml).ToDictionary(m => m.Groups[1].Value, m => m.Index);
        var problems = new List<string>();
        foreach (Match use in ResourceUse().Matches(xaml))
        {
            var (how, key) = (use.Groups[1].Value, use.Groups[2].Value);
            var line = xaml.AsSpan(0, use.Index).Count('\n') + 1;
            if (how == "DynamicResource")
            {
                // A key of both generated themes, or one the contrast colours alone hold for the contrast triggers (D02 T03 §10).
                if ((!dark.Contains(key) || !light.Contains(key)) && !Bosun.Panel.Theme.HighContrastTheme.OwnKeys.Contains(key))
                    problems.Add($"line {line}: {key} is not a key of both generated themes");
            }
            else if (dark.Contains(key) || light.Contains(key))
                problems.Add($"line {line}: {key} is a theme key named with StaticResource, which a theme swap would not repaint");
            else if (!own.TryGetValue(key, out var declared) || declared > use.Index)
                problems.Add($"line {line}: {key} is not declared earlier in this file");
        }
        return problems;
    }

    static IReadOnlySet<string> ThemeKeys(string file) =>
        KeyDeclared().Matches(File.ReadAllText(TestPaths.GeneratedTheme(file))).Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void Every_key_the_styles_name_is_real_and_a_theme_key_is_always_dynamic()
    {
        var (dark, light) = (ThemeKeys("Dark.xaml"), ThemeKeys("Light.xaml"));
        Assert.Contains("B.Accent", dark);
        var text = ControlsText();
        var problems = KeyProblems(text, dark, light);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        // The file really names both kinds: an empty file has no bad key either.
        Assert.True(ResourceUse().Matches(text).Count(m => m.Groups[1].Value == "DynamicResource") > 50);
        Assert.Contains(ResourceUse().Matches(text), m => m.Groups[1].Value == "StaticResource");

        // WPF draws a theme key that isn't there as nothing, without an error: here it is one.
        Assert.Single(KeyProblems(text.Replace("{DynamicResource B.LineStrong}\"/>\n            </Trigger>".ReplaceLineEndings(Eol(text)), "{DynamicResource B.LineStrng}\"/>\n            </Trigger>".ReplaceLineEndings(Eol(text)), StringComparison.Ordinal), dark, light),
            problem => problem.EndsWith("B.LineStrng is not a key of both generated themes", StringComparison.Ordinal));
        Assert.Contains(KeyProblems(text.Replace("{DynamicResource B.OnAccent}", "{StaticResource B.OnAccent}", StringComparison.Ordinal), dark, light),
            problem => problem.Contains("B.OnAccent is a theme key named with StaticResource", StringComparison.Ordinal));
        Assert.Contains(KeyProblems(text.Replace("{StaticResource FocusRing}", "{StaticResource FocusRung}", StringComparison.Ordinal), dark, light),
            problem => problem.EndsWith("FocusRung is not declared earlier in this file", StringComparison.Ordinal));
        // A key present in one theme only is not a key of the theme.
        Assert.NotEmpty(KeyProblems(text, dark, light.Where(key => key != "B.Hover").ToHashSet()));
        // Named before it is declared: a StaticResource reads upward only.
        var early = text.Replace("<FontFamily x:Key=\"F.Mono\">Cascadia Mono, Consolas</FontFamily>", "", StringComparison.Ordinal)
            .Replace("</ResourceDictionary>", "<FontFamily x:Key=\"F.Mono\">Cascadia Mono, Consolas</FontFamily></ResourceDictionary>", StringComparison.Ordinal);
        Assert.Contains(KeyProblems(early, dark, light), problem => problem.EndsWith("F.Mono is not declared earlier in this file", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_file_loads_beside_either_theme_with_its_fonts_text_styles_and_rings(string theme)
    {
        Live(theme, () => new Border(), page =>
        {
            foreach (var font in new[] { "F.Ui", "F.Display", "F.Mono" }) Assert.IsType<FontFamily>(page.FindResource(font));
            foreach (var style in new[] { "T.Mono", "T.SectionLabel", "FieldLabel", "FocusRing", "FocusRing.Inside", "Cell.Text", "Cell.Number" })
                Assert.IsType<Style>(page.FindResource(style));

            var mono = new TextBlock { Text = "0x1F" };
            mono.SetResourceReference(FrameworkElement.StyleProperty, "T.Mono");
            ((Border)page.Child).Child = mono;
            PageRenderer.Settle(page);
            Assert.Same(page.FindResource("F.Mono"), mono.FontFamily);
            Assert.Equal((double)page.FindResource("T.Body"), mono.FontSize);
            Assert.Equal(Token(theme, "TextDim"), Paint(mono.Foreground));
        });
    }

    // ---- where the styles reach ----

    [Fact]
    public void The_application_merges_the_theme_and_then_the_styles()
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var app = XDocument.Load(System.IO.Path.Combine(TestPaths.Panel(), "App.xaml"));
        var merged = app.Descendants(p + "ResourceDictionary.MergedDictionaries").Single().Elements(p + "ResourceDictionary").Select(d => (string?)d.Attribute("Source"));
        Assert.Equal(["Foundation.xaml", "Theme/Generated/Dark.xaml", "Theme/Controls.xaml"], merged);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_rendered_page_has_a_windows_text_and_the_applications_styles(string theme)
    {
        TextBlock words = null!;
        CheckBox check = null!;
        Live(theme, () =>
        {
            var stack = new StackPanel();
            stack.Children.Add(words = new TextBlock { Text = "plain words" });
            stack.Children.Add(check = new CheckBox { Content = "a check box" });
            return stack;
        }, page =>
        {
            Assert.Same(page.FindResource("F.Ui"), words.FontFamily);
            Assert.Equal((double)page.FindResource("T.Body"), words.FontSize);
            Assert.Equal(Token(theme, "Text"), Paint(words.Foreground));
            // Controls.xaml's check box, not the stock one: it has the template's named box.
            Assert.NotNull(check.Template.FindName("box", check));
        });
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_gallery_draws_every_control_in_the_applications_styles(string theme)
    {
        ControlsGallery gallery = null!;
        Live(theme, () => gallery = ControlsGallery.Gallery(), page =>
        {
            var controls = (ResourceDictionary)page.Resources.MergedDictionaries[1];
            var buttons = Under<Button>(gallery).ToList();
            Assert.Equal(7, buttons.Count);
            foreach (var button in buttons) Assert.Contains(button.Style, new[] { "Btn", "Btn.Small", "Btn.Primary", "Btn.Danger", "Btn.Chrome" }.Select(key => controls[key]));
            Assert.Same(controls["TabStrip"], Under<TabControl>(gallery).Single().Style);
            var fields = Under<TextBox>(gallery).ToList();
            Assert.Equal(4, fields.Count); // three in a row, and the form section's (D02 T04 §3)
            foreach (var field in fields) Assert.Same(controls["Field"], field.Style);
            var checks = Under<CheckBox>(gallery).ToList();
            Assert.Equal(4, checks.Count);
            foreach (var check in checks) Assert.NotNull(check.Template.FindName("box", check));
            var combos = Under<ComboBox>(gallery).ToList();
            Assert.Equal(3, combos.Count);
            foreach (var combo in combos) Assert.NotNull(combo.Template.FindName("Placeholder", combo));
            var lists = Under<ListBox>(gallery).ToList();
            Assert.Equal([controls["NavList"], controls["LogBox"], controls["LogBox"]], lists.Select(list => list.Style));
            // The first log is longer than its space, so it scrolls with the styled bar; the second has room to spare and
            // shows none (D02 T04 §9).
            Assert.Contains(Under<ScrollBar>(lists[1]), bar => bar.IsVisible && bar.ActualWidth == 10);
            Assert.DoesNotContain(Under<ScrollBar>(lists[2]), bar => bar.IsVisible);
        }, width: PageRenderer.Width, height: PageRenderer.Height);

        Live(theme, () => gallery = ControlsGallery.Table(), _ =>
        {
            var rows = Under<DataGridRow>(gallery).ToList();
            Assert.Equal(ControlsGallery.Rows.Count, rows.Count);
            Assert.Equal(Token(theme, "AccentDim"), Paint(rows[1].Background));
            Assert.All(rows, row => Assert.True(row.ActualHeight >= 32));
        }, width: PageRenderer.Width, height: PageRenderer.Height);
    }
}

using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Bosun.Core;
using Bosun.Panel.Settings;
using Bosun.Panel.Tests.Helpers;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Settings;
using Bosun.Panel.Theme;
using Xunit;

namespace Bosun.Panel.Tests;

/// <summary>
/// Windows' contrast themes (D02 T03 §10): the contrast colours hold every colour key the panel uses, mapped as
/// Windows' guidance has it; the theme service puts them in force over the person's choice while contrast is on,
/// live and on the UI thread, and gives the choice back after; the templates draw a disabled control in gray text
/// with no fade and a selection in the highlight colours; and the Settings page says why its theme choice waits.
/// Night sky (dark) and Desert (light) are both proved.
/// </summary>
public class HighContrastTests
{
    public static TheoryData<string> Palettes => ["NightSky", "Desert"];

    static SystemPalette Named(string name) => ContrastPalettes.Both.Single(p => p.Name == name).Palette;

    static ResourceDictionary Generated(string theme)
    {
        using var stream = File.OpenRead(TestPaths.GeneratedTheme(theme + ".xaml"));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    static Color? Paint(object? brush) => (brush as SolidColorBrush)?.Color;

    /// <summary>Windows' contrast setting, as a test turns it: on, off, or to other colours, on the calling thread.</summary>
    sealed class FakeContrast(bool on, SystemPalette palette) : IHighContrast
    {
        public bool On { get; private set; } = on;

        public SystemPalette Palette { get; private set; } = palette;

        public event Action? Changed;

        public void Set(bool on, SystemPalette? palette = null)
        {
            On = on;
            Palette = palette ?? Palette;
            Changed?.Invoke();
        }
    }

    // ---- the colours ----

    [Theory]
    [MemberData(nameof(Palettes))]
    public void The_contrast_colours_hold_every_colour_and_elevation_key_mapped_as_Windows_guidance_has_it(string name)
    {
        var palette = Named(name);
        Sta.Run(() =>
        {
            var dark = Generated("Dark");
            var colours = HighContrastTheme.Build(palette, dark);
            var expected = dark.Keys.OfType<string>().Where(k => k.StartsWith("C.", StringComparison.Ordinal) || k.StartsWith("B.", StringComparison.Ordinal) || k.StartsWith("Elevation.", StringComparison.Ordinal))
                .Concat(HighContrastTheme.OwnKeys).Order(StringComparer.Ordinal);
            Assert.Equal(expected, colours.Keys.OfType<string>().Order(StringComparer.Ordinal));

            foreach (var (key, want) in new[]
            {
                ("Base", palette.Window), ("Surface", palette.Window), ("Raised", palette.Window), ("Hover", palette.Window), ("OkSoft", palette.Window), ("DangerSoft", palette.Window),
                ("Text", palette.WindowText), ("TextDim", palette.WindowText), ("TextMute", palette.WindowText), ("Line", palette.WindowText), ("LineStrong", palette.WindowText),
                ("Ok", palette.WindowText), ("Warn", palette.WindowText), ("Danger", palette.WindowText), ("Info", palette.WindowText),
                ("Accent", palette.Highlight), ("AccentDim", palette.Highlight), ("OnAccent", palette.HighlightText),
                ("GrayText", palette.GrayText), ("HighlightText", palette.HighlightText),
            })
            {
                Assert.Equal(want, (Color)colours["C." + key]);
                var brush = Assert.IsType<SolidColorBrush>(colours["B." + key]);
                Assert.Equal((want, true), (brush.Color, brush.IsFrozen));
            }
            Assert.All(colours.Keys.OfType<string>().Where(k => k.StartsWith("Elevation.", StringComparison.Ordinal)),
                key => Assert.Equal(0, ((DropShadowEffect)colours[key]).Opacity));
            return 0;
        });
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Text_selected_text_and_gray_text_read_at_4_5_to_1_on_what_they_are_drawn_on(string name)
    {
        var palette = Named(name);
        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        foreach (var (what, text, on) in new[]
        {
            ("text on the window", palette.WindowText, palette.Window),
            ("highlight text on the highlight", palette.HighlightText, palette.Highlight),
            ("gray text on the window", palette.GrayText, palette.Window),
            ("the highlight as a selected rail row's mark on the window", palette.Highlight, palette.Window),
        })
            Assert.True(ThemeContrast.Ratio(Hex(text), Hex(on)) >= ThemeContrast.Minimum, $"{name}: {what} is {ThemeContrast.Ratio(Hex(text), Hex(on)):F2}:1");
    }

    [Fact]
    public void Merged_over_the_Dark_theme_every_theme_key_a_view_names_resolves()
    {
        var src = Path.Combine(TestPaths.Repo(), "src", "Bosun.Panel");
        var named = Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?:Dynamic|Static)Resource ((?:B|C|R|S|T|F|Motion|Elevation)\.[A-Za-z.]+)\}").Select(m => m.Groups[1].Value))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Contains("B.Accent", named); // the scan saw the views
        var missing = Sta.Run(() =>
        {
            var dark = Generated("Dark");
            var root = new ResourceDictionary();
            root.MergedDictionaries.Add(dark);
            root.MergedDictionaries.Add(HighContrastTheme.Build(ContrastPalettes.NightSky, dark));
            root.MergedDictionaries.Add(PageRenderer.Controls());
            return named.Where(key => !root.Contains(key)).ToList();
        });
        Assert.Empty(missing);
    }

    // ---- the theme service ----

    [Fact]
    public void Contrast_wins_over_the_choice_live_on_the_ui_thread_rebuilds_on_new_colours_and_gives_the_choice_back()
    {
        var light = Paint(Generated("Light")["B.Text"]);
        Border drawn = null!;
        PageRenderer.InPage("Dark", () =>
        {
            drawn = new Border();
            drawn.SetResourceReference(Border.BackgroundProperty, "B.Text");
            return drawn;
        }, page =>
        {
            var merged = page.Resources.MergedDictionaries;
            var styles = merged[1];
            var contrast = new FakeContrast(false, ContrastPalettes.NightSky);
            var theme = new ThemeService(merged, new FakeWindowsTheme { IsLight = false }, page.Dispatcher, contrast: contrast);
            var swappedOn = new List<int>();
            theme.EffectiveChanged += () => swappedOn.Add(Environment.CurrentManagedThreadId);
            theme.Apply(ThemeChoice.Dark);

            // Windows says contrast is on, on a thread of its own: swapped here, on the UI thread.
            var other = Task.Run(() => contrast.Set(true));
            Assert.True(StartupSupport.Pump(() => other.IsCompleted && theme.IsHighContrast), "contrast was never applied");
            PageRenderer.Settle(page);
            Assert.Equal([Environment.CurrentManagedThreadId], swappedOn);
            Assert.Equal(ContrastPalettes.NightSky.WindowText, Paint(drawn.Background)); // a control already on screen repaints
            Assert.Same(styles, merged[^1]); // the styles still come last, never reloaded

            theme.Apply(ThemeChoice.Light); // saved while contrast is on: kept, not shown
            PageRenderer.Settle(page);
            Assert.True(theme.IsHighContrast);
            Assert.Equal(ThemeChoice.Light, theme.Choice);
            Assert.Equal(ContrastPalettes.NightSky.WindowText, Paint(drawn.Background));

            contrast.Set(true, ContrastPalettes.Desert); // another contrast theme chosen
            Assert.True(StartupSupport.Pump(() => Paint(drawn.Background) == ContrastPalettes.Desert.WindowText), "new colours were never built");

            contrast.Set(false); // off: the choice made meanwhile
            Assert.True(StartupSupport.Pump(() => !theme.IsHighContrast), "contrast was never taken off");
            PageRenderer.Settle(page);
            Assert.Equal(ThemeService.Light, theme.Effective);
            Assert.Equal(light, Paint(drawn.Background));
            Assert.Equal(2, merged.Count); // the contrast colours are gone
            return true;
        });
    }

    [Fact]
    public void A_start_with_contrast_on_draws_contrast_at_once_and_Follow_Windows_is_followed_after()
    {
        Border drawn = null!;
        PageRenderer.InPage("Dark", () =>
        {
            drawn = new Border();
            drawn.SetResourceReference(Border.BackgroundProperty, "B.Base");
            return drawn;
        }, page =>
        {
            var contrast = new FakeContrast(true, ContrastPalettes.Desert);
            var windows = new FakeWindowsTheme { IsLight = true };
            var theme = new ThemeService(page.Resources.MergedDictionaries, windows, page.Dispatcher, contrast: contrast);
            theme.Apply(ThemeChoice.FollowWindows);
            PageRenderer.Settle(page);
            Assert.True(theme.IsHighContrast);
            Assert.Equal(ContrastPalettes.Desert.Window, Paint(drawn.Background));

            contrast.Set(false);
            Assert.True(StartupSupport.Pump(() => theme.Effective == ThemeService.Light), "Follow Windows was not followed");
            windows.Flip(); // Windows goes dark: followed, as before contrast
            Assert.True(StartupSupport.Pump(() => theme.Effective == ThemeService.Dark), "Follow Windows stopped following");
            return true;
        });
    }

    // ---- the templates ----

    /// <summary>
    /// Live controls, disabled, in a page of the theme: the opacity of the part each template fades, and the colour of
    /// every part a person sees (the words as drawn, a check box's edge, fill and tick, a combo box's edge and arrow).
    /// </summary>
    static List<(string What, double Opacity, Color?[] Parts)> Disabled(string theme, SystemPalette? palette = null) => PageRenderer.InPage(theme, () =>
    {
        var panel = new StackPanel();
        var button = new Button { Content = "Save", IsEnabled = false };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Btn");
        var field = new TextBox { Text = "build Bosun", IsEnabled = false };
        field.SetResourceReference(FrameworkElement.StyleProperty, "Field");
        var combo = new ComboBox { ItemsSource = new[] { "WSL", "Windows" }, SelectedIndex = 0, IsEnabled = false };
        foreach (UIElement each in new UIElement[] { button, field, new CheckBox { Content = "Notify", IsChecked = true, IsEnabled = false }, combo }) panel.Children.Add(each);
        return panel;
    }, page =>
    {
        var panel = (StackPanel)((Border)page).Child;
        Color? Words(DependencyObject under) => Paint(Shell.ShellSupport.Under<TextBlock>(under).First().Foreground);
        var found = new List<(string, double, Color?[])>();
        foreach (Control control in panel.Children)
        {
            var part = control.Template.FindName("root", control) as UIElement ?? control.Template.FindName("bd", control) as UIElement ?? control.Template.FindName("row", control) as UIElement;
            Color?[] parts = control switch
            {
                Button b => [Words(b)],
                TextBox t => [Paint(t.Foreground), Paint(((Border)t.Template.FindName("bd", t)).BorderBrush)],
                CheckBox c => [Words(c), Paint(((Border)c.Template.FindName("box", c)).BorderBrush), Paint(((System.Windows.Shapes.Path)c.Template.FindName("tick", c)).Stroke)],
                ComboBox x => [Words((DependencyObject)x.Template.FindName("Selected", x)), ToggleParts(x)[0], ToggleParts(x)[1]],
                _ => [],
            };
            found.Add((control.GetType().Name, part?.Opacity ?? -1, parts));
        }
        return found;
    }, palette: palette);

    /// <summary>A combo box's toggle: its edge and its arrow.</summary>
    static Color?[] ToggleParts(ComboBox combo)
    {
        var toggle = (System.Windows.Controls.Primitives.ToggleButton)combo.Template.FindName("Toggle", combo);
        return [Paint(((Border)toggle.Template.FindName("bd", toggle)).BorderBrush), Paint(((System.Windows.Shapes.Path)toggle.Template.FindName("arrow", toggle)).Stroke)];
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void A_disabled_control_in_contrast_is_drawn_in_gray_text_at_full_strength_and_in_Dark_keeps_its_fade(string name)
    {
        var palette = Named(name);
        var contrast = Disabled("HighContrast", palette);
        Assert.Equal(4, contrast.Count);
        foreach (var (what, opacity, parts) in contrast)
        {
            Assert.Equal((what, 1.0), (what, opacity));
            Assert.All(parts, colour => Assert.Equal((what, (Color?)palette.GrayText), (what, colour)));
        }
        // The checked box's fill is the window, not the highlight: nothing of it says it can be used.
        var box = PageRenderer.InPage("HighContrast", () => new CheckBox { Content = "Notify", IsChecked = true, IsEnabled = false },
            page => Paint(((Border)((CheckBox)((Border)page).Child).Template.FindName("box", (CheckBox)((Border)page).Child)).Background), palette: palette);
        Assert.Equal(palette.Window, box);
        var dark = Disabled("Dark");
        Assert.All(dark, c => Assert.True(c.Opacity < 1, $"{c.What} in Dark is not faded"));
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void A_selected_item_row_and_rail_row_in_contrast_take_the_highlight_text_on_the_highlight(string name)
    {
        var palette = Named(name);
        var read = PageRenderer.InPage("HighContrast", () =>
        {
            var panel = new StackPanel();
            var combo = new ComboBox();
            foreach (var word in new[] { "WSL", "Windows" }) combo.Items.Add(new ComboBoxItem { Content = word });
            ((ComboBoxItem)combo.Items[1]).IsSelected = true;
            var rail = new ListBox { ItemsSource = new[] { "Overview", "Memory" }, SelectedIndex = 1 };
            rail.SetResourceReference(FrameworkElement.StyleProperty, "NavList");
            var grid = ControlsGallery.Grid(ControlsGallery.Rows);
            grid.SelectedIndex = 0;
            panel.Children.Add(combo);
            panel.Children.Add(rail);
            panel.Children.Add(grid);
            return panel;
        }, page =>
        {
            var panel = (StackPanel)((Border)page).Child;
            var combo = (ComboBox)panel.Children[0];
            var item = (ComboBoxItem)combo.Items[1];
            item.ApplyTemplate(); // its container is the item itself: its template is applied without opening the box
            var rail = (ListBox)panel.Children[1];
            var row = (ListBoxItem)rail.ItemContainerGenerator.ContainerFromIndex(1);
            var grid = (DataGrid)panel.Children[2];
            var cell = Shell.ShellSupport.Under<DataGridCell>(grid).First(c => c.IsSelected);
            return (HighContrastMode.GetIsOn(item), Paint(item.Foreground), Paint(row.Foreground), Paint(((Border)row.Template.FindName("bd", row)).Background), Paint(cell.Foreground));
        }, palette: palette);
        Assert.True(read.Item1, "the combo box's item does not see the contrast mode");
        Assert.Equal(palette.HighlightText, read.Item2);
        Assert.Equal(palette.HighlightText, read.Item3);
        Assert.Equal(palette.Highlight, read.Item4);
        Assert.Equal(palette.HighlightText, read.Item5);
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void The_focus_ring_in_contrast_is_drawn_in_the_highlight_colour(string name)
    {
        var palette = Named(name);
        var ring = PageRenderer.InPage("HighContrast", () =>
        {
            var focus = new Control { Width = 120, Height = 32 };
            focus.SetResourceReference(FrameworkElement.StyleProperty, "FocusRing");
            return focus;
        }, page => Paint(Shell.ShellSupport.Under<Border>(((Border)page).Child).First(b => b.BorderThickness.Left > 0).BorderBrush), palette: palette);
        Assert.Equal(palette.Highlight, ring);
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void A_selected_tab_in_contrast_is_highlight_text_on_the_highlight(string name)
    {
        var palette = Named(name);
        var read = PageRenderer.InPage("HighContrast", () =>
        {
            var tabs = new TabControl();
            tabs.SetResourceReference(FrameworkElement.StyleProperty, "TabStrip"); // the panel's tab strip
            tabs.Items.Add(new TabItem { Header = "Overview", IsSelected = true });
            tabs.Items.Add(new TabItem { Header = "Logs" });
            return tabs;
        }, page =>
        {
            var tab = (TabItem)((TabControl)((Border)page).Child).Items[0];
            var header = (ContentPresenter)tab.Template.FindName("header", tab);
            return (Paint(TextElement(header)), Paint(((Border)tab.Template.FindName("bd", tab)).Background));
        }, palette: palette);
        Assert.Equal((palette.HighlightText, palette.Highlight), read);
    }

    static object? TextElement(DependencyObject element) => element.GetValue(System.Windows.Documents.TextElement.ForegroundProperty);

    /// <summary>A button held pressed, as a person's press holds it, for a test to read its pressed state.</summary>
    sealed class HeldButton : Button
    {
        public HeldButton() => IsPressed = true;
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Buttons_in_contrast_take_Windows_button_colours_and_a_press_is_the_opaque_highlight_pair(string name)
    {
        var palette = Named(name);
        var read = PageRenderer.InPage("HighContrast", () =>
        {
            var panel = new StackPanel();
            var plain = new Button { Content = "Refresh" };
            plain.SetResourceReference(FrameworkElement.StyleProperty, "Btn");
            var primary = new Button { Content = "Save" };
            primary.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Primary");
            var held = new HeldButton { Content = "Stop job" };
            held.SetResourceReference(FrameworkElement.StyleProperty, "Btn");
            panel.Children.Add(plain);
            panel.Children.Add(primary);
            panel.Children.Add(held);
            return panel;
        }, page =>
        {
            var panel = (StackPanel)((Border)page).Child;
            var (plain, primary, held) = ((Button)panel.Children[0], (Button)panel.Children[1], (Button)panel.Children[2]);
            Color? Words(Button b) => Paint(Shell.ShellSupport.Under<TextBlock>(b).First().Foreground);
            return new[]
            {
                Paint(plain.Background), Words(plain), Paint(plain.BorderBrush),
                Paint(primary.Background), Words(primary),
                Paint(((Border)held.Template.FindName("bd", held)).Background), Words(held), (Color?)Colors.Transparent,
            }.Append(Paint(Shell.ShellSupport.Under<Border>(held).First(b => b.Name == "wash").Background)).ToArray()
             .Concat([((UIElement)held.Template.FindName("wash", held)).Opacity == 0 ? (Color?)Colors.Black : Colors.White]).ToArray();
        }, palette: palette);
        Assert.Equal(palette.ButtonFace, read[0]);
        Assert.Equal(palette.ButtonText, read[1]);
        Assert.Equal(palette.ButtonText, read[2]);
        Assert.Equal(palette.Highlight, read[3]); // the primary action keeps its emphasis
        Assert.Equal(palette.HighlightText, read[4]);
        Assert.Equal(palette.Highlight, read[5]); // pressed: the highlight, opaque
        Assert.Equal(palette.HighlightText, read[6]);
        Assert.Equal(Colors.Black, read[^1]); // and no translucent wash over it
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void A_combo_box_s_own_drop_down_sees_contrast_draws_its_selection_in_the_highlight_pair_and_follows_a_live_change(string name)
    {
        var palette = Named(name);
        var read = PageRenderer.InPage("HighContrast", () => new ComboBox { ItemsSource = new[] { "Follow Windows", "Dark", "Light" }, SelectedIndex = 1 }, page =>
        {
            var combo = (ComboBox)((Border)page).Child;
            // The drop-down's own tree, as the template makes it, laid out without opening a window on screen.
            var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
            var drop = (FrameworkElement)popup.Child;
            drop.Measure(new Size(300, 300));
            drop.Arrange(new Rect(0, 0, 300, 300));
            drop.UpdateLayout();
            var selected = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(1);
            Color? Words() => Paint(Shell.ShellSupport.Under<TextBlock>(selected).First().Foreground);
            var on = (Bosun.Panel.Theme.HighContrastMode.GetIsOn(drop), Bosun.Panel.Theme.HighContrastMode.GetIsOn(selected), Words(), Paint(((Border)selected.Template.FindName("bd", selected)).Background));
            Bosun.Panel.Theme.HighContrastMode.SetIsOn(page, false); // contrast turned off, live
            drop.UpdateLayout();
            var off = (Bosun.Panel.Theme.HighContrastMode.GetIsOn(drop), Words());
            return (on, off);
        }, palette: palette);
        Assert.Equal((true, true, (Color?)palette.HighlightText, (Color?)palette.Highlight), read.on);
        Assert.False(read.off.Item1);
        Assert.NotEqual(palette.HighlightText, read.off.Item2);
    }

    // ---- the Settings page ----

    [Fact]
    public void The_Settings_page_says_why_its_theme_waits_while_contrast_is_on_and_stops_when_it_is_off()
    {
        Sta.Run(() =>
        {
            var fixture = new SettingsFixture().Open();
            var merged = new List<ResourceDictionary>();
            var contrast = new FakeContrast(false, ContrastPalettes.NightSky);
            var theme = new ThemeService(merged, new FakeWindowsTheme(), System.Windows.Threading.Dispatcher.CurrentDispatcher, _ => Generated("Dark"), contrast);
            theme.Apply(ThemeChoice.Dark);
            var page = new SettingsPage(fixture.Store, fixture.Startup, fixture.Log, () => { }, theme);
            Assert.Null(page.ThemeNote);

            contrast.Set(true);
            Assert.True(StartupSupport.Pump(() => page.ThemeNote is not null), "the line never came");
            Assert.Equal(SettingsPage.ContrastNote, page.ThemeNote);
            contrast.Set(false);
            Assert.True(StartupSupport.Pump(() => page.ThemeNote is null), "the line never went");
            return 0;
        });
    }
}

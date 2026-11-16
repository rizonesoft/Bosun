using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Bosun.Panel.Controls;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Bosun.Panel.Theme;
using Xunit;

namespace Bosun.Panel.Tests.Controls;

/// <summary>
/// The segmented control (D02 T03 §4, design/SPEC.md §19 Segmented control): its indicator slides to the chosen
/// segment over the motion gate's fast duration, lands at once with animation effects off or turned off mid-change, and
/// holds nothing of the gate once unloaded; each segment is a 32 px target; and in a contrast theme the chosen segment
/// is the highlight's text on the highlight and a disabled one Windows' gray, in both palettes, following a live switch.
/// The keys (Left, Right, Home, End) are driven on the real panel by the smoke's KeyboardTests.
/// </summary>
public class SegmentedControlTests
{
    sealed class FakeAnimation(bool on) : IWindowsAnimation
    {
        public bool On { get; private set; } = on;

        public event Action? Changed;

        public void Set(bool on)
        {
            On = on;
            Changed?.Invoke();
        }
    }

    sealed class FakeContrast(SystemPalette palette) : IHighContrast
    {
        public bool On { get; private set; }

        public SystemPalette Palette { get; private set; } = palette;

        public event Action? Changed;

        public void Set(bool on, SystemPalette? palette = null)
        {
            On = on;
            Palette = palette ?? Palette;
            Changed?.Invoke();
        }
    }

    static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    static SegmentedControl Choices() => new() { ItemsSource = new[] { "Now", "Today", "Week" }, SelectedIndex = 0 };

    static double LeftOf(SegmentedControl c, int index) => ((ListBoxItem)c.ItemContainerGenerator.ContainerFromIndex(index)).TranslatePoint(new Point(0, 0),
        (UIElement)VisualTreeHelper.GetParent((Border)c.Template.FindName("PART_Marker", c))).X;

    static void WithGate(bool animates, Action<Border, SegmentedControl, FakeAnimation, Motion> test)
    {
        PageRenderer.InPage("Dark", Choices, page =>
        {
            var windows = new FakeAnimation(animates);
            var gate = new Motion(page.Resources, windows);
            page.Resources[Motion.GateKey] = gate;
            var control = ShellSupport.Under<SegmentedControl>(page).Single();
            PageRenderer.Settle(page);
            test(page, control, windows, gate);
            return 0;
        });
    }

    [Fact]
    public void The_indicator_slides_part_way_then_lands_on_the_chosen_segment_over_the_fast_duration()
    {
        WithGate(true, (page, control, _, gate) =>
        {
            var from = RailMarker.Position(control)!.Value.Now;
            Assert.Equal(LeftOf(control, 0), from, 1);
            control.SelectedIndex = 2;
            Pump(TimeSpan.FromMilliseconds(gate.Fast.TimeSpan.TotalMilliseconds / 3));
            var midway = RailMarker.Position(control)!.Value;
            Assert.True(RailMarker.IsSliding(control));
            Assert.InRange(midway.Now, from + 1, midway.Target - 1);
            // Choosing a segment un-bolds the last one, which narrows it, so the target's left can move by under a pixel
            // after the slide began; the landing places it again, exactly (asserted below).
            Assert.Equal(LeftOf(control, 2), midway.Target, tolerance: 2.0);
            Pump(gate.Fast.TimeSpan * 3);
            Assert.False(RailMarker.IsSliding(control));
            Assert.Equal(LeftOf(control, 2), RailMarker.Position(control)!.Value.Now, 1);
            var marker = (Border)control.Template.FindName("PART_Marker", control);
            Assert.Equal(((ListBoxItem)control.ItemContainerGenerator.ContainerFromIndex(2)).ActualWidth, marker.Width, 1); // the segment's own width
        });
    }

    [Fact]
    public void With_animation_effects_off_it_lands_at_once_and_turning_them_off_mid_change_lands_it_at_once()
    {
        WithGate(false, (_, control, windows, _) =>
        {
            control.SelectedIndex = 1;
            Assert.False(RailMarker.IsSliding(control));
            Assert.Equal(LeftOf(control, 1), RailMarker.Position(control)!.Value.Now, 1);
            windows.Set(true);
            control.SelectedIndex = 2;
            Assert.True(RailMarker.IsSliding(control));
            windows.Set(false);
            Assert.False(RailMarker.IsSliding(control));
            Assert.Equal(LeftOf(control, 2), RailMarker.Position(control)!.Value.Now, 1);
        });
    }

    [Fact]
    public void Unloaded_it_lands_and_holds_nothing_of_the_gate()
    {
        WithGate(true, (page, control, windows, gate) =>
        {
            int Listeners() => (typeof(Motion).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate) as Delegate)?
                .GetInvocationList().Count(d => d.Target?.GetType().DeclaringType == typeof(RailMarker)) ?? 0;
            control.SelectedIndex = 2;
            Assert.Equal(1, Listeners());
            page.Child = null; // unloaded mid-change
            PageRenderer.Settle(page);
            Assert.False(RailMarker.IsSliding(control));
            Assert.Equal(0, Listeners());
        });
    }

    [Fact]
    public void Each_segment_is_a_32_px_target()
    {
        PageRenderer.InPage("Dark", Choices, page =>
        {
            var control = ShellSupport.Under<SegmentedControl>(page).Single();
            Assert.All(ShellSupport.Under<ListBoxItem>(control), segment => Assert.True(segment.ActualHeight >= 32, $"{segment.Content} is {segment.ActualHeight} px"));
            return 0;
        });
    }

    static ResourceDictionary Generated(string theme)
    {
        using var stream = File.OpenRead(TestPaths.GeneratedTheme(theme + ".xaml"));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    [Fact]
    public void In_a_contrast_theme_the_chosen_segment_and_a_disabled_one_take_Windows_colours_in_both_palettes_live()
    {
        PageRenderer.InPage("Dark", () => new SegmentedControl
        {
            Items = { new ListBoxItem { Content = "Now" }, new ListBoxItem { Content = "Today" }, new ListBoxItem { Content = "Week", IsEnabled = false } },
            SelectedIndex = 0,
        }, page =>
        {
            var contrast = new FakeContrast(Helpers.ContrastPalettes.NightSky);
            var theme = new ThemeService(page.Resources.MergedDictionaries, new Settings.FakeWindowsTheme(), page.Dispatcher, Generated, contrast);
            theme.Apply(Bosun.Core.ThemeChoice.Dark);
            HighContrastMode.SetIsOn(page, theme.IsHighContrast);
            theme.EffectiveChanged += () => HighContrastMode.SetIsOn(page, theme.IsHighContrast);
            var control = ShellSupport.Under<SegmentedControl>(page).Single();
            var marker = (Border)control.Template.FindName("PART_Marker", control);
            ListBoxItem Segment(int i) => (ListBoxItem)control.Items[i];
            Color Of(object brush) => ((SolidColorBrush)brush).Color;
            Color Key(string key) => ((SolidColorBrush)page.FindResource(key)).Color;
            void Check(string said)
            {
                PageRenderer.Settle(page);
                Assert.True(theme.IsHighContrast, said);
                Assert.Equal(Key("B.HighlightText"), Of(Segment(0).Foreground));
                Assert.Equal(Key("B.Accent"), Of(marker.Background)); // the highlight, in a contrast theme
                Assert.Equal(Key("B.GrayText"), Of(Segment(2).Foreground));
                Assert.Equal(1.0, Segment(2).Opacity);
            }

            contrast.Set(true);
            Assert.True(StartupSupport.Pump(() => theme.IsHighContrast));
            Check("Night sky");
            contrast.Set(true, Helpers.ContrastPalettes.Desert);
            Assert.True(StartupSupport.Pump(() => Key("B.HighlightText") == Color.FromRgb(255, 245, 227)));
            Check("Desert");
            contrast.Set(false);
            Assert.True(StartupSupport.Pump(() => !theme.IsHighContrast));
            PageRenderer.Settle(page);
            Assert.Equal(Key("B.Surface"), Of(marker.Background)); // back to the theme's own
            Assert.Equal(Key("B.Text"), Of(Segment(0).Foreground));
            return 0;
        });
    }
}

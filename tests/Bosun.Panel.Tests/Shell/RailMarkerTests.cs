using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Theme;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The rail's one marker (D02 T03 §3, design/SPEC.md §19 Motion: the rail marker over the normal duration): it slides,
/// seen part-way between rows and then on the new one, through the motion gate; it lands at once with animation effects
/// off, and at once when they are turned off mid-slide; it follows a click on a row, the shell's own navigation and a
/// resize; it stays on the row a refused move leaves selected; it is the contrast theme's highlight text colour there;
/// and once the rail is unloaded it holds nothing of the gate.
/// </summary>
public class RailMarkerTests
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

    /// <summary>Lets the dispatcher, and the animation clock with it, run for a while.</summary>
    static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    static ListBox RailOf(Border page) => ShellSupport.Under<ListBox>(page).First(l => l.Name == "Rail");

    static double TopOf(ListBox rail, int index) => ShellSupport.Row(rail, index).TranslatePoint(new Point(0, 0), rail).Y + 8;

    /// <summary>A shell in a page with its own motion gate, as the application installs one.</summary>
    static void WithGate(bool animates, Action<Border, ListBox, ShellViewModel, FakeAnimation, Motion> test, string theme = "Dark")
    {
        ShellViewModel shell = null!;
        PageRenderer.InPage(theme, () => new ShellView { DataContext = shell = Shells.Panel() }, page =>
        {
            var windows = new FakeAnimation(animates);
            var gate = new Motion(page.Resources, windows);
            page.Resources[Motion.GateKey] = gate;
            var rail = RailOf(page);
            PageRenderer.Settle(page);
            test(page, rail, shell, windows, gate);
            return 0;
        }, 1360, 860);
    }

    [Fact]
    public void It_slides_part_way_then_lands_on_the_new_row_over_the_normal_duration()
    {
        WithGate(true, (page, rail, _, _, gate) =>
        {
            var from = RailMarker.Position(rail)!.Value.Now;
            Assert.Equal(TopOf(rail, 0), from, 1);
            rail.SelectedIndex = 3;
            Pump(TimeSpan.FromMilliseconds(gate.Normal.TimeSpan.TotalMilliseconds / 3));
            var midway = RailMarker.Position(rail)!.Value;
            Assert.True(RailMarker.IsSliding(rail), "a slide is under way");
            Assert.InRange(midway.Now, from + 1, midway.Target - 1); // seen between the two rows
            Assert.Equal(TopOf(rail, 3), midway.Target, 1);
            Pump(gate.Normal.TimeSpan * 2);
            Assert.False(RailMarker.IsSliding(rail));
            Assert.Equal(TopOf(rail, 3), RailMarker.Position(rail)!.Value.Now, 1);
        });
    }

    [Fact]
    public void With_animation_effects_off_it_lands_at_once_and_turning_them_off_mid_slide_lands_it_at_once()
    {
        WithGate(false, (_, rail, _, windows, _) =>
        {
            rail.SelectedIndex = 2;
            Assert.False(RailMarker.IsSliding(rail));
            Assert.Equal(TopOf(rail, 2), RailMarker.Position(rail)!.Value.Now, 1);

            windows.Set(true);
            rail.SelectedIndex = 5;
            Assert.True(RailMarker.IsSliding(rail));
            windows.Set(false); // turned off mid-slide
            Assert.False(RailMarker.IsSliding(rail));
            Assert.Equal(TopOf(rail, 5), RailMarker.Position(rail)!.Value.Now, 1);
        });
    }

    [Fact]
    public void It_follows_the_persons_pick_the_shells_own_navigation_and_a_resize()
    {
        WithGate(false, (page, rail, shell, _, _) =>
        {
            // The person's own pick: a click or a key ends in the rail's selection moving, which is what the marker
            // follows (off screen a row can't take the focus a click needs; the smoke's rail walk, RailTests, clicks and
            // types into the real panel).
            rail.SelectedIndex = 1;
            PageRenderer.Settle(page);
            Assert.Equal(1, rail.SelectedIndex);
            Assert.Equal(TopOf(rail, 1), RailMarker.Position(rail)!.Value.Now, 1);

            // The shell's own navigation (the command palette, a link to another page).
            Assert.True(shell.GoToAsync(shell.Sections[4]).GetAwaiter().GetResult());
            PageRenderer.Settle(page);
            Assert.Equal(TopOf(rail, 4), RailMarker.Position(rail)!.Value.Now, 1);

            // A smaller window: the rows move under a shorter rail, and the marker with them.
            page.Height = 640;
            PageRenderer.Settle(page);
            Assert.Equal(TopOf(rail, 4), RailMarker.Position(rail)!.Value.Now, 1);
        });
    }

    [Fact]
    public void A_move_refused_for_unsaved_edits_leaves_it_on_the_page_kept()
    {
        var (shell, first, _, _) = Shells.WithTestPages();
        PageRenderer.InPage("Dark", () => new ShellView { DataContext = shell }, page =>
        {
            var rail = RailOf(page);
            PageRenderer.Settle(page);
            first.Edits = "a change not yet saved";
            rail.SelectedIndex = 1; // the person picks another row
            Assert.True(StartupSupport.Pump(() => shell.Asking is not null), "the leave question never came");
            shell.Answer(LeaveChoice.Stay);
            Assert.True(StartupSupport.Pump(() => shell.Asking is null && rail.SelectedIndex == 0), "the rail never went back to the page kept");
            PageRenderer.Settle(page);
            Assert.Equal(TopOf(rail, 0), RailMarker.Position(rail)!.Value.Now, 1);
            return 0;
        }, 1360, 860);
    }

    [Fact]
    public void In_a_contrast_theme_it_is_the_highlights_text_colour()
    {
        PageRenderer.InPage("HighContrast", () => new ShellView { DataContext = Shells.Panel() }, page =>
        {
            var rail = RailOf(page);
            var marker = (Border)rail.Template.FindName("PART_Marker", rail);
            Assert.Equal(((SolidColorBrush)page.FindResource("B.HighlightText")).Color, ((SolidColorBrush)marker.Background).Color);
            Assert.Equal(Visibility.Visible, marker.Visibility);
            return 0;
        }, 1360, 860);
    }

    [Fact]
    public void Unloaded_it_lands_and_holds_nothing_of_the_gate()
    {
        WithGate(true, (page, rail, _, windows, gate) =>
        {
            // The marker's own listeners on the gate (the page's transition listens too, for itself).
            int Listeners() => (typeof(Motion).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate) as Delegate)?
                .GetInvocationList().Count(d => d.Target?.GetType().DeclaringType == typeof(RailMarker)) ?? 0;
            var before = Listeners();
            rail.SelectedIndex = 3; // finds the gate and starts a slide
            Assert.True(RailMarker.IsSliding(rail));
            Assert.Equal(before + 1, Listeners());
            var parent = (System.Windows.Controls.Panel)VisualTreeHelper.GetParent(rail);
            parent.Children.Remove(rail); // unloaded mid-slide
            PageRenderer.Settle(page);
            Assert.False(RailMarker.IsSliding(rail));
            Assert.Equal(before, Listeners());
            rail.SelectedIndex = 1; // a change while unloaded subscribes nothing again
            windows.Set(false);
            Assert.Equal(before, Listeners());
        });
    }
}

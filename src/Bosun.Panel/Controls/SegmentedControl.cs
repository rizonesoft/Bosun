using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Bosun.Panel.Controls;

/// <summary>
/// A short exclusive choice in one press (D02 T03 §4, design/SPEC.md §19 Segmented control): its choices side by side,
/// one selected, a sliding indicator behind it (Theme/RailMarker.cs, horizontal, over the motion gate's fast duration).
/// Left and Right move the selection, Home and End go to the first and the last; the focus moves with it, so the choice
/// is made by the keyboard alone. A screen reader finds a list of choices with the selected one marked, as a list box
/// gives it. Each segment is a 32 px target (Theme/Controls.xaml, its <c>Segment</c> style).
/// </summary>
public class SegmentedControl : ListBox
{
    public SegmentedControl() => SelectionMode = SelectionMode.Single;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End))
        {
            base.OnKeyDown(e);
            return;
        }
        if (Target(e.Key) is { } to) Select(to);
        e.Handled = true;
    }

    /// <summary>
    /// The segment a key goes to, skipping disabled ones, so the selection and the keyboard never part (completion
    /// review: Right onto a disabled segment selected it while the focus, refused, stayed behind): Left and Right the
    /// nearest enabled one that way, or none at the end; Home and End the first and the last enabled one.
    /// </summary>
    internal int? Target(Key key)
    {
        bool Enabled(int i) => (ItemContainerGenerator.ContainerFromIndex(i) as UIElement ?? Items[i] as UIElement)?.IsEnabled ?? true;
        var enabled = Enumerable.Range(0, Items.Count).Where(Enabled).ToList();
        if (enabled.Count == 0) return null;
        return key switch
        {
            Key.Left => enabled.LastOrDefault(i => i < SelectedIndex, -1) is var left and >= 0 ? left : null,
            Key.Right => enabled.FirstOrDefault(i => i > SelectedIndex, -1) is var right and >= 0 ? right : null,
            Key.Home => enabled[0],
            Key.End => enabled[^1],
            _ => null,
        };
    }

    /// <summary>Selects a segment and gives it the keyboard, as an arrow key does.</summary>
    public void Select(int index)
    {
        SelectedIndex = index;
        if (ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem segment) segment.Focus();
    }
}

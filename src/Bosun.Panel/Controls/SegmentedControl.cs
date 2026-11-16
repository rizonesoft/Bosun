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
        var to = e.Key switch
        {
            Key.Left => SelectedIndex - 1,
            Key.Right => SelectedIndex + 1,
            Key.Home => 0,
            Key.End => Items.Count - 1,
            _ => int.MinValue,
        };
        if (to == int.MinValue || Items.Count == 0)
        {
            base.OnKeyDown(e);
            return;
        }
        Select(Math.Clamp(to, 0, Items.Count - 1));
        e.Handled = true;
    }

    /// <summary>Selects a segment and gives it the keyboard, as an arrow key does.</summary>
    public void Select(int index)
    {
        SelectedIndex = index;
        if (ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem segment) segment.Focus();
    }
}

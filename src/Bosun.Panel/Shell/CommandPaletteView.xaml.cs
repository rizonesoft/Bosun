using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Shell;

/// <summary>
/// The command palette's overlay (D02 T03 §8). It shows while its <see cref="CommandPalette"/> is open, takes the
/// keyboard into its search field as it opens, and gives the keyboard back to where it was as it closes. It fades in
/// over the motion gate's fast duration, at once with animation effects off, and holds no clock once hidden.
/// </summary>
public partial class CommandPaletteView : UserControl
{
    private CommandPalette? _palette;
    private IInputElement? _before;

    public CommandPaletteView()
    {
        InitializeComponent();
        SearchGlyph.Kind = Glyphs.For(GlyphAction.Search);
        Visibility = Visibility.Collapsed;
        DataContextChanged += (_, _) => Bind(DataContext as CommandPalette);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) Card.BeginAnimation(OpacityProperty, null);
        };
        Results.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnRowClick), true);
    }

    /// <summary>Whether the fade is under way.</summary>
    public bool Fading => Card.HasAnimatedProperties;

    private void Bind(CommandPalette? palette)
    {
        if (_palette is not null)
        {
            _palette.PropertyChanged -= OnChanged;
            _palette.Closed -= GiveBack;
        }
        _palette = palette;
        if (palette is null) return;
        palette.PropertyChanged += OnChanged;
        palette.Closed += GiveBack;
        Show(palette.IsOpen);
        Nothing.Visibility = palette.NothingMatched ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_palette is null) return;
        if (e.PropertyName == nameof(CommandPalette.IsOpen)) Show(_palette.IsOpen);
        if (e.PropertyName is nameof(CommandPalette.NothingMatched) or nameof(CommandPalette.IsOpen))
            Nothing.Visibility = _palette.NothingMatched ? Visibility.Visible : Visibility.Collapsed;
        if (e.PropertyName == nameof(CommandPalette.Selected) && _palette.Selected is { } item) Results.ScrollIntoView(item);
    }

    private void Show(bool open)
    {
        if (!open)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        // Where the keyboard was, before the palette takes it: given back as it closes.
        _before = Keyboard.FocusedElement ?? FocusManager.GetFocusedElement(FocusManager.GetFocusScope(this));
        Visibility = Visibility.Visible;
        Fade();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Search.Focus();
            Search.SelectAll();
        });
    }

    private void Fade()
    {
        Card.BeginAnimation(OpacityProperty, null);
        Card.Opacity = 1;
        if (TryFindResource(Motion.GateKey) is not Motion { Animates: true } gate || gate.Fast.TimeSpan <= TimeSpan.Zero) return;
        var fade = new DoubleAnimation(0, 1, gate.Fast) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => Card.BeginAnimation(OpacityProperty, null);
        Card.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The keyboard goes back to where it was, when that is still there to take it.</summary>
    private void GiveBack()
    {
        var before = _before;
        _before = null;
        if (before is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
        else if (before is IInputElement other && before is not UIElement) other.Focus();
    }

    private async void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (_palette is null) return;
        switch (e.Key)
        {
            case Key.Down:
                _palette.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                _palette.Move(-1);
                e.Handled = true;
                break;
            case Key.Escape:
                _palette.Close();
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await _palette.RunSelectedAsync();
                break;
        }
    }

    private async void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (_palette is null || (e.OriginalSource as DependencyObject)?.FindItem(Results) is not PaletteRow row) return;
        e.Handled = true;
        await _palette.RunAsync(row);
    }

    private void OnScrim(object sender, MouseButtonEventArgs e) => _palette?.Close();
}

/// <summary>Finds the palette row a click landed on.</summary>
internal static class RowFinder
{
    public static object? FindItem(this DependencyObject from, ItemsControl list)
    {
        for (var at = from; at is not null; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
            if (at is ListBoxItem item) return list.ItemContainerGenerator.ItemFromContainer(item);
        return null;
    }
}

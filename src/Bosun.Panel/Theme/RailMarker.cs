using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Bosun.Panel.Theme;

/// <summary>
/// The rail's one marker (D02 T03 §3, design/SPEC.md §1 and §19): a bar beside the selected row, the same element
/// whichever row that is, sliding to the new row over the motion gate's normal duration with its easing, so the eye
/// follows the change. It is the <c>PART_Marker</c> of the <c>NavList</c> template, moved here; with Windows' animation
/// effects off, or with no gate, it lands at once, and an effect turned off mid-slide lands it at once too. Its clock
/// and every subscription go when the list is unloaded, hidden or its window minimised.
/// </summary>
public static class RailMarker
{
    public static readonly DependencyProperty SlidesProperty = DependencyProperty.RegisterAttached(
        "Slides", typeof(bool), typeof(RailMarker), new PropertyMetadata(false, OnSlidesChanged));

    private static readonly DependencyProperty MoverProperty = DependencyProperty.RegisterAttached(
        "Mover", typeof(Mover), typeof(RailMarker), new PropertyMetadata(null));

    public static bool GetSlides(DependencyObject element) => (bool)element.GetValue(SlidesProperty);

    public static void SetSlides(DependencyObject element, bool value) => element.SetValue(SlidesProperty, value);

    /// <summary>Where the marker is now (its top, moving during a slide) and where it is going; null before it is placed.</summary>
    public static (double Now, double Target)? Position(ListBox list) =>
        list.GetValue(MoverProperty) is Mover { Placed: true } mover ? (mover.Now, mover.Target) : null;

    /// <summary>Whether the marker is part-way through a slide.</summary>
    public static bool IsSliding(ListBox list) => list.GetValue(MoverProperty) is Mover { Sliding: true };

    private static void OnSlidesChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ListBox list) return;
        if (list.GetValue(MoverProperty) is Mover old)
        {
            old.Detach();
            list.ClearValue(MoverProperty);
        }
        if (e.NewValue is true) list.SetValue(MoverProperty, new Mover(list));
    }

    private sealed class Mover
    {
        private readonly ListBox _list;
        private readonly TranslateTransform _move = new();
        private Border? _marker;
        private Motion? _gate;
        private Window? _window;
        private bool _hooked;
        private bool _suspended; // hidden or minimised: no gate and no layout listened to until it is back
        private int _slide; // which slide is the current one: a slide replaced by another never lands the marker

        public Mover(ListBox list)
        {
            _list = list;
            list.Loaded += OnLoaded;
            list.Unloaded += OnUnloaded;
            list.SelectionChanged += OnSelectionChanged;
            list.IsVisibleChanged += OnVisibleChanged;
            if (list.IsLoaded) OnLoaded(list, new RoutedEventArgs());
        }

        public bool Placed { get; private set; }

        public bool Sliding { get; private set; }

        public double Target { get; private set; }

        public double Now => _move.Y;

        public void Detach()
        {
            Unhook();
            _list.Loaded -= OnLoaded;
            _list.Unloaded -= OnUnloaded;
            _list.SelectionChanged -= OnSelectionChanged;
            _list.IsVisibleChanged -= OnVisibleChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_hooked) return;
            _list.ApplyTemplate();
            _marker = _list.Template?.FindName("PART_Marker", _list) as Border;
            if (_marker is null) return; // a template without the part: nothing to move
            _marker.RenderTransform = _move;
            _hooked = true;
            Gate();
            _window = Window.GetWindow(_list);
            if (_window is not null) _window.StateChanged += OnWindowState;
            _list.LayoutUpdated += OnLayoutUpdated;
            Place(animate: false);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Unhook();

        /// <summary>The application's motion gate, looked for until it is found: one installed after the rail loaded counts too.</summary>
        private Motion? Gate()
        {
            if (!_hooked || _suspended) return null; // unloaded, hidden or minimised: nothing is subscribed again
            if (_gate is null && _list.TryFindResource(Motion.GateKey) is Motion gate)
            {
                _gate = gate;
                _gate.Changed += Land;
            }
            return _gate;
        }

        private void Unhook()
        {
            if (!_hooked) return;
            Land();
            if (_gate is not null) _gate.Changed -= Land;
            if (_window is not null) _window.StateChanged -= OnWindowState;
            if (!_suspended) _list.LayoutUpdated -= OnLayoutUpdated;
            _hooked = false;
            _suspended = false;
            _gate = null;
            _window = null;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, _list)) Place(animate: true);
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Rest(e.NewValue is false);

        private void OnWindowState(object? sender, EventArgs e) => Rest(_window?.WindowState == WindowState.Minimized);

        /// <summary>
        /// Hidden or minimised, the marker lands and lets go of the gate and of every layout pass (review round 1); back,
        /// it takes them again and is placed. It keeps only what tells it it is back: its visibility and its window's state.
        /// </summary>
        private void Rest(bool away)
        {
            if (!_hooked || away == _suspended) return;
            if (away)
            {
                Land();
                if (_gate is not null) _gate.Changed -= Land;
                _gate = null;
                _list.LayoutUpdated -= OnLayoutUpdated;
                _suspended = true;
                return;
            }
            _suspended = false;
            Gate();
            _list.LayoutUpdated += OnLayoutUpdated;
            Place(animate: false);
        }

        /// <summary>A row that moved (a resize, a scroll, a theme with other measures) takes the marker with it, at once.</summary>
        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (!Sliding) Place(animate: false);
        }

        /// <summary>Ends a slide where it was going, now: the clock is let go.</summary>
        private void Land()
        {
            _move.BeginAnimation(TranslateTransform.YProperty, null);
            _move.Y = Target;
            Sliding = false;
        }

        private void Place(bool animate)
        {
            if (_marker is null || VisualTreeHelper.GetParent(_marker) is not Visual layer) return;
            if (_list.SelectedItem is null || _list.ItemContainerGenerator.ContainerFromItem(_list.SelectedItem) is not FrameworkElement row || !row.IsLoaded ||
                !row.IsDescendantOf(_list))
            {
                _marker.Visibility = Visibility.Collapsed;
                return;
            }
            // The bar sits where the row's own padding leaves it: 8 in from the left, 6 down plus a 2 px inset.
            var at = row.TranslatePoint(new Point(0, 0), (UIElement)layer);
            var top = at.Y + 8;
            var height = Math.Max(0, row.ActualHeight - 16);
            Canvas.SetLeft(_marker, at.X + 8);
            Canvas.SetTop(_marker, 0);
            if (double.IsNaN(_marker.Height) || Math.Abs(_marker.Height - height) > 0.5) _marker.Height = height; // NaN: not set yet
            _marker.Visibility = Visibility.Visible;
            if (Placed && Math.Abs(top - Target) < 0.5) return;
            Target = top;
            if (!Placed || !animate || Gate() is not { Animates: true } gate || gate.Normal.TimeSpan <= TimeSpan.Zero || !_list.IsVisible)
            {
                Land();
                Placed = true;
                return;
            }
            var slide = new DoubleAnimationUsingKeyFrames { Duration = gate.Normal, FillBehavior = FillBehavior.HoldEnd };
            slide.KeyFrames.Add(new SplineDoubleKeyFrame(top, KeyTime.FromTimeSpan(gate.Normal.TimeSpan), gate.Ease));
            var mine = ++_slide;
            slide.Completed += (_, _) =>
            {
                if (mine == _slide && Sliding) Land();
            };
            Sliding = true;
            _move.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
        }
    }
}

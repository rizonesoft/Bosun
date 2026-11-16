using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bosun.Panel.Accessibility;

namespace Bosun.Panel.Controls;

/// <summary>The shape of a page's data, which its loading state takes (design/SPEC.md §19, Skeleton).</summary>
public enum SkeletonShape
{
    /// <summary>Cards stacked down the page: the default.</summary>
    Card,

    /// <summary>A table: a header and rows.</summary>
    Table,

    /// <summary>A row of metric cards, each with a round gauge.</summary>
    Gauge,
}

/// <summary>
/// The loading state every page shows (D02 T03 §7, design/SPEC.md §1 and §19): blocks shaped like the page's data
/// under a dim "Loading" line, so a load never looks like missing data. It appears only <see cref="Delay"/> (150 ms)
/// after it is made active, so a fast load does not flash, and it says <c>Loading &lt;title&gt;</c> once per load, when it
/// appears, through the live region; a load too fast to show it says nothing. Made inactive (the load ended, or its
/// page left the screen), it hides at once, and a delay that had not ended is dropped: its timer is disposed, and a
/// tick already on its way is recognised as old and ignored.
///
/// The blocks are drawn from the theme (B.Raised on B.Surface with a B.Line border, R.Card corners) and do not move:
/// no token gives a shimmer's speed, and a shimmer redraws the window for as long as a load lasts.
/// </summary>
public sealed class Skeleton : Border
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(150);

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(SkeletonShape), typeof(Skeleton), new PropertyMetadata(SkeletonShape.Card, (d, _) => ((Skeleton)d).Draw()));

    public static readonly DependencyProperty DelayProperty = DependencyProperty.Register(
        nameof(Delay), typeof(TimeSpan), typeof(Skeleton), new PropertyMetadata(DefaultDelay));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(Skeleton), new PropertyMetadata("", (d, _) => ((Skeleton)d).SayTitle()));

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(Skeleton), new PropertyMetadata(false, (d, e) => ((Skeleton)d).ActiveChanged((bool)e.NewValue)));

    private readonly TextBlock _caption;
    private readonly StackPanel _blocks;
    private ITimer? _timer;
    private int _load; // which activation a tick belongs to: one that arrives for an earlier one is ignored

    public Skeleton()
    {
        Focusable = false;
        _caption = new TextBlock { Name = "Loading", Margin = new Thickness(0, 0, 0, 12) };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "B.TextMute");
        LiveRegion.SetAnnounce(_caption, true);
        AutomationProperties.SetItemStatus(_caption, "Loading");
        _blocks = new StackPanel();
        Child = new StackPanel { Children = { _caption, _blocks } };
        Visibility = Visibility.Collapsed;
        Draw();
    }

    public SkeletonShape Shape
    {
        get => (SkeletonShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    /// <summary>How long after it is made active it appears: 150 ms (design/SPEC.md §19); zero for a picture.</summary>
    public TimeSpan Delay
    {
        get => (TimeSpan)GetValue(DelayProperty);
        set => SetValue(DelayProperty, value);
    }

    /// <summary>The page's title, which the announcement names.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Whether a load is under way on a page on screen.</summary>
    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    /// <summary>What times the delay; the system's clock unless a test gives its own.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Whether the blocks are on screen now.</summary>
    public bool Revealed => Visibility == Visibility.Visible;

    private void ActiveChanged(bool active)
    {
        var load = ++_load;
        _timer?.Dispose();
        _timer = null;
        Hide();
        if (!active) return;
        if (Delay <= TimeSpan.Zero)
        {
            Reveal();
            return;
        }
        // The tick comes on a thread of the clock's; it is passed to this control's thread, and acted on only if the
        // same load is still under way there.
        _timer = Clock.CreateTimer(_ => Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (load == _load && Active) Reveal();
        }), null, Delay, Timeout.InfiniteTimeSpan);
    }

    private void Reveal()
    {
        _timer?.Dispose();
        _timer = null;
        Visibility = Visibility.Visible;
        SayTitle(); // said once, now
    }

    /// <summary>
    /// The line, while shown: a title that arrives after it (a page and its frame bound in the same moment) is put in
    /// it, and the live region, which says the latest text of changes made together, still says it once.
    /// </summary>
    private void SayTitle()
    {
        if (!Revealed) return;
        _caption.Text = string.IsNullOrWhiteSpace(Title) ? "Loading" : $"Loading {Title}";
    }

    private void Hide()
    {
        Visibility = Visibility.Collapsed;
        _caption.Text = ""; // so the next load's line is a change, and is said again
    }

    /// <summary>The blocks for the shape.</summary>
    private void Draw()
    {
        _blocks.Children.Clear();
        switch (Shape)
        {
            case SkeletonShape.Table:
                _blocks.Children.Add(Block(height: 36, bottom: 4));
                for (var line = 0; line < 6; line++) _blocks.Children.Add(Block(height: 28, bottom: 4, radius: "R.Control"));
                break;
            case SkeletonShape.Gauge:
                var row = new UniformGrid { Columns = 3, Rows = 1, Margin = new Thickness(0, 0, 0, 12) };
                for (var tile = 0; tile < 3; tile++)
                {
                    var card = Block(height: 160, bottom: 0);
                    card.Margin = new Thickness(0, 0, tile < 2 ? 12 : 0, 0);
                    var ring = new Ellipse { Width = 96, Height = 96, StrokeThickness = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "B.Line");
                    card.Child = ring;
                    row.Children.Add(card);
                }
                _blocks.Children.Add(row);
                _blocks.Children.Add(Block(height: 110, bottom: 12));
                break;
            default:
                for (var card = 0; card < 3; card++) _blocks.Children.Add(Block(height: 110, bottom: 12));
                break;
        }
    }

    private static Border Block(double height, double bottom, string radius = "R.Card")
    {
        var block = new Border { Height = height, Margin = new Thickness(0, 0, 0, bottom), BorderThickness = new Thickness(1) };
        block.SetResourceReference(BackgroundProperty, "B.Raised");
        block.SetResourceReference(BorderBrushProperty, "B.Line");
        block.SetResourceReference(CornerRadiusProperty, radius);
        return block;
    }
}

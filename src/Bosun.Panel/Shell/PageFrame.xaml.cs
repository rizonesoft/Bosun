using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Shell;

/// <summary>
/// The frame a page is drawn in: its header and one of its four states (D02 T02 §4), the loading state's skeleton
/// and the move from one page to the next (D02 T03 §7).
///
/// The skeleton is active while the page is loading and on screen: a page navigated away from, or in a window
/// minimised or hidden to the tray, is not shown (<see cref="PageBase.IsShown"/>), so its delay is dropped and it says
/// nothing. When the frame's page changes (navigation, never a state or data change of the same page), everything it
/// draws fades in from nothing and rises 4 px over <see cref="Motion.Normal"/> with <see cref="Motion.Ease"/>, from
/// the application's gate (<see cref="Motion.GateKey"/>). With no gate (a picture drawn off screen), with Windows'
/// animation effects off, or when the page leaves the screen, it is drawn at once.
/// </summary>
public partial class PageFrame : UserControl
{
    /// <summary>The least the failed state's message is given, however little room there is: three lines or so.</summary>
    public const double MinMessageHeight = 48;

    /// <summary>How far the content rises as a new page comes in (design/SPEC.md §19).</summary>
    public const double RiseBy = 4;

    public static readonly DependencyProperty SkeletonDelayProperty = DependencyProperty.Register(
        nameof(SkeletonDelay), typeof(TimeSpan), typeof(PageFrame), new PropertyMetadata(Controls.Skeleton.DefaultDelay, (d, e) => ((PageFrame)d).Skeleton.Delay = (TimeSpan)e.NewValue));

    private PageBase? _page;
    private Motion? _motion;
    private bool _moveWhenShown; // a page change waiting for its page to come on screen
    private int _move;           // which move an animation belongs to: an old one's end leaves a newer one alone

    public PageFrame()
    {
        InitializeComponent();
        StateArea.SizeChanged += (_, _) => HoldTheMessage();
        Retry.SizeChanged += (_, _) => HoldTheMessage();
        FailedHeading.SizeChanged += (_, _) => HoldTheMessage();
        DataContextChanged += (_, e) => PageChanged(e.OldValue as PageBase, e.NewValue as PageBase);
        Loaded += (_, _) => Listen(TryFindResource(Motion.GateKey) as Motion);
        Unloaded += (_, _) =>
        {
            Listen(null);
            Finish(); // a frame taken off screen keeps no animation running
        };
    }

    /// <summary>How long a load waits before its skeleton appears: 150 ms; zero for a picture, which can't wait.</summary>
    public TimeSpan SkeletonDelay
    {
        get => (TimeSpan)GetValue(SkeletonDelayProperty);
        set => SetValue(SkeletonDelayProperty, value);
    }

    /// <summary>What times the skeleton's delay; the system's clock unless a test gives its own.</summary>
    public TimeProvider Clock
    {
        get => Skeleton.Clock;
        set => Skeleton.Clock = value;
    }

    /// <summary>The fade and the rise of the last page change started, for a test to hold to the design.</summary>
    internal (DoubleAnimationUsingKeyFrames Fade, DoubleAnimationUsingKeyFrames Rise)? LastMove { get; private set; }

    /// <summary>Whether a page change is being drawn now.</summary>
    public bool IsMoving => Body.HasAnimatedProperties || Rise.HasAnimatedProperties;

    private void PageChanged(PageBase? old, PageBase? page)
    {
        if (old is not null) old.PropertyChanged -= OnPageChanged;
        _page = page;
        if (page is not null) page.PropertyChanged += OnPageChanged;
        UpdateSkeleton();
        Finish();
        // Navigation is a page replacing another; the first page a frame is given is simply there. The shell gives the
        // frame its new page before it shows it, so the move starts when the page comes on screen.
        _moveWhenShown = old is not null && page is not null && !ReferenceEquals(old, page);
        if (_moveWhenShown && page!.IsShown) Move();
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _page)) return; // a page this frame no longer shows changes nothing here
        if (e.PropertyName is nameof(PageBase.State) or nameof(PageBase.IsShown)) UpdateSkeleton();
        if (e.PropertyName != nameof(PageBase.IsShown)) return;
        if (_page?.IsShown == true && _moveWhenShown) Move();
        else if (_page?.IsShown == false)
        {
            _moveWhenShown = false; // left the screen: shown again later, it is simply there
            Finish();
        }
    }

    private void UpdateSkeleton() => Skeleton.Active = _page is { State: PageState.Loading, IsShown: true };

    private void Listen(Motion? motion)
    {
        if (_motion is not null) _motion.Changed -= OnMotionChanged;
        _motion = motion;
        if (_motion is not null) _motion.Changed += OnMotionChanged;
    }

    private void OnMotionChanged()
    {
        if (_motion?.Animates != true) Finish(); // turned off mid-move: the page is simply there
    }

    /// <summary>The new page fades in and rises, when there is a gate, it animates, and the page is on screen.</summary>
    private void Move()
    {
        _moveWhenShown = false;
        Finish();
        var move = _move;
        // The gate is looked for again if there was none when the frame loaded, and then listened to as well.
        if (_motion is null && TryFindResource(Motion.GateKey) is Motion found) Listen(found);
        var motion = _motion;
        if (motion is not { Animates: true } || _page is not { IsShown: true }) return;
        var length = motion.Normal;
        var fade = new DoubleAnimationUsingKeyFrames { Duration = length };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(length.TimeSpan), motion.Ease));
        var rise = new DoubleAnimationUsingKeyFrames { Duration = length };
        rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(RiseBy, KeyTime.FromPercent(0)));
        rise.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(length.TimeSpan), motion.Ease));
        // Each removes itself when it ends, so nothing is left holding the values, and no clock outlives the move.
        fade.Completed += (_, _) =>
        {
            if (move == _move) Body.BeginAnimation(OpacityProperty, null);
        };
        rise.Completed += (_, _) =>
        {
            if (move == _move) Rise.BeginAnimation(TranslateTransform.YProperty, null);
        };
        LastMove = (fade, rise);
        Body.BeginAnimation(OpacityProperty, fade);
        Rise.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>Ends a page change at once: the content is where it ends up, and no animation is left.</summary>
    private void Finish()
    {
        _move++;
        Body.BeginAnimation(OpacityProperty, null);
        Rise.BeginAnimation(TranslateTransform.YProperty, null);
        Body.Opacity = 1;
        Rise.Y = 0;
    }

    /// <summary>
    /// Holds the failed state's message to the height left once the heading and Retry have theirs: a long message
    /// scrolls there, and Retry is never pushed out of the page by what the tool said.
    /// </summary>
    private void HoldTheMessage()
    {
        var others = FailedHeading.ActualHeight + FailureScroll.Margin.Top + FailureScroll.Margin.Bottom
                     + Retry.ActualHeight + Retry.Margin.Top + Retry.Margin.Bottom;
        // Rounded down: text heights are fractions of a pixel, and a fraction over would put Retry's last pixel
        // outside the frame.
        FailureScroll.MaxHeight = Math.Max(MinMessageHeight, Math.Floor(StateArea.ActualHeight - others));
    }
}

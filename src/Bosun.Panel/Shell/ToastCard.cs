using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bosun.Panel.Shell;

/// <summary>
/// One toast on screen (D02 T03 §9): its state pill, its words (at most three lines, the whole result in its tooltip,
/// its help text and the Activity line), and its Dismiss button on the first line. The pointer over it or the keyboard
/// in it holds its time, each on its own (<see cref="ToastHost.HoldByPointer"/>, <see cref="ToastHost.HoldByFocus"/>).
/// </summary>
public class ToastCard : Control
{
    public static readonly DependencyProperty HostProperty = DependencyProperty.Register(
        nameof(Host), typeof(ToastHost), typeof(ToastCard), new FrameworkPropertyMetadata(null));

    private static readonly DependencyPropertyKey IsCutKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCut), typeof(bool), typeof(ToastCard), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsCutProperty = IsCutKey.DependencyProperty;

    /// <summary>The lines a collapsed message shows.</summary>
    public const int Lines = 3;

    /// <summary>The height of one of its lines (the template's LineHeight).</summary>
    public const double LineHeight = 19;

    static ToastCard() => FocusableProperty.OverrideMetadata(typeof(ToastCard), new FrameworkPropertyMetadata(false));

    public ToastCard()
    {
        Dismiss = new Relay(() =>
        {
            if (Toast is { } toast) Host?.Dismiss(toast);
        });
        ShowMore = new Relay(() =>
        {
            if (Toast is { } toast) Host?.Expand(toast, !toast.Expanded);
        });
        SizeChanged += (_, _) => Measure();
        DataContextChanged += (_, _) => Measure();
        MouseEnter += (_, _) => Hold(pointer: true);
        MouseLeave += (_, _) => Hold(pointer: false);
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (Toast is { } toast) Host?.HoldByFocus(toast, e.NewValue is true);
        };
    }

    /// <summary>The shell's toasts, which this card's toast is one of.</summary>
    public ToastHost? Host { get => (ToastHost?)GetValue(HostProperty); set => SetValue(HostProperty, value); }

    public Toast? Toast => DataContext as Toast;

    /// <summary>Its Dismiss button's command.</summary>
    public ICommand Dismiss { get; }

    /// <summary>Its Show more and Show less button's command (D02 T04 §7).</summary>
    public ICommand ShowMore { get; }

    /// <summary>
    /// Whether its message runs past <see cref="Lines"/> lines as it is laid out at the card's width, wrapping included:
    /// only then is Show more offered.
    /// </summary>
    public bool IsCut { get => (bool)GetValue(IsCutProperty); private set => SetValue(IsCutKey, value); }

    private void Measure()
    {
        if (Toast is not { } toast || GetTemplateChild("Words") is not TextBlock words || words.ActualWidth <= 0)
        {
            return;
        }
        var text = new FormattedText(toast.Message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(words.FontFamily, words.FontStyle, words.FontWeight, words.FontStretch), words.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = words.ActualWidth,
            LineHeight = LineHeight,
        };
        IsCut = text.Height > Lines * LineHeight + 0.5;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("Words") is TextBlock words) words.SizeChanged += (_, _) => Measure();
    }

    private void Hold(bool pointer)
    {
        if (Toast is { } toast) Host?.HoldByPointer(toast, pointer);
    }

    private sealed class Relay(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run();
    }
}

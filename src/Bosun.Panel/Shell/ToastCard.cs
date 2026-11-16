using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

    static ToastCard() => FocusableProperty.OverrideMetadata(typeof(ToastCard), new FrameworkPropertyMetadata(false));

    public ToastCard()
    {
        Dismiss = new Relay(() =>
        {
            if (Toast is { } toast) Host?.Dismiss(toast);
        });
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

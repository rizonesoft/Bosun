using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Bosun.Panel.Shell;

/// <summary>
/// The Save, Discard or Keep editing question (D02 T02 §4). Each button gives its choice to the shell, which carries
/// on with what the person was doing, or does not.
/// </summary>
public partial class LeaveDialog : UserControl
{
    public LeaveDialog()
    {
        InitializeComponent();
        // The keyboard goes to Save when the question appears: Enter saves, Esc keeps editing.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Save.Focus());
        };
    }

    private void Answer(LeaveChoice choice) => (DataContext as ShellViewModel)?.Answer(choice);

    private void OnSave(object sender, RoutedEventArgs e) => Answer(LeaveChoice.Save);

    private void OnDiscard(object sender, RoutedEventArgs e) => Answer(LeaveChoice.Discard);

    private void OnKeep(object sender, RoutedEventArgs e) => Answer(LeaveChoice.Stay);
}

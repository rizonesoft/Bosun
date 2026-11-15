using System.Windows.Input;

namespace Bosun.Panel.Mvvm;

/// <summary>
/// A command for an action that takes time (D02 T02 §2, after Steward's AsyncCommand). It can't re-enter: while one
/// run is under way a second click starts nothing, and the control bound to it is disabled.
///
/// What the action throws never escapes <see cref="Execute"/>, the entry a button calls: an exception out of an
/// <c>async void</c> lands on the UI thread, where it ends the panel. It is given to <c>failed</c> instead, which
/// every command must have, so a page shows the failure and the command can run again. A caller that awaits
/// <see cref="ExecuteAsync"/> gets the exception itself.
/// </summary>
public sealed class AsyncCommand(Func<Task> execute, Action<Exception> failed, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    /// <summary>True while a run is under way.</summary>
    public bool IsRunning => _running;

    public bool CanExecute(object? parameter = null) => !_running && (canExecute?.Invoke() ?? true);

    /// <summary>Runs the action when it may run; what it throws goes to <c>failed</c>, never out of here.</summary>
    public async void Execute(object? parameter = null)
    {
        try
        {
            await ExecuteAsync();
        }
        // Everything the action throws, on purpose: this is the one place between a page's action and the UI
        // thread's crash handler, and a failed action is a state the page shows, not a reason to close the panel.
        catch (Exception e)
        {
            failed(e);
        }
    }

    /// <summary>Runs the action when it may run, and gives what it throws to the caller.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute()) return;
        _running = true;
        Refresh();
        try
        {
            await execute();
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    /// <summary>Has the bound control ask <see cref="CanExecute"/> again: call it when what <c>canExecute</c> reads has changed.</summary>
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

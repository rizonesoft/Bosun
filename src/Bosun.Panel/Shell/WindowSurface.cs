namespace Bosun.Panel.Shell;

/// <summary>
/// What the shell needs of the window it sits in (D02 T02 §4): the window buttons' three operations, showing and
/// activating it, and its state. <see cref="MainWindow"/> is the real one; a test stands in its own, since a test
/// must never put a window on the person's desktop.
/// </summary>
public interface IWindowSurface
{
    bool IsVisible { get; }

    bool IsMinimised { get; }

    bool IsMaximised { get; }

    void Show();

    /// <summary>Back from minimised to the size it had.</summary>
    void Restore();

    void Activate();

    void Minimise();

    /// <summary>Maximised becomes its normal size, and anything else becomes maximised.</summary>
    void ToggleMaximise();

    /// <summary>Asks the window to close. A window with unsaved edits asks the person first (<see cref="ExitGuard"/>).</summary>
    void Close();
}

/// <summary>Bringing the panel's window forward when a later launch asks for it (D02 T02 §1 raises the request).</summary>
public static class WindowActivation
{
    /// <summary>A minimised window is restored, a hidden one shown, and it is activated, in that order.</summary>
    public static void BringForward(IWindowSurface window)
    {
        if (window.IsMinimised) window.Restore();
        if (!window.IsVisible) window.Show();
        window.Activate();
    }
}

/// <summary>Making the panel's first window and putting it on screen.</summary>
public static class WindowStart
{
    /// <summary>Makes the window around a shell and shows it.</summary>
    public static IWindowSurface Open(ShellViewModel shell, Func<ShellViewModel, IWindowSurface> make)
    {
        var window = make(shell);
        window.Show();
        return window;
    }

    /// <summary>The shell a panel starts with: the rail's sections and an activity log on this thread, saying it is ready.</summary>
    public static ShellViewModel NewShell(System.Windows.Threading.Dispatcher dispatcher)
    {
        var log = new ActivityLog(new UiLogSink(dispatcher));
        var shell = new ShellViewModel(Sections.All(), log);
        log.Info("Panel ready");
        return shell;
    }
}

/// <summary>
/// Closing the window, and so leaving the panel, goes through the same question as leaving a page (D02 T02 §4): with
/// unsaved edits the close is held back, the person is asked, and the window closes only if the shell's
/// <see cref="ShellViewModel.LeaveAsync"/> says the page may be left. Alt+F4 and the close button are the same close.
/// </summary>
public sealed class ExitGuard(ShellViewModel shell, IWindowSurface window)
{
    private bool _agreed;

    /// <summary>
    /// Called as the window is closing. True lets it close now. False holds it back: the person is being asked, and
    /// the window is closed again, for good, if they agree.
    /// </summary>
    public bool MayCloseNow()
    {
        if (_agreed || !shell.Page.HasUnsavedEdits) return true;
        _ = AskThenCloseAsync();
        return false;
    }

    /// <summary>Asks, and closes the window when the page may be left. Awaitable, so the tray's Exit can use it too.</summary>
    public async Task<bool> AskThenCloseAsync()
    {
        bool may;
        try
        {
            may = await shell.LeaveAsync();
        }
        // The question itself failed (the dialog could not be put up): the window stays open, the edits stay, and
        // the Activity pane says why.
        catch (Exception e)
        {
            shell.Log.Fail($"Close Bosun Panel: {e.Message}");
            return false;
        }
        if (!may) return false;
        _agreed = true;
        window.Close();
        return true;
    }
}

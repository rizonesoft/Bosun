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

    /// <summary>Off the screen and the taskbar, and still there: what closing does while the panel lives in the tray.</summary>
    void Hide();

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

    /// <summary>
    /// Tells the shell whether its window is on screen (D02 T02 §8): a window that is minimised, or not shown, shows
    /// no page, so the page stops what it only does while looked at; back on screen, the page is shown again.
    /// </summary>
    public static void TellShell(ShellViewModel shell, IWindowSurface window)
    {
        if (window.IsMinimised || !window.IsVisible) shell.WindowHidden();
        else shell.WindowShown();
    }
}

/// <summary>Making the panel's first window and putting it on screen.</summary>
public static class WindowStart
{
    /// <summary>
    /// Makes the window around a shell and puts it on screen. With a tray, <paramref name="show"/> is the tray's
    /// start, which shows the window or, started for the tray, leaves it hidden until it is asked for (D02 T02 §7).
    /// </summary>
    public static IWindowSurface Open(ShellViewModel shell, Func<ShellViewModel, IWindowSurface> make, Action<IWindowSurface>? show = null)
    {
        var window = make(shell);
        if (show is null) window.Show();
        else show(window);
        return window;
    }

    /// <summary>
    /// The shell a panel starts with: an activity log on this thread, the rail's sections with the pages that are
    /// built over the panel's own settings and start-up entry, and the Activity pane at the height that was saved.
    /// Settings that could not be read say so in the Activity pane from the start.
    /// </summary>
    /// <param name="windowShown">False when the panel is started for the tray: no page is on screen yet.</param>
    public static ShellViewModel NewShell(System.Windows.Threading.Dispatcher dispatcher, Bosun.Core.PanelSettingsStore settings,
        Bosun.Core.StartupEntry startup, Action openStartupSettings, bool windowShown = true)
    {
        var log = new ActivityLog(new UiLogSink(dispatcher));
        var pages = new Dictionary<string, PageBase>
        {
            ["settings"] = new Bosun.Panel.Settings.SettingsPage(settings, startup, log, openStartupSettings),
        };
        var shell = new ShellViewModel(Sections.All(pages), log, settings, windowShown);
        log.Info("Panel ready");
        if (settings.Notice is { } notice) log.Warn(notice);
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
        if (_agreed) return true;
        // While a move to another page is still being asked about or saved, the window stays: closing now would
        // drop the save in the middle.
        if (shell.IsLeaving) return false;
        if (!shell.Page.HasUnsavedEdits) return true;
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

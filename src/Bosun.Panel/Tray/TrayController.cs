using Bosun.Core;
using Bosun.Panel.Shell;

namespace Bosun.Panel.Tray;

/// <summary>
/// The panel living in the tray (D02 T02 §7, design/SPEC.md §17): what the icon, its menu and the window's close do.
///
/// With an icon, closing the window hides it and the panel carries on, said once ever; the question about unsaved
/// edits is asked where they could be lost, at Exit. A hidden window's page is hidden too, so it reads nothing.
/// With no icon (Windows refused it) there is no tray: the window is shown whatever the panel was started with, and
/// its close asks and exits as it did before there was a tray, so the panel is never running where nobody can see it.
/// </summary>
public sealed class TrayController : IDisposable
{
    public const string TrayArgument = "--tray";
    public const string StillRunningTitle = "Bosun Panel is still running";
    public const string StillRunningText = "It is in the notification area. To close it, right-click its icon and choose Exit.";

    private readonly ShellViewModel _shell;
    private readonly ITrayIcon _icon;
    private readonly Notifier _notifier;
    private readonly PanelSettingsStore _settings;
    private readonly Action<int> _exit;
    private IWindowSurface? _window;
    private bool _hasTray, _saidThisRun, _exiting, _disposed;

    public TrayController(ShellViewModel shell, ITrayIcon icon, Notifier notifier, PanelSettingsStore settings, Action<int> exit,
        INeedsYouSource? needs = null, IQueueSummary? queue = null)
    {
        _shell = shell;
        _icon = icon;
        _notifier = notifier;
        _settings = settings;
        _exit = exit;
        Tray = new TrayViewModel(notifier, OpenAsync, OpenQueueAsync, ExitAsync, needs, queue);
    }

    /// <summary>The tray's state and menu.</summary>
    public TrayViewModel Tray { get; }

    /// <summary>Whether the panel has an icon in the tray.</summary>
    public bool HasTray => _hasTray;

    /// <summary>Whether the panel was started for the tray: by Windows at sign-in, through the Run value (D02 T02 §5).</summary>
    public static bool StartsInTray(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, TrayArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Puts the icon in the tray and the window on screen, or, started for the tray, leaves the window hidden until
    /// it is asked for. A refused icon is said in the Activity pane, and the window is then shown in any case.
    /// </summary>
    public void Start(IWindowSurface window, bool inTray)
    {
        _window = window;
        _icon.Menu = Tray.Menu;
        _icon.Opened += Open;
        _hasTray = _icon.Install(Tray.State, Tray.Tooltip);
        if (_hasTray)
        {
            _notifier.Sink = _icon;
            Tray.Changed += ShowState;
        }
        else _shell.Log.Warn("Windows did not add Bosun Panel's icon to the notification area, so closing the window exits the panel.");

        if (_hasTray && inTray) _shell.WindowHidden();
        else Open();
    }

    private void ShowState()
    {
        if (!_disposed) _icon.Update(Tray.State, Tray.Tooltip);
    }

    /// <summary>Brings the window forward, shown if it was hidden, with its page.</summary>
    public void Open()
    {
        if (_window is null || _disposed) return;
        WindowActivation.BringForward(_window);
        _shell.WindowShown();
    }

    private Task OpenAsync()
    {
        Open();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The menu's Queue line: the window first, so a question about unsaved edits is on screen, then the Queue page
    /// by the way the rail goes. A person who stays keeps the page and the edits.
    /// </summary>
    public async Task OpenQueueAsync()
    {
        Open();
        try
        {
            await _shell.GoToAsync(_shell.Sections.Single(section => section.Key == "queue"));
        }
        // The question could not be put, or there is no Queue page: said, and the page shown stays.
        catch (Exception e)
        {
            _shell.Log.Fail($"Open the Queue page: {e.Message}");
        }
    }

    /// <summary>
    /// The window is closing; true when the tray took the close: the window is hidden and the panel carries on. False
    /// leaves the close to the window, which asks about unsaved edits and exits: there is no tray, or the panel is
    /// on its way out.
    /// </summary>
    public bool TakeClose()
    {
        if (!_hasTray || _exiting || _window is null) return false;
        _window.Hide();
        _shell.WindowHidden();
        SayStillRunning();
        return true;
    }

    /// <summary>Said the first time ever, and whatever the notification settings say: it explains what the close just did.</summary>
    private void SayStillRunning()
    {
        if (_saidThisRun || _settings.Current.TrayNoticeSaid) return;
        if (!_notifier.Explain(StillRunningTitle, StillRunningText))
        {
            // Not said, so not marked said: the next close tries again.
            _shell.Log.Warn($"{StillRunningTitle}: {StillRunningText} (Windows did not show this as a notification.)");
            return;
        }
        _saidThisRun = true;
        var saved = _settings.Update(current => current with { TrayNoticeSaid = true });
        if (!saved.Succeeded) _shell.Log.Warn($"That the tray notice was shown could not be saved, so it will be shown once more: {saved.Reason}");
    }

    /// <summary>
    /// The menu's Exit. Unsaved edits are asked about first, with the window brought forward so the question is seen:
    /// staying, or a save that fails, keeps the panel running. Then the icon is removed and the panel exits.
    /// </summary>
    public async Task ExitAsync()
    {
        if (_exiting) return;
        if (_shell.Page.HasUnsavedEdits || _shell.IsLeaving) Open();
        bool may;
        try
        {
            may = await _shell.LeaveAsync();
        }
        // The question itself failed: the panel stays, the edits stay, and the Activity pane says why.
        catch (Exception e)
        {
            _shell.Log.Fail($"Exit Bosun Panel: {e.Message}");
            return;
        }
        if (!may) return;
        _exiting = true;
        Dispose();
        _exit(0);
    }

    /// <summary>Removes the icon. However the panel ends, none is left behind.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hasTray = false;
        _notifier.Sink = null;
        _icon.Opened -= Open;
        _icon.Dispose();
    }
}

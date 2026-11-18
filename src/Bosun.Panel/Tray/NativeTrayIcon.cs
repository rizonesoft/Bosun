namespace Bosun.Panel.Tray;

/// <summary>
/// The panel's icon in the notification area, as the rest of the panel sees it (D02 T02 §7). The real one is
/// <see cref="NativeTrayIcon"/>; a test stands in its own.
/// </summary>
public interface ITrayIcon : INotificationSink, IDisposable
{
    /// <summary>Puts the icon in the tray. False when Windows refuses it: there is then no tray.</summary>
    bool Install(TrayState state, string tooltip);

    /// <summary>
    /// Whether the icon is in the tray now. It can be lost after it was added: Windows' taskbar restarts, and may
    /// refuse to take the icon back.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Changes the icon and its tooltip. False when Windows refused the change: the icon may show an older state,
    /// and the change is tried again.
    /// </summary>
    bool Update(TrayState state, string tooltip);

    /// <summary>
    /// Raised when the person asks for the panel: a click, a double click, or the keyboard on the icon, or a click
    /// on one of its notifications.
    /// </summary>
    event Action? Opened;

    /// <summary>
    /// Raised when Windows says a notification is on screen. Windows can accept one and never show it (a queue, do
    /// not disturb), so this, not <see cref="INotificationSink.Notify"/>'s answer, is what says it was seen.
    /// </summary>
    event Action? NotificationShown;

    /// <summary>Gives the menu as it reads now; asked each time the menu is opened. The chosen line is run.</summary>
    Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }

    /// <summary>Raised as the menu opens, before <see cref="Menu"/> is asked: once for each opening.</summary>
    event Action? MenuOpening;

    /// <summary>What <see cref="Menu"/> gives has changed: a menu that is open shows the new words in place.</summary>
    void MenuChanged();
}

/// <summary>What a message from the tray icon means.</summary>
public enum TrayEvent
{
    None,

    /// <summary>The person asked for the panel.</summary>
    Open,

    /// <summary>The person asked for the menu: a right click, or the menu key.</summary>
    Menu,

    /// <summary>A notification from the icon is on screen.</summary>
    NotificationShown,

    /// <summary>Windows' taskbar started again: every icon is gone and must be added again.</summary>
    TaskbarCreated,
}

/// <summary>
/// The real tray icon (D02 T02 §7): Windows' own, through <see cref="ITrayShell"/>, which holds the calls to Windows
/// so that this class, with all its decisions, is the same in a test as in the panel. A click, a double click or the
/// keyboard opens the panel; a right click or the menu key shows the menu as a native popup and runs the line chosen;
/// a notification is the icon's own; and the icon is added again when the taskbar restarts.
/// </summary>
public sealed class NativeTrayIcon : ITrayIcon
{
    /// <summary>Windows keeps 128 characters of a tooltip, the last a terminator.</summary>
    public const int MaxTooltip = 127;

    // What Windows sends in the low word of lParam with version 4 of the icon's messages.
    public const int WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401,
        NIN_BALLOONSHOW = 0x0402, NIN_BALLOONUSERCLICK = 0x0405;

    private readonly ITrayShell _shell;
    private bool _installed, _wanted;
    private TrayState _state;
    private string _tooltip = "";
    private (TrayState State, string Tooltip)? _delivered; // what Windows last took; null when it has taken nothing

    public NativeTrayIcon(ITrayShell shell)
    {
        _shell = shell;
        _shell.Message += OnMessage;
    }

    public event Action? Opened;

    public event Action? NotificationShown;

    public event Action? MenuOpening;

    public Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }

    private IReadOnlyList<TrayMenuItem>? _open; // the lines of the menu on screen now, as it shows them

    public bool IsInstalled => _installed;

    public bool Install(TrayState state, string tooltip)
    {
        (_state, _tooltip) = (state, Cut(tooltip));
        _installed = _shell.Add(_state, _tooltip);
        _wanted = _installed; // a refused icon is not asked for again: the panel goes on without a tray
        _delivered = _installed ? (_state, _tooltip) : null;
        return _installed;
    }

    public bool Update(TrayState state, string tooltip)
    {
        (_state, _tooltip) = (state, Cut(tooltip));
        return Deliver();
    }

    /// <summary>
    /// Tells Windows the state and words it does not have yet. What is wanted and what Windows took are kept apart:
    /// a change it refused is not taken for shown, and is tried again at the next change or message from the icon.
    /// </summary>
    private bool Deliver()
    {
        if (!_installed || _delivered == (_state, _tooltip)) return true;
        if (!_shell.Modify(_state, _tooltip)) return false;
        _delivered = (_state, _tooltip);
        return true;
    }

    public bool Notify(string title, string text) => _installed && _shell.Notify(title, text);

    /// <summary>What a message means: the icon's own messages by the event in their low word, and the taskbar's restart.</summary>
    public static TrayEvent Decode(int message, IntPtr lParam, int callbackMessage, int taskbarCreatedMessage)
    {
        if (taskbarCreatedMessage != 0 && message == taskbarCreatedMessage) return TrayEvent.TaskbarCreated;
        if (message != callbackMessage) return TrayEvent.None;
        return (unchecked((int)lParam.ToInt64()) & 0xFFFF) switch
        {
            // A click on a notification asks for the panel too: it is the way from the alert to what it is about.
            NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONDBLCLK or NIN_BALLOONUSERCLICK => TrayEvent.Open,
            WM_CONTEXTMENU => TrayEvent.Menu,
            NIN_BALLOONSHOW => TrayEvent.NotificationShown,
            _ => TrayEvent.None,
        };
    }

    private void OnMessage(int message, IntPtr lParam)
    {
        Deliver(); // the icon is alive and talking: a change Windows refused earlier is tried again
        switch (Decode(message, lParam, _shell.CallbackMessage, _shell.TaskbarCreatedMessage))
        {
            case TrayEvent.Open:
                Opened?.Invoke();
                break;
            case TrayEvent.Menu:
                ShowMenu();
                break;
            case TrayEvent.NotificationShown:
                NotificationShown?.Invoke();
                break;
            case TrayEvent.TaskbarCreated:
                // The taskbar that held the icon is gone, and the icon with it: it is put back as it stood. If it
                // can't be, the panel is asked for, so it is never left running where nobody can reach it; the next
                // restart of the taskbar tries again.
                if (!_wanted) break;
                _installed = _shell.Add(_state, _tooltip);
                _delivered = _installed ? (_state, _tooltip) : null;
                if (!_installed) Opened?.Invoke();
                break;
        }
    }

    private void ShowMenu()
    {
        MenuOpening?.Invoke();
        if (Menu?.Invoke() is not { Count: > 0 } items) return;
        _open = items;
        int? chosen;
        try
        {
            // The menu's commands are numbered from 1: Windows gives 0 for a menu that was dismissed.
            chosen = _shell.TrackMenu([.. items.Select((item, index) => (index + 1, item.Text))]);
            items = _open;
        }
        finally
        {
            _open = null;
        }
        if (chosen is >= 1 && chosen <= items.Count) _ = items[chosen.Value - 1].Run();
        // Cancelled (Escape, a click away): the focus goes back to the notification area, where the keyboard left it.
        else _shell.ReturnFocus();
    }

    /// <summary>
    /// The menu's words changed while it may be open (the Queue line's read ended): each line of the open menu whose
    /// words differ is changed in place, its id kept, so one opening moves from the last words to the new ones.
    /// </summary>
    public void MenuChanged()
    {
        if (_open is not { } shown || Menu?.Invoke() is not { } now || now.Count != shown.Count) return;
        for (var i = 0; i < now.Count; i++)
            if (now[i].Id == shown[i].Id && now[i].Text != shown[i].Text) _shell.ChangeMenuItem(i + 1, now[i].Text);
        _open = now;
    }

    /// <summary>A tooltip no longer than Windows keeps, cut at a word with an ellipsis when it is.</summary>
    public static string Cut(string tooltip)
    {
        tooltip = (tooltip ?? "").Trim();
        if (tooltip.Length <= MaxTooltip) return tooltip;
        var keep = tooltip[..(MaxTooltip - 1)];
        var space = keep.LastIndexOf(' ');
        return (space > MaxTooltip / 2 ? keep[..space] : keep).TrimEnd(' ', ',', ':') + "…";
    }

    public void Dispose()
    {
        _shell.Message -= OnMessage;
        _wanted = false;
        if (_installed)
        {
            _installed = false;
            _shell.Delete();
        }
        _shell.Dispose();
    }
}

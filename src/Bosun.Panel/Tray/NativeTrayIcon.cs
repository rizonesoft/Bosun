namespace Bosun.Panel.Tray;

/// <summary>
/// The panel's icon in the notification area, as the rest of the panel sees it (D02 T02 §7). The real one is
/// <see cref="NativeTrayIcon"/>; a test stands in its own.
/// </summary>
public interface ITrayIcon : INotificationSink, IDisposable
{
    /// <summary>Puts the icon in the tray. False when Windows refuses it: there is then no tray.</summary>
    bool Install(TrayState state, string tooltip);

    /// <summary>Changes the icon and its tooltip.</summary>
    void Update(TrayState state, string tooltip);

    /// <summary>Raised when the person asks for the panel: a click, a double click, or the keyboard on the icon.</summary>
    event Action? Opened;

    /// <summary>Gives the menu as it reads now; asked each time the menu is opened. The chosen line is run.</summary>
    Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }
}

/// <summary>What a message from the tray icon means.</summary>
public enum TrayEvent
{
    None,

    /// <summary>The person asked for the panel.</summary>
    Open,

    /// <summary>The person asked for the menu: a right click, or the menu key.</summary>
    Menu,

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
    public const int WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;

    private readonly ITrayShell _shell;
    private bool _installed, _wanted;
    private TrayState _state;
    private string _tooltip = "";

    public NativeTrayIcon(ITrayShell shell)
    {
        _shell = shell;
        _shell.Message += OnMessage;
    }

    public event Action? Opened;

    public Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }

    /// <summary>Whether the icon is in the tray now.</summary>
    public bool IsInstalled => _installed;

    public bool Install(TrayState state, string tooltip)
    {
        (_state, _tooltip) = (state, Cut(tooltip));
        _installed = _shell.Add(_state, _tooltip);
        _wanted = _installed; // a refused icon is not asked for again: the panel goes on without a tray
        return _installed;
    }

    public void Update(TrayState state, string tooltip)
    {
        var cut = Cut(tooltip);
        if (state == _state && cut == _tooltip) return;
        (_state, _tooltip) = (state, cut);
        if (_installed) _shell.Modify(_state, _tooltip);
    }

    public bool Notify(string title, string text) => _installed && _shell.Notify(title, text);

    /// <summary>What a message means: the icon's own messages by the event in their low word, and the taskbar's restart.</summary>
    public static TrayEvent Decode(int message, IntPtr lParam, int callbackMessage, int taskbarCreatedMessage)
    {
        if (taskbarCreatedMessage != 0 && message == taskbarCreatedMessage) return TrayEvent.TaskbarCreated;
        if (message != callbackMessage) return TrayEvent.None;
        return (unchecked((int)lParam.ToInt64()) & 0xFFFF) switch
        {
            NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONDBLCLK => TrayEvent.Open,
            WM_CONTEXTMENU => TrayEvent.Menu,
            _ => TrayEvent.None,
        };
    }

    private void OnMessage(int message, IntPtr lParam)
    {
        switch (Decode(message, lParam, _shell.CallbackMessage, _shell.TaskbarCreatedMessage))
        {
            case TrayEvent.Open:
                Opened?.Invoke();
                break;
            case TrayEvent.Menu:
                ShowMenu();
                break;
            case TrayEvent.TaskbarCreated:
                // The taskbar that held the icon is gone, and the icon with it: it is put back as it stood. If it
                // can't be, the panel is asked for, so it is never left running where nobody can reach it; the next
                // restart of the taskbar tries again.
                if (!_wanted) break;
                _installed = _shell.Add(_state, _tooltip);
                if (!_installed) Opened?.Invoke();
                break;
        }
    }

    private void ShowMenu()
    {
        if (Menu?.Invoke() is not { Count: > 0 } items) return;
        // The menu's commands are numbered from 1: Windows gives 0 for a menu that was dismissed.
        var chosen = _shell.TrackMenu([.. items.Select((item, index) => (index + 1, item.Text))]);
        if (chosen is >= 1 && chosen <= items.Count) _ = items[chosen.Value - 1].Run();
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

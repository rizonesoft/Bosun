using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Settings;
using Bosun.Panel.Tests.Shell;
using Bosun.Panel.Tray;

namespace Bosun.Panel.Tests.Tray;

/// <summary>A tray icon of the tests' own: it writes down what it was asked, and can refuse. No test puts an icon in the real tray.</summary>
public sealed class FakeTrayIcon : ITrayIcon
{
    /// <summary>Windows refuses the icon.</summary>
    public bool Refuse { get; set; }

    /// <summary>Windows refuses to show a notification.</summary>
    public bool FailNotify { get; set; }

    /// <summary>Windows says each notification it accepts is on screen. False: accepted, and never shown.</summary>
    public bool ConfirmsDisplay { get; set; } = true;

    /// <summary>Windows refuses a change of the icon.</summary>
    public bool RefuseUpdate { get; set; }

    public bool Installed { get; private set; }

    public bool IsInstalled => Installed;

    public TrayState State { get; private set; }

    public string Tooltip { get; private set; } = "";

    public int Updates { get; private set; }

    public int Disposed { get; private set; }

    public List<(string Title, string Text)> Notifications { get; } = [];

    public event Action? Opened;

    public event Action? NotificationShown;

    public Func<IReadOnlyList<TrayMenuItem>>? Menu { get; set; }

    public bool Install(TrayState state, string tooltip)
    {
        if (Refuse) return false;
        (Installed, State, Tooltip) = (true, state, tooltip);
        return true;
    }

    public bool Update(TrayState state, string tooltip)
    {
        Updates++;
        if (RefuseUpdate) return false;
        (State, Tooltip) = (state, tooltip);
        return true;
    }

    public bool Notify(string title, string text)
    {
        if (FailNotify || !Installed) return false;
        Notifications.Add((title, text));
        if (ConfirmsDisplay) NotificationShown?.Invoke();
        return true;
    }

    /// <summary>The person clicks the icon.</summary>
    public void Click() => Opened?.Invoke();

    /// <summary>The person opens the menu and chooses a line by its id.</summary>
    public Task Choose(string id) => Menu!().Single(item => item.Id == id).Run();

    public void Dispose()
    {
        Disposed++;
        Installed = false;
    }
}

/// <summary>Windows' side of a tray icon, as a test says it is: what was added, changed, shown and removed, and which menu line is chosen.</summary>
public sealed class FakeTrayShell : ITrayShell
{
    public const int Callback = 0x8001, TaskbarCreated = 0xC123;

    public bool RefuseAdd { get; set; }

    public bool RefuseNotify { get; set; }

    public bool RefuseModify { get; set; }

    /// <summary>The line the person chooses when a menu is shown; null dismisses it.</summary>
    public int? Chooses { get; set; }

    public bool Installed { get; private set; }

    public TrayState State { get; private set; }

    public string Tooltip { get; private set; } = "";

    public List<string> Calls { get; } = [];

    public List<(string Title, string Text)> Notifications { get; } = [];

    public List<IReadOnlyList<(int Id, string Text)>> Menus { get; } = [];

    public int CallbackMessage => Callback;

    public int TaskbarCreatedMessage => TaskbarCreated;

    public event Action<int, IntPtr>? Message;

    public bool Add(TrayState state, string tooltip)
    {
        Calls.Add("add");
        if (RefuseAdd) return false;
        (Installed, State, Tooltip) = (true, state, tooltip);
        return true;
    }

    public bool Modify(TrayState state, string tooltip)
    {
        Calls.Add("modify");
        if (RefuseModify) return false;
        (State, Tooltip) = (state, tooltip);
        return true;
    }

    public bool Notify(string title, string text)
    {
        Calls.Add("notify");
        if (RefuseNotify) return false;
        Notifications.Add((title, text));
        return true;
    }

    public void Delete()
    {
        Calls.Add("delete");
        Installed = false;
    }

    public void ReturnFocus() => Calls.Add("focus");

    public int? TrackMenu(IReadOnlyList<(int Id, string Text)> items)
    {
        Calls.Add("menu");
        Menus.Add(items);
        return Chooses;
    }

    /// <summary>Windows sends the icon's window a message.</summary>
    public void Send(int message, int lParam) => Message?.Invoke(message, new IntPtr(lParam));

    /// <summary>Windows' taskbar started again: the icons it held are gone.</summary>
    public void RestartTaskbar()
    {
        Installed = false;
        Send(TaskbarCreated, 0);
    }

    public void Dispose() => Calls.Add("dispose");
}

/// <summary>What needs the operator, as a test says.</summary>
public sealed class FakeNeeds : INeedsYouSource
{
    private int _count;

    public int Count
    {
        get => _count;
        set
        {
            _count = value;
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}

public sealed class FakeQueue : IQueueSummary
{
    public string? Words { get; set; }

    /// <summary>How many times the menu said it was opening.</summary>
    public int Openings { get; private set; }

    public void MenuOpening() => Openings++;
}

/// <summary>
/// The tray over a shell of three test pages (Overview, Queue, Settings), a window and an icon of the tests' own,
/// settings that are only memory, and a clock the test moves.
/// </summary>
public sealed class TrayFixture
{
    public MemoryFiles Files { get; }
    public PanelSettingsStore Store { get; }
    public ActivityLog Log { get; } = Shells.Log();
    public TestPage Overview { get; } = new TestPage("Overview", "At a glance.").Data();
    public TestPage Queue { get; } = new TestPage("Queue", "Who holds the build slots.").Data();
    public TestPage Settings { get; } = new TestPage("Settings", "How the panel starts.").Data();
    public ShellViewModel Shell { get; }
    public FakeWindow Window { get; } = new();
    public FakeTrayIcon Icon { get; } = new();
    public FakeNeeds Needs { get; } = new();
    public FakeQueue QueueWords { get; } = new();
    public Notifier Notifier { get; }
    public TrayController Controller { get; }
    public List<int> Exits { get; } = [];
    public DateTime Now { get; set; } = new(2026, 11, 15, 9, 10, 0, DateTimeKind.Local);

    /// <param name="files">The settings of an earlier run, to start a new one over them.</param>
    /// <param name="icon">The icon to use in place of the fixture's own: the real class over a tray shell of the tests' own.</param>
    /// <param name="refreshes">The panel's refresh scheduler, which Exit stops (D02 T02 §8).</param>
    public TrayFixture(MemoryFiles? files = null, ITrayIcon? icon = null, RefreshScheduler? refreshes = null)
    {
        Files = files ?? new MemoryFiles();
        Store = new PanelSettingsStore(SettingsFixture.FilePath, Files);
        Shell = new ShellViewModel(
        [
            new Section("overview", "Overview", Sections.Monitor, Overview),
            new Section("queue", "Queue", Sections.Monitor, Queue),
            new Section("settings", "Settings", Sections.App, Settings),
        ], Log, Store);
        Notifier = new Notifier(Store, Log, () => Now);
        Controller = new TrayController(Shell, icon ?? Icon, Notifier, Store, Exits.Add, Needs, QueueWords, refreshes);
    }

    public TrayFixture Start(bool inTray = false, bool activate = true)
    {
        Controller.Start(Window, inTray, activate);
        return this;
    }

    /// <summary>How the person answers the question about unsaved edits.</summary>
    public void Answer(LeaveChoice choice) => Shell.AskToLeave = _ => Task.FromResult(choice);

    public Section Section(string key) => Shell.Sections.Single(section => section.Key == key);

    /// <summary>The Activity pane's lines of a severity, once the dispatcher has run.</summary>
    public IReadOnlyList<string> Lines(LogSeverity severity)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        return [.. Log.Entries.Where(entry => entry.Severity == severity).Select(entry => entry.Message)];
    }
}

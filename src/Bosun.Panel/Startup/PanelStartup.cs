using System.Windows.Threading;

namespace Bosun.Panel.Startup;

/// <summary>
/// What <see cref="PanelStartup"/> needs from the application it starts: the real window, message boxes, process
/// start and shutdown in the panel, and recorders in the tests. It makes no window and shows no message of its own.
/// </summary>
public sealed record StartupHost
{
    /// <summary>The UI thread's dispatcher: where an activation is taken and where a crash is caught.</summary>
    public required Dispatcher Dispatcher { get; init; }

    /// <summary>Makes and shows the first window. Called once, and only for the first instance.</summary>
    public required Action MakeWindow { get; init; }

    /// <summary>Brings the window forward when a later launch asks: restore, show, activate.</summary>
    public required Action BringForward { get; init; }

    /// <summary>Tells the person something, with nothing to choose.</summary>
    public required Action<string> Tell { get; init; }

    /// <summary>Asks the person a yes or no question; true for yes.</summary>
    public required Func<string, bool> Ask { get; init; }

    /// <summary>Starts the panel again as a new process; throws when it can't.</summary>
    public required Action Restart { get; init; }

    /// <summary>Ends the application with an exit code.</summary>
    public required Action<int> Exit { get; init; }

    /// <summary>The value of <see cref="InstanceName.Variable"/>; null when it is not set.</summary>
    public string? InstanceVariable { get; init; }

    /// <summary>The panel's data folder (<see cref="PanelPaths.Data()"/>).</summary>
    public required string DataFolder { get; init; }

    /// <summary>How long a later launch waits for the first to acknowledge, per try.</summary>
    public TimeSpan AnswerWithin { get; init; } = SingleInstance.DefaultAnswerWithin;
}

/// <summary>
/// Everything the panel does at its start and at its end (D02 T02 §1), in one place the tests drive as the
/// application does: the crash handlers are attached when it is made, <see cref="Begin"/> decides whether this launch
/// is the panel or a request to the one already running, and disposing it gives the instance up.
///
/// Exit codes: 0 when the launch was handed to the running panel, 1 after a crash on the UI thread, 2 when
/// <c>BOSUN_PANEL_INSTANCE</c> can't be a name, 3 when a panel is running but does not answer.
/// </summary>
public sealed class PanelStartup : IDisposable
{
    public const int ExitForwarded = 0, ExitCrashed = 1, ExitBadInstanceName = 2, ExitNotAnswering = 3;

    private readonly StartupHost _host;
    private readonly CrashHandlers _handlers;
    private SingleInstance? _instance;

    public PanelStartup(StartupHost host)
    {
        _host = host;
        // First, before anything else can throw.
        _handlers = CrashHandlers.Attach(host.Dispatcher, PanelPaths.Crash(host.DataFolder), OnUiCrash, host.Exit);
    }

    /// <summary>What this launch turned out to be; null before <see cref="Begin"/>, or when the instance name was refused.</summary>
    public InstanceRole? Role => _instance?.Role;

    /// <summary>
    /// Starts the panel. True when this is the first instance and its window was made; false when the launch ended
    /// some other way, with the host's exit already asked for.
    /// </summary>
    public bool Begin()
    {
        if (!InstanceName.TryResolve(_host.InstanceVariable, out var name, out var refusal))
        {
            _host.Tell(refusal);
            _host.Exit(ExitBadInstanceName);
            return false;
        }

        _instance = SingleInstance.Start(name, _host.AnswerWithin);
        switch (_instance.Role)
        {
            case InstanceRole.Forwarded:
                _host.Exit(ExitForwarded);
                return false;
            case InstanceRole.NotAnswering:
                _host.Tell("Bosun Panel is already running, but it is not answering.\n\nEnd Bosun.Panel.exe in Task Manager, then start Bosun Panel again.");
                _host.Exit(ExitNotAnswering);
                return false;
        }

        // Taken on the UI thread: the later launch is told it was heard only once the window has been brought forward.
        _instance.Activated = () => _host.Dispatcher.InvokeAsync(_host.BringForward).Task;
        _host.MakeWindow();
        return true;
    }

    /// <summary>The words of the crash notice: the report's path, or that none could be saved and why, and the question.</summary>
    public static string CrashNotice(CrashReport report) =>
        "Bosun Panel ran into a problem and has to close.\n\n"
        + (report.Saved ? $"A report was saved to:\n{report.Path}" : $"No report could be saved: {report.Failure}")
        + "\n\nStart Bosun Panel again?";

    /// <summary>
    /// After a crash on the UI thread: the person chooses whether the panel starts again. The instance is given up
    /// before the new process is started, so that process is the first and not a request to this one. The exit
    /// itself is asked for by the handlers, whatever happens here.
    /// </summary>
    private void OnUiCrash(CrashReport report)
    {
        if (!_host.Ask(CrashNotice(report))) return;
        ReleaseInstance();
        try
        {
            _host.Restart();
        }
        // Whatever stops the new process from starting: this is already the crash path, so it is told once, nothing
        // is retried, no second report is written, and the panel still closes.
        catch (Exception e)
        {
            _host.Tell($"Bosun Panel could not be started again: {e.Message}");
        }
    }

    private void ReleaseInstance() => Interlocked.Exchange(ref _instance, null)?.Dispose();

    public void Dispose()
    {
        ReleaseInstance();
        _handlers.Dispose();
    }
}

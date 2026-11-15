using System.Windows.Threading;

namespace Bosun.Panel.Startup;

/// <summary>
/// The three places an exception nobody caught can surface (D02 T02 §1), each writing a crash report:
///
/// - On the UI thread (<see cref="Dispatcher.UnhandledException"/>): the panel's state is unknown and it can change
///   the machine, so it does not carry on. The report is written, the person is told through <c>notify</c>, and
///   <c>exit</c> is asked for code 1.
/// - In a task nobody awaited (<see cref="TaskScheduler.UnobservedTaskException"/>): reported and observed; the panel
///   carries on, since nothing on screen depends on a task nobody awaited.
/// - On any other thread (<see cref="AppDomain.UnhandledException"/>): .NET ends the process; the report is written
///   first.
/// </summary>
public sealed class CrashHandlers : IDisposable
{
    public const string UiThread = "UI thread", OtherThread = "Thread", UnobservedTask = "Task";

    private readonly Dispatcher _dispatcher;
    private readonly string _folder;
    private readonly Action<CrashReport> _notify;
    private readonly Action<int> _exit;
    private bool _closing;
    private bool _attached;

    private CrashHandlers(Dispatcher dispatcher, string folder, Action<CrashReport> notify, Action<int> exit)
    {
        _dispatcher = dispatcher;
        _folder = folder;
        _notify = notify;
        _exit = exit;
    }

    /// <summary>Starts handling; <see cref="Detach"/> (or disposing) stops it.</summary>
    public static CrashHandlers Attach(Dispatcher dispatcher, string folder, Action<CrashReport> notify, Action<int> exit)
    {
        var handlers = new CrashHandlers(dispatcher, folder, notify, exit);
        dispatcher.UnhandledException += handlers.OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += handlers.OnDomainException;
        TaskScheduler.UnobservedTaskException += handlers.OnTaskException;
        handlers._attached = true;
        return handlers;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _dispatcher.UnhandledException -= OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainException;
        TaskScheduler.UnobservedTaskException -= OnTaskException;
    }

    public void Dispose() => Detach();

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var report = CrashReports.Write(_folder, e.Exception, UiThread);
        // The notice is a dialog, which runs the message loop: a second exception while it is up is reported, and
        // nothing more, or the person would get a notice per exception and an exit per notice.
        if (_closing) return;
        _closing = true;
        _notify(report);
        _exit(1);
    }

    /// <summary>
    /// The handler for an exception on a thread that is not the UI thread. Public so a test can raise it: the real
    /// event only fires as the process dies.
    /// </summary>
    public void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new InvalidOperationException($"a non-exception object was thrown: {e.ExceptionObject}");
        CrashReports.Write(_folder, exception, OtherThread);
    }

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashReports.Write(_folder, e.Exception, UnobservedTask);
        e.SetObserved();
    }
}

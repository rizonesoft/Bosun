using System.Diagnostics;
using System.Windows;
using Bosun.Panel.Shell;
using Bosun.Panel.Startup;

namespace Bosun.Panel;

/// <summary>
/// The application hands <see cref="PanelStartup"/> the real things (its window, message boxes, a process start and
/// its own shutdown) and does nothing else at its start or end (D02 T02 §1).
/// </summary>
public partial class App : Application
{
    private const string Title = "Bosun Panel";
    private readonly PanelStartup _startup;
    private IWindowSurface? _window;

    public App()
    {
        // Made here, in the constructor, so the crash handlers are attached before anything else can throw.
        _startup = new PanelStartup(new StartupHost
        {
            Dispatcher = Dispatcher,
            DataVariable = Environment.GetEnvironmentVariable(PanelPaths.Variable),
            InstanceVariable = Environment.GetEnvironmentVariable(InstanceName.Variable),
            MakeWindow = MakeWindow,
            BringForward = BringForward,
            Tell = text => MessageBox.Show(text, Title, MessageBoxButton.OK, MessageBoxImage.Warning),
            Ask = text => MessageBox.Show(text, Title, MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.Yes) == MessageBoxResult.Yes,
            Restart = Restart,
            Exit = Shutdown,
        });
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _startup.Begin();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startup.Dispose();
        base.OnExit(e);
    }

    /// <summary>The first window: the shell in its window, shown (D02 T02 §4).</summary>
    private void MakeWindow() =>
        _window = WindowStart.Open(WindowStart.NewShell(Dispatcher), shell =>
        {
            var window = new MainWindow(shell);
            MainWindow = window;
            return window;
        });

    /// <summary>A later launch asked for the panel: its window is restored if minimised, shown if hidden, and activated.</summary>
    private void BringForward()
    {
        if (_window is not null) WindowActivation.BringForward(_window);
    }

    private static void Restart()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("the panel's own path is not known");
        using var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
    }
}

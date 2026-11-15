using System.Diagnostics;
using System.Windows;
using Bosun.Core;
using Bosun.Panel.Settings;
using Bosun.Panel.Shell;
using Bosun.Panel.Startup;
using Bosun.Panel.Theme;

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
    private WindowsTheme? _windowsTheme;

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
        _windowsTheme?.Dispose();
        _startup.Dispose();
        base.OnExit(e);
    }

    /// <summary>The first window: the shell in its window, shown (D02 T02 §4).</summary>
    private void MakeWindow()
    {
        var settings = new PanelSettingsStore(System.IO.Path.Combine(_startup.DataFolder, "panel.json"));

        // The stored theme first, so the window is never drawn in one theme and then another (D02 T02 §5).
        _windowsTheme = new WindowsTheme();
        var theme = new ThemeService(Resources.MergedDictionaries, _windowsTheme, Dispatcher);
        theme.Apply(settings.Current.Theme);
        settings.Changed += saved => theme.Apply(saved.Theme);
        _windowsTheme.Listen();

        var startup = new StartupEntry(new RegistryRunKey(), Environment.ProcessPath ?? "Bosun.Panel.exe");
        _window = WindowStart.Open(WindowStart.NewShell(Dispatcher, settings, startup, OpenStartupSettings), shell =>
        {
            var window = new MainWindow(shell);
            MainWindow = window;
            return window;
        });
    }

    /// <summary>Windows' own start-up settings: where an entry Windows has turned off is turned back on.</summary>
    private static void OpenStartupSettings()
    {
        using var opened = Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
    }

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

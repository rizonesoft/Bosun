using System.Diagnostics;
using System.Windows;
using Bosun.Core;
using Bosun.Panel.Settings;
using Bosun.Panel.Shell;
using Bosun.Panel.Startup;
using Bosun.Panel.Theme;
using Bosun.Panel.Tray;

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
    private IHighContrast? _contrast;
    private Backdrop? _backdrop;
    private TrayController? _tray;
    private RefreshScheduler? _refreshes;
    private Motion? _motion;

    public App()
    {
        // The panel lives in the tray (D02 T02 §7): closing its window does not end it. It ends when it is told to,
        // by the tray's Exit, by a window that really closed, or by startup.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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
        _refreshes?.Dispose(); // however the panel ends, nothing goes on reading a tool
        _motion?.Dispose();    // and nothing goes on listening for Windows' animation setting
        _tray?.Dispose();      // and no icon is left in the tray
        _windowsTheme?.Dispose();
        (_contrast as IDisposable)?.Dispose();
        _backdrop?.Dispose();
        _startup.Dispose();
        base.OnExit(e);
    }

    /// <summary>The first window: the shell in its window, shown, or left hidden when started for the tray (D02 T02 §4, §7).</summary>
    private void MakeWindow()
    {
        var settings = new PanelSettingsStore(System.IO.Path.Combine(_startup.DataFolder, "panel.json"));

        // The stored theme first, so the window is never drawn in one theme and then another (D02 T02 §5).
        _windowsTheme = new WindowsTheme();
        // Windows' contrast themes win over the stored choice while one is on (D02 T03 §10); a preview named by
        // BOSUN_PANEL_CONTRAST stands in for them, always on, and a file it can't read leaves Windows' own (D02 T03 §11).
        var (contrast, previewLine, previewFailed) = PreviewContrast.Choose(Environment.GetEnvironmentVariable(PreviewContrast.Variable), () => new WindowsHighContrast());
        _contrast = contrast;
        var theme = new ThemeService(Resources.MergedDictionaries, _windowsTheme, Dispatcher, contrast: _contrast);
        theme.Apply(settings.Current.Theme);
        settings.Changed += saved => theme.Apply(saved.Theme);
        _windowsTheme.Listen();
        (_contrast as WindowsHighContrast)?.Listen();
        // The one motion gate (D02 T03 §1, §7), over the theme just applied: every animation takes its length from it,
        // and none runs while Windows' animation effects are off.
        _motion = Motion.Install(Resources, new WindowsAnimation());

        var startup = new StartupEntry(new RegistryRunKey(), Environment.ProcessPath ?? "Bosun.Panel.exe");
        // Started for the tray, by Windows at sign-in: no window, and so no page on screen, until it is asked for.
        var inTray = TrayController.StartsInTray(Environment.GetCommandLineArgs());
        // The one owner of every refresh (D02 T02 §8), ticked on this thread, and only while something is active.
        var refreshes = _refreshes = new RefreshScheduler(driver: new DispatcherRefreshDriver(Dispatcher));
        // One reader of the memory documents, for the Memory page and the tray's pressure check (D03 T01 §2, §4).
        var memory = Bosun.Core.Memory.MemoryClient.ForThisUser();
        var made = WindowStart.NewShell(Dispatcher, settings, startup, OpenStartupSettings, windowShown: !inTray, theme: theme,
            refreshes: refreshes, memory: memory, instances: Bosun.Core.Instances.InstanceClient.ForThisUser(), queue: Bosun.Core.Queue.QueueClient.ForThisUser());
        // The preview said where the person will look: in force from which file, or why its file was not used.
        if (previewLine is not null)
        {
            if (previewFailed) made.Log.Warn(previewLine);
            else made.Log.Info(previewLine);
        }
        // The tray's Queue line shares the Queue page's readings, and reads once when its menu opens (D03 T02 §3).
        var queueWords = made.Sections.Select(section => section.Page).OfType<Bosun.Panel.Queue.QueuePage>().FirstOrDefault()?.Words;
        var tray = _tray = new TrayController(made, new NativeTrayIcon(new Win32TrayShell()), new Notifier(settings, made.Log), settings, Shutdown, queue: queueWords, refreshes: refreshes);
        // One read as the panel starts, so the menu's first opening has the queue's words, not only "Checking".
        queueWords?.ReadOnce();
        var activates = LaunchShow.FirstWindowActivates();
        _window = WindowStart.Open(made, shell =>
        {
            // Shown as the program that started the panel asked: a start without activation leaves its window in front.
            var window = new MainWindow(shell) { TakeClose = tray.TakeClose, ShowActivated = activates };
            // A window that really closed (there is no tray to hide in) ends the panel.
            window.Closed += (_, _) => Shutdown();
            MainWindow = window;
            // The templates' contrast mode follows the theme, on the window and so on everything in it.
            HighContrastMode.SetIsOn(window, theme.IsHighContrast);
            theme.EffectiveChanged += () => HighContrastMode.SetIsOn(window, theme.IsHighContrast);
            // Mica under the chrome where Windows has it and the person allows it, else solid (D02 T03 §2): attached as
            // the handle is made, so a window started in the tray has it too, and let go when the window closes.
            _backdrop = Backdrop.Attach(window, window.View, theme, _contrast, made.Log);
            // Where it was last, on that screen, before it is first shown (D02 T03 §12), and kept each time it is hidden.
            WindowPlacement.Attach(window, settings, made.Log);
            return window;
        }, window =>
        {
            tray.Start(window, inTray, activates);
            // After the tray, so a pressure already present at launch has somewhere to be said (D03 T01 §4).
            tray.StartPressureCheck(token => memory.SnapshotAsync("windows", token));
        });
    }

    /// <summary>Windows' own start-up settings: where an entry Windows has turned off is turned back on.</summary>
    private static void OpenStartupSettings()
    {
        using var opened = Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
    }

    /// <summary>A later launch asked for the panel: its window is restored if minimised, shown if hidden, and activated.</summary>
    private void BringForward() => _tray?.Open();

    private static void Restart()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("the panel's own path is not known");
        using var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
    }
}

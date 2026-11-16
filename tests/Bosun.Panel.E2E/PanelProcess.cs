using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Bosun.Core;

namespace Bosun.Panel.E2E;

/// <summary>
/// One real panel for one test: <c>Bosun.Panel.exe</c> started in a Windows job object of its own (created suspended
/// and named in the job before its first instruction, by <see cref="ChildProcess"/>), with a fresh data folder and an
/// instance name of its own, so it never meets the operator's panel, settings or crash reports. Disposing it ends the
/// panel and everything it started, takes its icon out of the notification area (a process that is ended can't), and
/// removes its folder.
/// </summary>
sealed class PanelProcess : IDisposable
{
    private readonly ChildProcess _child;
    private readonly FirstShown? _shown;
    private bool _disposed;

    private PanelProcess(ChildProcess child, string data, string instance, string program, FirstShown? shown)
    {
        (_child, DataFolder, Instance, Program, _shown) = (child, data, instance, program, shown);
        if (shown is not null) shown.Process = child.Id;
    }

    /// <summary>The panel the build made, beside this test's own output (build/dotnet/bin/&lt;project&gt;/&lt;config&gt;).</summary>
    public static string PanelExe => Sibling("Bosun.Panel", "Bosun.Panel.exe");

    /// <summary>The test child the process runner's tests use, which can start a grandchild (<c>tree</c>).</summary>
    public static string TestChildExe => Sibling("Bosun.TestChild", "Bosun.TestChild.exe");

    private static string Sibling(string project, string file)
    {
        var config = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var path = Path.Combine(config.Parent!.Parent!.FullName, project, config.Name, file);
        return File.Exists(path) ? path : throw new FileNotFoundException($"{file} is not built beside the smoke; build tests/Bosun.Panel.E2E", path);
    }

    /// <summary>Where every test's folders go: the scratch folder <c>BOSUN_SCRATCH</c> names, else the system's temp folder.</summary>
    public static string ScratchRoot()
    {
        var root = Environment.GetEnvironmentVariable("BOSUN_SCRATCH") is { Length: > 0 } scratch ? scratch : Path.GetTempPath();
        var folder = Path.Combine(root, "bosun-e2e");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string DataFolder { get; }

    public string Instance { get; }

    public string Program { get; }

    public int Id => _child.Id;

    public Task Exited => _child.Exited;

    public int ExitCode() => _child.ExitCode();

    /// <summary>A panel of its own, with these arguments (<c>--tray</c> starts it in the tray).</summary>
    public static PanelProcess Start(params string[] arguments) =>
        Launch(PanelExe, arguments, Path.Combine(ScratchRoot(), Guid.NewGuid().ToString("N")[..12]), "e2e-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>A panel of its own with more environment: a contrast preview's file, say (D02 T03 §11).</summary>
    public static PanelProcess StartWith(IReadOnlyDictionary<string, string?> environment, params string[] arguments) =>
        Launch(PanelExe, arguments, Path.Combine(ScratchRoot(), Guid.NewGuid().ToString("N")[..12]), "e2e-" + Guid.NewGuid().ToString("N")[..12], extra: environment);

    /// <summary>Another <c>Bosun.Panel.exe</c> as the same instance, with the same data folder: a second launch.</summary>
    public PanelProcess StartAgain(params string[] arguments) => Launch(PanelExe, arguments, DataFolder, Instance, ownsFolder: false);

    /// <summary>Any program, held the same way: proves the job ends what the program started.</summary>
    public static PanelProcess StartProgram(string program, params string[] arguments) =>
        Launch(program, arguments, Path.Combine(ScratchRoot(), Guid.NewGuid().ToString("N")[..12]), "e2e-" + Guid.NewGuid().ToString("N")[..12]);

    private bool _ownsFolder = true;

    private static PanelProcess Launch(string program, string[] arguments, string data, string instance, bool ownsFolder = true,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        Directory.CreateDirectory(data);
        // A local application data folder of the test's own, holding a fresh Windows snapshot with plenty of memory:
        // the panel's memory check (D03 T01 §4) reads that, never this machine's own logger, so the tray says "normal".
        var local = Path.Combine(data, "local");
        Directory.CreateDirectory(Path.Combine(local, "Bosun", "logs"));
        File.WriteAllText(Path.Combine(local, "Bosun", "logs", "current-windows.json"),
            "{\"schema_version\":1,\"side\":\"windows\",\"at\":\"" + DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            + "\",\"interval_s\":30,\"memory\":{\"available_mb\":20000,\"total_mb\":32000},\"wsl_vm\":null,\"repos\":[],\"processes\":[],\"history_written\":true}");
        // The panel opens on the test screen, never first on the main one (D02 T03 §12): its window's place is the
        // screen's centre, unless the folder already holds settings (a second launch of the same panel).
        var panel = string.Equals(program, PanelExe, StringComparison.OrdinalIgnoreCase);
        var settings = Path.Combine(data, "panel.json");
        if (panel && !File.Exists(settings)) File.WriteAllText(settings, TestScreen.Settings(TestScreen.Current));
        var shown = panel ? new FirstShown() : null;
        var request = new ProcessRequest(program, arguments)
        {
            Environment = new Dictionary<string, string?>(extra ?? new Dictionary<string, string?>())
            {
                ["BOSUN_PANEL_DATA"] = data, ["BOSUN_PANEL_INSTANCE"] = instance, ["LOCALAPPDATA"] = local,
            },
            WorkingDirectory = data,
        };
        try
        {
            return new PanelProcess(ChildProcess.Start(request, new StartGate()), data, instance, program, shown) { _ownsFolder = ownsFolder };
        }
        catch
        {
            shown?.Dispose();
            throw;
        }
    }

    /// <summary>The panel's main window, once it has one (it has none while it is only in the tray).</summary>
    public AutomationElement Window(TimeSpan? limit = null)
    {
        var window = Ui.Until(() => Ui.TopLevel(Id, "Bosun Panel"), limit ?? TimeSpan.FromSeconds(20), "the panel's window to appear");
        RequireShownOnTestScreen();
        return window;
    }

    /// <summary>
    /// Where the window first appeared, which must be the test screen: a panel that opened on another screen (the main
    /// one, the owner's) and was then moved fails here, whatever the test it was started for.
    /// </summary>
    private void RequireShownOnTestScreen()
    {
        if (_shown is null) return;
        Ui.Until(() => _shown.Bounds is not null, TimeSpan.FromSeconds(5), "the panel's window to be seen as it was first shown");
        var b = _shown.Bounds!.Value;
        var screen = TestScreen.Current;
        if (!screen.Holds((b.Left + b.Right) / 2, (b.Top + b.Bottom) / 2))
            throw new InvalidOperationException(FormattableString.Invariant(
                $"the panel's window was first shown at ({b.Left}, {b.Top}) to ({b.Right}, {b.Bottom}), not on the test screen {screen}"));
    }

    /// <summary>The window's handle.</summary>
    public IntPtr Handle(TimeSpan? limit = null) => new(Window(limit).Current.NativeWindowHandle);

    /// <summary>The panel's window, shown and given the foreground through UI Automation (no key or click is sent).</summary>
    public AutomationElement Front()
    {
        var window = Window();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        // No window owns the foreground while the desktop is locked or a secure screen is up: no test can take it then,
        // and saying so beats a timeout that reads like a fault of the panel (found 2026-11-16, an idle machine locked).
        if (Native.GetForegroundWindow() == IntPtr.Zero && !Ui.Wait(() => Native.GetForegroundWindow() != IntPtr.Zero, TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException("no window owns the foreground: the desktop is locked or a secure screen is up, so the smoke can't run; unlock it and run again");
        Ui.Until(() =>
        {
            if (Native.GetForegroundWindow() == handle) return true;
            try
            {
                window.SetFocus();
            }
            catch (InvalidOperationException)
            {
                // Windows can refuse a focus change in the moment a window is being shown; asked again below.
            }
            return Native.GetForegroundWindow() == handle;
        }, TimeSpan.FromSeconds(10), "the panel's window to take the foreground");
        return window;
    }

    /// <summary>Whether the process, or anything its job holds, is still running.</summary>
    public bool Running => !_child.Exited.IsCompleted;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Running) RemoveTrayIcon();
        _child.Dispose(); // the job's last handle: the panel and everything it started end here
        _shown?.Dispose();
        if (_ownsFolder) RemoveFolder();
    }

    /// <summary>Why the test's folder could not be removed, or null when it was (or was never this panel's to remove).</summary>
    public string? CleanupFailure { get; private set; }

    /// <summary>
    /// Removes the test's folder, giving the ended process's handles two seconds to close. A folder that stays is
    /// said, with its path and why, as a warning on the test's result: a green run never hides scratch it left.
    /// </summary>
    private void RemoveFolder()
    {
        Exception? last = null;
        for (var tries = 0; tries < 20 && Directory.Exists(DataFolder); tries++)
        {
            try
            {
                Directory.Delete(DataFolder, recursive: true);
                last = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                last = e;
                Thread.Sleep(100); // the ended process's handles close a moment after it ends
            }
        }
        if (!Directory.Exists(DataFolder)) return;
        CleanupFailure = $"the test's folder {DataFolder} was left behind: {last?.Message ?? "it still exists"}";
        Xunit.TestContext.Current.AddWarning(CleanupFailure);
    }

    /// <summary>
    /// The notification area keeps the icon of a process that was ended until someone points at it, and a later test
    /// would see two. The panel's tray window and icon id are known (Win32TrayShell: a window titled "Bosun Panel
    /// tray", icon 1), so the icon is taken out here, as the panel's own exit would.
    /// </summary>
    private void RemoveTrayIcon()
    {
        foreach (var window in Native.WindowsOf(Id).Where(w => Native.TitleOf(w) == "Bosun Panel tray"))
        {
            var data = new NOTIFYICONDATAW { cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = window, uID = 1, szTip = "", szInfo = "", szInfoTitle = "" };
            Shell_NotifyIconW(NIM_DELETE, ref data);
        }
    }

    private const int NIM_DELETE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);
}

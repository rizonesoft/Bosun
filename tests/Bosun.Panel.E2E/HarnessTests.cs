using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>The smoke's own two promises: nothing it starts outlives a test, and no real input lands anywhere else.</summary>
public class HarnessTests
{
    // The real-input runner's watcher (D02 T02 §12) tells a person from the smoke: input the smoke sends is marked
    // injected, and leaves its file alone; input that is not (the watcher's own stand-in line) is a person, and moves it.
    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void The_input_watcher_passes_over_injected_input_and_sees_a_person()
    {
        SmokeInput.Require("HarnessTests.The_input_watcher_passes_over_injected_input_and_sees_a_person");
        var file = Path.Combine(Path.GetTempPath(), $"bosun-watch-{Guid.NewGuid():N}.txt");
        var info = new ProcessStartInfo(PanelProcess.TestChildExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("watch-input");
        info.ArgumentList.Add(file);
        using var watcher = Process.Start(info)!;
        try
        {
            Assert.Equal("watching", watcher.StandardOutput.ReadLine());
            Assert.Equal("none", File.ReadAllText(file).Trim());
            for (var i = 0; i < 5; i++)
            {
                Native.MoveInPlace();
                Thread.Sleep(100);
            }
            // The watcher writes at most once a second: past that, a move it counted would be in the file.
            Thread.Sleep(1500);
            Assert.Equal("none", File.ReadAllText(file).Trim());
            var before = DateTime.UtcNow;
            watcher.StandardInput.WriteLine("person");
            watcher.StandardInput.Flush();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            string said;
            while ((said = File.ReadAllText(file).Trim()) == "none" && DateTime.UtcNow < deadline) Thread.Sleep(100);
            Assert.True(DateTime.TryParse(said, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen), $"the watcher wrote '{said}'");
            Assert.True(seen.ToUniversalTime() >= before.AddSeconds(-1), $"a person at {seen:o}, before the stand-in at {before:o}");
        }
        finally
        {
            if (!watcher.HasExited) watcher.Kill(entireProcessTree: true);
            File.Delete(file);
        }
    }

    [Fact]
    public void Outside_the_real_input_run_every_way_of_taking_the_desktop_is_refused_and_nothing_is_sent()
    {
        // D02 T02 §12: a test without [Trait("Input", "Real")] can't send a key, click, move the pointer or bring a
        // panel forward, whatever the run, so the default smoke never takes the operator's mouse or keyboard.
        using var panel = PanelProcess.Start();
        var window = panel.Window();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var pointer = Native.Cursor();
        var refusals = new List<string>
        {
            Assert.Throws<RealInputRefused>(() => RealInput.Key(handle, Native.VK_TAB)).Message,
            Assert.Throws<RealInputRefused>(() => RealInput.ClickOnWindow(handle, 10, 10)).Message,
            Assert.Throws<RealInputRefused>(() => RealInput.MoveOnIcon(window)).Message,
            Assert.Throws<RealInputRefused>(() => panel.Front()).Message,
        };
        Assert.All(refusals, said => Assert.Contains("[Trait(\"Input\", \"Real\")]", said));
        Assert.Equal(pointer, Native.Cursor());                                              // the pointer never moved
    }

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void A_default_test_that_leaves_a_panel_it_started_in_front_is_failed()
    {
        // A second launch activates the first panel, taking the foreground with no RealInput call: the hook after each
        // default test catches it (D02 T02 §12). Proved here in the real-input run, as the activation takes the desktop.
        using var panel = PanelProcess.Start();
        var window = panel.Window();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        using (panel.StartAgain()) { }
        Ui.Until(() => Native.GetForegroundWindow() == handle, Ui.Short, "the second launch to bring the first panel forward");
        var opted = Environment.GetEnvironmentVariable(SmokeInput.Variable);
        try
        {
            Environment.SetEnvironmentVariable(SmokeInput.Variable, null);                    // as a default run
            var hook = new ForegroundKeptAttribute();
            var refused = Assert.Throws<RealInputRefused>(() => hook.After(typeof(HarnessTests).GetMethod(nameof(A_default_test_that_leaves_a_panel_it_started_in_front_is_failed))!, null!));
            Assert.Contains("in the foreground", refused.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SmokeInput.Variable, opted);
        }
    }

    [Fact]
    public void A_test_s_process_and_what_it_started_end_with_it()
    {
        var others = OtherPanels();
        string beat;
        int child, grandchild;
        using (var started = PanelProcess.StartProgram(PanelProcess.TestChildExe, "tree", "beat.txt", "60000"))
        {
            beat = Path.Combine(started.DataFolder, "beat.txt");
            child = started.Id;
            grandchild = Ui.Until(() => FirstLine(beat) is { } id ? (object)id : null, Ui.Short, "the grandchild to write its id") is int found ? found : 0;
            Assert.True(Alive(child) && Alive(grandchild), "the child and the grandchild run while the test holds them");
        }
        // The grandchild beats for ten seconds by itself; ended with the test, it is gone well before.
        Ui.Until(() => !Alive(child) && !Alive(grandchild), TimeSpan.FromSeconds(3), "the child and the grandchild to end with the test");
        Assert.All(others, id => Assert.True(Alive(id), $"the panel {id}, which the test did not start, still runs"));
    }

    [Fact]
    public void A_folder_that_can_t_be_removed_is_said_with_its_path_and_why()
    {
        var started = PanelProcess.StartProgram(PanelProcess.TestChildExe, "sleep", "30000");
        var locked = Path.Combine(started.DataFolder, "locked.txt");
        using (var held = new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            started.Dispose();
            Assert.NotNull(started.CleanupFailure);
            Assert.Contains(started.DataFolder, started.CleanupFailure);
            Assert.Contains("locked.txt", started.CleanupFailure); // Windows names the file it could not remove
        }
        Directory.Delete(started.DataFolder, recursive: true); // the test's own leftover, now that it is let go
    }

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void Input_is_refused_when_its_target_is_not_the_panel()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var (x, y) = Ui.Centre(window);

        var frame = Ui.Box(window);
        using var cover = Cover.Open(new Rect(frame.X + frame.Width / 4, frame.Y + frame.Height / 4, frame.Width / 2, frame.Height / 2));
        var keys = Assert.Throws<RealInputRefused>(() => RealInput.Key(handle, Native.VK_SPACE));
        Assert.Contains("foreground window is", keys.Message);
        Assert.Throws<RealInputRefused>(() => RealInput.ClickOnWindow(handle, x, y));
        Assert.Equal((0, 0), cover.Heard());

        // Not a Bosun Panel icon: the notification area's own button.
        var taskbar = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
        var other = Ui.Until(() => taskbar.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon")), Ui.Short, "a button of the notification area");
        Assert.Throws<RealInputRefused>(() => RealInput.RightClickIcon(other));

        // Not a menu of the panel: the test's own window.
        Assert.Throws<RealInputRefused>(() => RealInput.ClickMenuLine(panel.Id, cover.Element));
        Assert.Equal((0, 0), cover.Heard());
    }

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void Input_is_refused_while_a_modifier_is_held()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var (x, y) = Ui.Centre(Ui.Get(window, "Rail"));
        var focused = Ui.Focused();

        // Shift is held as a person would hold it (pressed on the panel, which owns the foreground): a Tab sent now
        // would be Shift+Tab, and with Alt or Windows it would leave the panel altogether.
        Assert.Equal(handle, Native.GetForegroundWindow());
        Native.KeyDown(Native.VK_SHIFT);
        try
        {
            var refused = Assert.Throws<RealInputRefused>(() => RealInput.Key(handle, Native.VK_TAB));
            Assert.Contains("Shift is held down", refused.Message);
            Assert.Throws<RealInputRefused>(() => RealInput.ClickOnWindow(handle, x, y));
        }
        finally
        {
            Native.KeyUp(Native.VK_SHIFT);
        }
        Assert.True(focused is null || Ui.Focused() is { } now && Automation.Compare(now, focused), "nothing reached the panel while Shift was held");
        RealInput.Key(handle, Native.VK_TAB); // let go, input goes again
    }

    private static int? FirstLine(string file)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            return int.TryParse(reader.ReadLine(), out var id) ? id : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool Alive(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static List<int> OtherPanels() => [.. Process.GetProcessesByName("Bosun.Panel").Select(p => { using (p) return p.Id; })];
}

/// <summary>
/// A window of the test's own, always on top, at a place in screen pixels, which counts the keys and clicks that reach
/// it. Given the foreground unless <c>activate</c> is false, so the panel can keep it while covered.
/// </summary>
sealed class Cover : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private int _keys, _clicks;

    private Cover(Dispatcher dispatcher, AutomationElement element) => (_dispatcher, Element) = (dispatcher, element);

    public AutomationElement Element { get; }

    public static Cover Open(Rect at, bool activate = true)
    {
        Cover? made = null;
        var ready = new ManualResetEventSlim();
        Window? window = null;
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            window = new Window
            {
                Title = "Bosun E2E cover", WindowStyle = WindowStyle.None, Topmost = true, ShowActivated = activate, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            window.SourceInitialized += (_, _) =>
            {
                // The window's place in screen pixels (SWP_NOACTIVATE).
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                SetWindowPos(handle, IntPtr.Zero, (int)at.X, (int)at.Y, (int)at.Width, (int)at.Height, 0x0010);
            };
            window.Show();
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(10)) || dispatcher is null)
        {
            dispatcher?.InvokeShutdown(); // a window shown late still closes
            throw new TimeoutException("the cover window was not shown within 10 s");
        }
        // From here the window exists: any way out but a return closes it, so a test that fails here leaves no
        // always-on-top window over the desktop.
        try
        {
            var element = Ui.Until(() => Ui.TopLevel(Environment.ProcessId, "Bosun E2E cover"), Ui.Short, "the cover window");
            made = new Cover(dispatcher, element);
            dispatcher.Invoke(() =>
            {
                window!.PreviewKeyDown += (_, _) => Interlocked.Increment(ref made._keys);
                window.PreviewMouseDown += (_, _) => Interlocked.Increment(ref made._clicks);
            });
            var handle = new IntPtr(element.Current.NativeWindowHandle);
            if (activate)
                Ui.Until(() =>
                {
                    element.SetFocus();
                    return Native.GetForegroundWindow() == handle;
                }, Ui.Short, "the cover window to take the foreground");
            return made;
        }
        catch
        {
            dispatcher.InvokeShutdown();
            throw;
        }
    }

    /// <summary>The keys and clicks that reached the window.</summary>
    public (int Keys, int Clicks) Heard()
    {
        _dispatcher.Invoke(() => { }, DispatcherPriority.Background); // whatever was sent has been handled
        return (Volatile.Read(ref _keys), Volatile.Read(ref _clicks));
    }

    public void Dispose() => _dispatcher.InvokeShutdown();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

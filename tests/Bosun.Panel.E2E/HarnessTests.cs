using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>The smoke's own two promises: nothing it starts outlives a test, and no real input lands anywhere else.</summary>
public class HarnessTests
{
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

    [Fact]
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

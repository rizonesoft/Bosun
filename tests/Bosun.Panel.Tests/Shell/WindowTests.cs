using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using Bosun.Panel.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The window around the shell (D02 T02 §4, design/SPEC.md §1). A test never shows a window, so the window is made
/// and read without being shown, its work is done on a window surface of the tests' own, and the snap layouts' hit
/// test is proved on a real window handle that never appears. The window on a real screen is D02 T02 §10's.
/// </summary>
public partial class WindowTests
{
    // ---- the window ----

    [Fact]
    public void The_window_is_at_least_960_by_600_with_a_44_px_caption_around_the_shell_view()
    {
        Sta.Run(() =>
        {
            var shell = Shells.Panel();
            var window = new MainWindow(shell); // made, never shown

            Assert.Equal((960, 600), (window.MinWidth, window.MinHeight));
            Assert.Equal("Bosun Panel", window.Title);
            var chrome = WindowChrome.GetWindowChrome(window);
            Assert.Equal(44, chrome.CaptionHeight);
            Assert.Equal(new Thickness(6), chrome.ResizeBorderThickness);
            Assert.False(chrome.UseAeroCaptionButtons); // the buttons are the shell's own

            var view = Assert.IsType<ShellView>(window.Content);
            Assert.Same(view, window.View);
            Assert.Same(shell, view.DataContext);
            Assert.Same(window, view.Surface); // the three buttons act on this window
            Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(view.WindowButtons));
            Assert.False(window.IsVisible);

            // The window is the shell's surface: its state is the window's own.
            IWindowSurface surface = window;
            Assert.False(surface.IsMaximised);
            surface.ToggleMaximise();
            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.Equal("Restore", System.Windows.Automation.AutomationProperties.GetName(view.Maximise));
            surface.ToggleMaximise();
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal("Maximise", System.Windows.Automation.AutomationProperties.GetName(view.Maximise));
            surface.Minimise();
            Assert.True(surface.IsMinimised);
            surface.Restore();
            Assert.False(surface.IsMinimised);
        });
    }

    [Fact]
    public void Opening_the_panel_makes_its_window_around_the_shell_and_shows_it_once()
    {
        Sta.Run(() =>
        {
            var shell = WindowStart.NewShell(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            Assert.Equal(7, shell.Sections.Count);
            Assert.Equal("Panel ready", Assert.Single(shell.Activity).Message);

            ShellViewModel? given = null;
            var window = new FakeWindow();
            var opened = WindowStart.Open(shell, s =>
            {
                given = s;
                return window;
            });

            Assert.Same(window, opened);
            Assert.Same(shell, given);
            Assert.Equal(["show"], window.Calls);
            Assert.True(window.IsVisible);
        });
    }

    [Fact]
    public void A_window_asked_forward_is_restored_if_minimised_shown_if_hidden_and_activated_in_that_order()
    {
        var hiddenAndMinimised = new FakeWindow { IsMinimised = true, IsVisible = false };
        WindowActivation.BringForward(hiddenAndMinimised);
        Assert.Equal(["restore", "show", "activate"], hiddenAndMinimised.Calls);
        Assert.Equal((false, true), (hiddenAndMinimised.IsMinimised, hiddenAndMinimised.IsVisible));

        var minimised = new FakeWindow { IsMinimised = true, IsVisible = true };
        WindowActivation.BringForward(minimised);
        Assert.Equal(["restore", "activate"], minimised.Calls);

        var behind = new FakeWindow { IsVisible = true };
        WindowActivation.BringForward(behind);
        Assert.Equal(["activate"], behind.Calls);
    }

    // ---- closing ----

    [Fact]
    public void Closing_with_unsaved_edits_is_held_back_and_only_goes_through_on_discard_or_a_save_that_worked()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var (shell, first, _, _) = Shells.WithTestPages();
            var window = new FakeWindow();
            var guard = new ExitGuard(shell, window);
            window.Closing = guard.MayCloseNow; // as the real window asks while it is closing

            // Nothing unsaved: the window just closes.
            Assert.True(guard.MayCloseNow());
            Assert.False(shell.IsAsking);

            void CloseAnd(LeaveChoice choice)
            {
                window.Close(); // the close button, or Alt+F4
                Assert.True(shell.IsAsking, "closing did not ask about the unsaved edits");
                shell.Answer(choice);
                Assert.True(StartupSupport.Pump(() => !shell.IsLeaving), "the answer was never taken");
                StartupSupport.Pump(() => false, TimeSpan.FromMilliseconds(50)); // let a close that was agreed go through
            }

            first.Edits = "slots 3";
            CloseAnd(LeaveChoice.Stay);
            Assert.Equal((0, "slots 3"), (window.Closed, first.Edits));

            first.OnSave = () => Task.FromResult(ActionOutcome.Failed("the disk is full"));
            CloseAnd(LeaveChoice.Save);
            Assert.Equal((0, "slots 3"), (window.Closed, first.Edits)); // still open, still edited
            Assert.Equal("Not saved: the disk is full", first.SaveFailure);

            first.OnSave = () => Task.FromResult(ActionOutcome.Done);
            CloseAnd(LeaveChoice.Save);
            Assert.Equal((1, null, "slots 3"), (window.Closed, first.Edits, first.Saved)); // saved, then closed, once
            Assert.Equal(["close", "close", "close", "close"], window.Calls);              // three asked, and the one that went through
        });
    }

    [Fact]
    public void Closing_after_discard_closes_once_and_a_question_that_cant_be_asked_keeps_the_window_open()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var (shell, first, _, _) = Shells.WithTestPages();
            var window = new FakeWindow();
            var guard = new ExitGuard(shell, window);
            window.Closing = guard.MayCloseNow;

            first.Edits = "slots 3";
            shell.AskToLeave = _ => throw new InvalidOperationException("the dialog could not be shown");
            window.Close();
            StartupSupport.Pump(() => false, TimeSpan.FromMilliseconds(50));
            Assert.Equal((0, "slots 3"), (window.Closed, first.Edits));
            Assert.Equal("Close Bosun Panel: the dialog could not be shown", shell.Activity.Single(entry => entry.Severity == LogSeverity.Fail).Message);

            shell.AskToLeave = _ => Task.FromResult(LeaveChoice.Discard);
            window.Close();
            Assert.True(StartupSupport.Pump(() => window.Closed == 1), "Discard did not close the window");
            Assert.Equal((1, null), (first.DiscardCount, first.Edits));
        });
    }

    // ---- the snap layouts' hit test ----

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void A_point_is_on_the_maximise_button_by_the_screen_s_pixels_at_the_monitor_s_scale(double scale)
    {
        // A 46 by 44 button whose corner is at pixel (1000, -300): a monitor above the main one.
        var size = new Size(46, 44);
        int Px(double units) => (int)Math.Round(units * scale);
        bool Over(int x, int y) => SnapLayoutHook.Over(x, y, 1000, -300, size, scale);

        Assert.True(Over(1000, -300));                         // its top-left pixel
        Assert.True(Over(1000 + Px(45), -300 + Px(43)));       // just inside its far corner
        Assert.False(Over(999, -300));
        Assert.False(Over(1000, -301));
        Assert.False(Over(1000 + Px(46), -300 + Px(10)));      // one unit past its width
        Assert.False(Over(1000 + Px(10), -300 + Px(44)));      // one unit past its height
        Assert.False(SnapLayoutHook.Over(1000, -300, 1000, -300, size, 0));
        Assert.False(SnapLayoutHook.Over(1000, -300, 1000, -300, Size.Empty, scale));
    }

    [Fact]
    public void A_mouse_message_s_point_is_two_signed_numbers()
    {
        foreach (var (x, y) in new[] { (0, 0), (120, 20), (-1920, 300), (3000, -1080), (-1, -1), (32767, -32768) })
            Assert.Equal((x, y), SnapLayoutHook.ScreenPoint(SnapLayoutHook.Pack(x, y)));
    }

    private sealed class Maximise
    {
        public int Toggles;
        public List<bool> Hovers = [];
        public readonly SnapLayoutHook Hook;

        public Maximise() => Hook = new SnapLayoutHook((x, y) => x is >= 100 and < 146 && y is >= 0 and < 44, () => Toggles++, Hovers.Add);

        /// <summary>One message; whether the hook answered it, and with what.</summary>
        public (bool Handled, long Result) Send(int message, long wParam = 0, int x = 0, int y = 0)
        {
            var handled = false;
            var result = Hook.Handle(message, new IntPtr(wParam), SnapLayoutHook.Pack(x, y), ref handled);
            return (handled, result.ToInt64());
        }
    }

    [Fact]
    public void Over_the_button_the_window_answers_htmaxbutton_and_a_press_and_release_there_is_its_click()
    {
        var max = new Maximise();
        const int hit = SnapLayoutHook.WM_NCHITTEST, down = SnapLayoutHook.WM_NCLBUTTONDOWN, up = SnapLayoutHook.WM_NCLBUTTONUP;
        const int move = SnapLayoutHook.WM_NCMOUSEMOVE, leave = SnapLayoutHook.WM_NCMOUSELEAVE, button = SnapLayoutHook.HTMAXBUTTON;
        const int caption = 2, close = 20;

        Assert.Equal((true, 9), max.Send(hit, x: 120, y: 20));
        Assert.Equal((false, 0), max.Send(hit, x: 99, y: 20));   // outside: the window answers for itself
        Assert.Equal((false, 0), max.Send(hit, x: 120, y: 44));

        // A click: pressed and released on the button.
        Assert.True(max.Send(down, button).Handled);
        Assert.Equal(0, max.Toggles);
        Assert.True(max.Send(up, button).Handled);
        Assert.Equal(1, max.Toggles);

        // Pressed on the button, released off it: not a click, and the release is the window's to handle.
        max.Send(down, button);
        Assert.False(max.Send(up, caption).Handled);
        Assert.Equal(1, max.Toggles);
        // Released on the button after a press that began elsewhere: not a click either.
        Assert.False(max.Send(down, caption).Handled);
        max.Send(up, button);
        Assert.Equal(1, max.Toggles);
        // Pressed, then the pointer left the window: the press is forgotten.
        max.Send(down, button);
        max.Send(leave);
        max.Send(up, button);
        Assert.Equal(1, max.Toggles);

        // Hover: set when the pointer is over the button, cleared when it moves off it or leaves, each said once.
        max.Hovers.Clear();
        max.Send(move, button);
        max.Send(move, button);
        max.Send(move, close);
        max.Send(move, button);
        max.Send(leave);
        max.Send(move, button);
        max.Send(SnapLayoutHook.WM_MOUSEMOVE); // into the client area
        Assert.Equal([true, false, true, false, true, false], max.Hovers);
        Assert.Equal((false, 0), max.Send(0x0010)); // any other message is not the hook's
    }

    [Fact]
    public void Attached_to_a_real_window_handle_the_hook_answers_and_detached_it_does_not()
    {
        Sta.Run(() =>
        {
            const int WS_POPUP = unchecked((int)0x80000000), WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            // A real window that is never shown: no WS_VISIBLE.
            using var source = new HwndSource(new HwndSourceParameters("Bosun snap test") { WindowStyle = WS_POPUP, ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, Width = 300, Height = 100 });
            var max = new Maximise();

            long Hit(int x, int y) => SendMessage(source.Handle, SnapLayoutHook.WM_NCHITTEST, IntPtr.Zero, SnapLayoutHook.Pack(x, y)).ToInt64();

            Assert.NotEqual(SnapLayoutHook.HTMAXBUTTON, Hit(120, 20)); // before it is attached
            max.Hook.Attach(source);
            Assert.Equal(SnapLayoutHook.HTMAXBUTTON, Hit(120, 20));
            Assert.NotEqual(SnapLayoutHook.HTMAXBUTTON, Hit(10, 20));   // off the button: the window's own answer

            // The press and release reach it through the window too.
            SendMessage(source.Handle, SnapLayoutHook.WM_NCLBUTTONDOWN, new IntPtr(SnapLayoutHook.HTMAXBUTTON), SnapLayoutHook.Pack(120, 20));
            SendMessage(source.Handle, SnapLayoutHook.WM_NCLBUTTONUP, new IntPtr(SnapLayoutHook.HTMAXBUTTON), SnapLayoutHook.Pack(120, 20));
            Assert.Equal(1, max.Toggles);

            max.Hook.Detach();
            Assert.NotEqual(SnapLayoutHook.HTMAXBUTTON, Hit(120, 20));
            max.Hook.Detach(); // twice is nothing
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    // ---- the icon ----

    [Fact]
    public void The_built_panel_carries_the_app_icon_and_the_title_bar_takes_its_small_frame()
    {
        var program = StartupSupport.Built("Bosun.Panel", "Bosun.Panel.exe");
        var module = LoadLibraryEx(program, IntPtr.Zero, 0x00000002);
        Assert.True(module != IntPtr.Zero, $"{program} could not be opened");
        try
        {
            var groups = 0;
            EnumResourceNames(module, new IntPtr(14), (_, _, _, _) => // RT_GROUP_ICON
            {
                groups++;
                return true;
            }, IntPtr.Zero);
            Assert.True(groups >= 1, $"{program} has no icon");
        }
        finally
        {
            FreeLibrary(module);
        }

        Sta.Run(() =>
        {
            // The frame at least as big as asked, and the smallest such: 20 px is drawn for the pixel grid.
            var small = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(ShellView.IconFrame(20));
            Assert.Equal(20, small.PixelWidth);
            Assert.Equal(32, ((System.Windows.Media.Imaging.BitmapSource)ShellView.IconFrame(30)!).PixelWidth);
            Assert.Equal(256, ((System.Windows.Media.Imaging.BitmapSource)ShellView.IconFrame(1000)!).PixelWidth); // nothing that big: the largest
        });
    }

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}

/// <summary>
/// Rules over the shell's own source (D02 T02 §4): every command is made by the activity log, so none can be added
/// that does not write its line; the application makes and shows the real window; and every placeholder names a
/// section of the plan that exists.
/// </summary>
public partial class ShellRulesTests
{
    [GeneratedRegex(@"\bnew\s+AsyncCommand\s*\(")]
    private static partial Regex CommandMade();

    /// <summary>Each place a command is made outside the activity log: (file, line).</summary>
    private static List<string> CommandsMadeByHand(IEnumerable<(string File, string Text)> sources) =>
        [.. sources.Where(source => !source.File.Replace('\\', '/').EndsWith("Shell/ActivityLog.cs", StringComparison.Ordinal))
            .SelectMany(source => CommandMade().Matches(source.Text).Select(match => $"{source.File}:{source.Text.AsSpan(0, match.Index).Count('\n') + 1}"))];

    private static IEnumerable<(string File, string Text)> PanelSources() =>
        Directory.EnumerateFiles(TestPaths.Panel(), "*.cs", SearchOption.AllDirectories)
            .Select(path => (File: Path.GetRelativePath(TestPaths.Panel(), path), Text: File.ReadAllText(path)))
            .Where(source => !source.File.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    [Fact]
    public void A_command_is_only_ever_made_by_the_activity_log()
    {
        var sources = PanelSources().ToList();
        Assert.Contains(sources, source => source.File.EndsWith("ActivityLog.cs", StringComparison.Ordinal) && CommandMade().IsMatch(source.Text)); // the one place there is
        var byHand = CommandsMadeByHand(sources);
        Assert.True(byHand.Count == 0, "a command made outside ActivityLog.Command writes no line: " + string.Join(", ", byHand));

        // And one made by hand in a view is found, with its file and line.
        var stray = sources.Append((File: Path.Combine("Views", "QueueView.xaml.cs"), Text: "class V {\n  void M() {\n    var c = new AsyncCommand (Save, Failed);\n  }\n}\n"));
        Assert.Equal([Path.Combine("Views", "QueueView.xaml.cs") + ":3"], CommandsMadeByHand(stray));
    }

    [Fact]
    public void The_application_makes_the_real_window_through_the_one_place_that_shows_it()
    {
        var app = File.ReadAllText(Path.Combine(TestPaths.Panel(), "App.xaml.cs"));
        var make = app[app.IndexOf("private void MakeWindow()", StringComparison.Ordinal)..];
        make = make[..make.IndexOf("/// <summary>", StringComparison.Ordinal)];
        Assert.Contains("WindowStart.Open(", make, StringComparison.Ordinal);
        Assert.Contains("new MainWindow(", make, StringComparison.Ordinal);
        Assert.Contains("MakeWindow = MakeWindow", app, StringComparison.Ordinal); // and startup is handed it
        Assert.Contains("WindowActivation.BringForward(", app, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^D(\d\d) T(\d\d) §(\d+)$")]
    private static partial Regex Reference();

    [Fact]
    public void Every_placeholder_names_a_section_of_the_plan_that_exists()
    {
        var todo = Path.Combine(TestPaths.Repo(), "todo");
        foreach (var row in Sections.Rows)
        {
            var reference = Reference().Match(row.Owner);
            Assert.True(reference.Success, $"{row.Key}: '{row.Owner}' is not a section reference");
            var domain = Assert.Single(Directory.GetDirectories(todo, reference.Groups[1].Value + "-*"));
            var file = Assert.Single(Directory.GetFiles(domain, $"TODO-{reference.Groups[2].Value}-*.md"));
            var heading = new Regex($@"^## {reference.Groups[3].Value}\. \S", RegexOptions.Multiline);
            Assert.True(heading.IsMatch(File.ReadAllText(file)), $"{row.Key}: {row.Owner} is not a section of {Path.GetFileName(file)}");
        }
        Assert.Equal(7, Sections.Rows.Count);
    }
}

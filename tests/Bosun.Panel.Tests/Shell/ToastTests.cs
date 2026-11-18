using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Bosun.Panel.Accessibility;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The window's toasts (D02 T03 §9, design/SPEC.md §19 Toast): at most three, a FAIL kept until dismissed, gone after
/// 6 s unless held, each with its one Activity line, announced once, and nothing shown while the window is hidden.
/// </summary>
public class ToastTests
{
    /// <summary>A clock the test moves: timers fire as it passes them, from a worker thread when asked, as a real one does.</summary>
    sealed class FakeClock : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        public DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        public bool OffThread;

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state, Now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public int Live => _timers.Count(t => !t.Disposed);

        /// <summary>Moves the clock on, firing each timer it passes once.</summary>
        public void Advance(TimeSpan by)
        {
            Now += by;
            foreach (var t in _timers.Where(t => !t.Disposed && !t.Fired && t.Due <= Now).ToList())
            {
                t.Fired = true;
                if (OffThread) Task.Run(() => t.Callback(t.State)).Wait();
                else t.Callback(t.State);
            }
        }

        public sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public TimerCallback Callback => callback;
            public object? State => state;
            public DateTimeOffset Due { get; } = due;
            public bool Fired, Disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    static void OnUi(Action test) => PageRenderer.InPage("Dark", () => new Grid(), _ =>
    {
        test();
        return 0;
    });

    static (ShellViewModel Shell, FakeClock Clock) Shell(bool windowShown = true)
    {
        var clock = new FakeClock();
        var shell = new ShellViewModel(Sections.All(), Shells.Log(), windowShown: windowShown, clock: clock);
        return (shell, clock);
    }

    static string[] Words(ToastHost host) => host.Shown.Select(t => t.Message).ToArray();

    [Fact]
    public void An_expanded_toast_is_held_until_collapsed_whatever_the_pointer_and_the_keyboard_do()
    {
        // D02 T04 §7: its own hold, not the focus hold borrowed, which a later focus change would release.
        OnUi(() =>
        {
            var (shell, clock) = Shell();
            var host = shell.Toasts;
            host.Show(LogSeverity.Info, "a long result");
            var toast = host.Shown.Single();
            host.Expand(toast, true);
            host.HoldByPointer(toast, true);
            host.HoldByPointer(toast, false);                                                 // the pointer leaves
            host.HoldByFocus(toast, true);
            host.HoldByFocus(toast, false);                                                   // the keyboard leaves
            clock.Advance(TimeSpan.FromSeconds(30));
            Pump();
            Assert.Contains(toast, host.Shown);                                               // still held, expanded
            host.Expand(toast, false);                                                        // collapsed: its time runs again
            clock.Advance(TimeSpan.FromSeconds(7));
            Pump();
            Assert.DoesNotContain(toast, host.Shown);
            host.Show(LogSeverity.Info, "another");
            var other = host.Shown.Single();
            host.Expand(other, true);
            host.Dismiss(other);                                                               // dismissed while expanded
            Assert.Empty(host.Shown);
            host.Show(LogSeverity.Info, "a third");
            host.Expand(host.Shown.Single(), true);
            host.WindowShown = false;                                                         // hidden while expanded
            Assert.Empty(host.Shown);
        });
    }

    /// <summary>A toast card drawn at its width, with <paramref name="message"/>, in a shell's host.</summary>
    static int Drawn(string message, Func<ToastCard, Border, int> read) => PageRenderer.InPage("Dark", () =>
    {
        var (shell, _) = Shell();
        shell.Toasts.Show(LogSeverity.Fail, message);
        return new ToastCard { DataContext = shell.Toasts.Shown.Single(), Host = shell.Toasts, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    }, page =>
    {
        PageRenderer.Settle(page);
        return read((ToastCard)page.Child, page);
    }, 480, 600);

    [Theory]
    [InlineData("Saved.", false)]
    [InlineData("Line one\nLine two\nLine three", false)]
    [InlineData("Save queue limits: bosun config set slots 9: slots must be between 1 and 8, and the file was left as it was, so the queue still runs 2 slots and every job waits its turn as before; change the value to one the queue allows and save again.", true)]
    public void Show_more_is_offered_only_where_the_layout_cuts_the_text(string message, bool cut)
    {
        Drawn(message.Replace("\\n", "\n", StringComparison.Ordinal), (card, _) =>
        {
            Assert.Equal(cut, card.IsCut);
            var more = (Button)card.Template.FindName("ShowMore", card);
            Assert.Equal(cut ? Visibility.Visible : Visibility.Collapsed, more.Visibility);
            if (cut)
            {
                Assert.Equal("Show the whole message", System.Windows.Automation.AutomationProperties.GetName(more));
                Assert.True(more.ActualHeight >= 32, $"Show more is {more.ActualHeight} px high");
            }
            return 0;
        });
    }

    [Fact]
    public void Expanded_the_whole_message_is_reachable_and_Dismiss_stays_on_the_card()
    {
        var message = string.Join(" ", Enumerable.Range(1, 120).Select(i => $"word{i}"));
        Drawn(message, (card, page) =>
        {
            Assert.True(card.IsCut);
            ((Button)card.Template.FindName("ShowMore", card)).Command.Execute(null);
            PageRenderer.Settle(page);
            var scroll = (ToastMessage)card.Template.FindName("WordsScroll", card);
            var words = (TextBlock)card.Template.FindName("Words", card);
            Assert.True(scroll.ScrollableHeight > 0, "a very long message scrolls inside the card");
            Assert.True(scroll.Focusable && scroll.IsTabStop, "the keyboard can reach the message to scroll it");
            Assert.True(words.ActualHeight > ToastCard.Lines * ToastCard.LineHeight, "the whole message is laid out, not three lines");
            Assert.True(scroll.ActualHeight <= (double)page.FindResource("Size.ToastText") + 0.5, "bounded");
            scroll.ScrollToBottom();
            PageRenderer.Settle(page);
            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 0.5);                // its end is reached
            var dismiss = (Button)card.Template.FindName("Dismiss", card);
            Assert.True(dismiss.IsVisible && dismiss.TransformToAncestor(page).Transform(new Point()).Y >= 0);
            return 0;
        });
    }

    [Fact]
    public void Expanded_the_keyboard_scrolls_the_message_down_and_to_its_last_line()
    {
        // Key presses through the message area's own keyboard handling, in a real (hidden) source: Down moves it a
        // line, End reaches its end, Home goes back (completion review). That Tab reaches it at all is the E2E's.
        var message = string.Join(" ", Enumerable.Range(1, 120).Select(i => $"word{i}"));
        Drawn(message, (card, page) =>
        {
            ((Button)card.Template.FindName("ShowMore", card)).Command.Execute(null);
            PageRenderer.Settle(page);
            var scroll = (ToastMessage)card.Template.FindName("WordsScroll", card);
            var source = PresentationSource.FromVisual(scroll)!;
            void Press(Key key)
            {
                scroll.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                PageRenderer.Settle(page);
            }
            Assert.Equal(0, scroll.VerticalOffset);
            Press(Key.Down);
            Assert.True(scroll.VerticalOffset > 0, "Down scrolls the message");
            Press(Key.End);
            Assert.True(scroll.ScrollableHeight > 0);
            Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, 0.5);              // its last line
            Press(Key.Home);
            Assert.Equal(0, scroll.VerticalOffset, 0.5);
            return 0;
        });
    }

    [Fact]
    public void Expanded_the_message_is_in_the_automation_tree_under_its_name_with_the_message_as_help()
    {
        // WPF leaves a template's ScrollViewer and text out of the automation tree, so the opened message took the
        // keyboard and no screen reader could find it (found by the E2E, D02 T04 §7).
        var message = string.Join(" ", Enumerable.Range(1, 120).Select(i => $"word{i}"));
        Drawn(message, (card, page) =>
        {
            var scroll = (ToastMessage)card.Template.FindName("WordsScroll", card);
            var peer = UIElementAutomationPeer.CreatePeerForElement(scroll);
            Assert.False(peer.IsControlElement(), "a collapsed message is not a stop of its own: the toast's text says it");
            ((Button)card.Template.FindName("ShowMore", card)).Command.Execute(null);
            PageRenderer.Settle(page);
            Assert.True(peer.IsControlElement() && peer.IsContentElement());
            Assert.Equal("The whole message", peer.GetName());
            Assert.Equal(message, peer.GetHelpText());
            Assert.True(peer.IsKeyboardFocusable());
            return 0;
        });
    }

    [Fact]
    public void Each_toast_in_the_shell_is_a_list_item_named_by_what_it_says()
    {
        PageRenderer.InPage("Dark", () =>
        {
            var (shell, _) = Shell();
            shell.Toasts.Show(LogSeverity.Fail, "Save failed: the file is read-only.");
            return new ShellView { DataContext = shell };
        }, page =>
        {
            PageRenderer.Settle(page);
            var view = ShellSupport.Under<ShellView>(page).Single();
            var list = (ItemsControl)view.FindName("Toasts");
            var items = UIElementAutomationPeer.CreatePeerForElement(list).GetChildren();
            var item = Assert.Single(items);
            Assert.Equal("Failed: Save failed: the file is read-only.", item.GetName());
            return 0;
        });
    }

    [Fact]
    public void Three_expanded_toasts_keep_every_Dismiss_in_the_smallest_window()
    {
        var message = string.Join(" ", Enumerable.Range(1, 120).Select(i => $"word{i}"));
        PageRenderer.InPage("Dark", () =>
        {
            var (shell, _) = Shell();
            for (var i = 0; i < 3; i++) shell.Toasts.Show(LogSeverity.Fail, message);
            foreach (var toast in shell.Toasts.Shown.ToList()) shell.Toasts.Expand(toast, true);
            return new Bosun.Panel.Shell.ShellView { DataContext = shell };
        }, page =>
        {
            PageRenderer.Settle(page);
            var dismisses = ShellSupport.Under<Button>(page).Where(b => b.Name == "Dismiss").ToList();
            Assert.Equal(3, dismisses.Count);
            foreach (var dismiss in dismisses)
            {
                var box = dismiss.TransformToAncestor(page).TransformBounds(new Rect(dismiss.RenderSize));
                Assert.True(box.Top >= 0 && box.Bottom <= page.ActualHeight, $"a Dismiss at {box} is off the {page.ActualHeight} px window");
            }
            return 0;
        }, 960, 600);
    }

    [Fact]
    public void A_fourth_toast_takes_the_place_of_the_oldest_that_is_not_a_FAIL()
    {
        OnUi(() =>
        {
            var host = Shell().Shell.Toasts;
            host.Show(LogSeverity.Info, "a");
            host.Show(LogSeverity.Fail, "b");
            host.Show(LogSeverity.Info, "c");
            host.Show(LogSeverity.Info, "d");
            Assert.Equal(["b", "c", "d"], Words(host));                                     // a went, not the FAIL b
            host.Show(LogSeverity.Fail, "e");
            Assert.Equal(["b", "d", "e"], Words(host));
            Assert.Equal(0, host.Overflow);
        });
    }

    [Fact]
    public void A_held_toast_is_never_the_one_a_newcomer_takes_the_place_of()
    {
        // Opened in full, under the pointer or holding the keyboard, a toast is being read: a newcomer takes the
        // place of the oldest toast nobody holds, and when every one is held it is counted, not shown (§7 round 1).
        OnUi(() =>
        {
            var host = Shell().Shell.Toasts;
            host.Show(LogSeverity.Info, "a");
            host.Show(LogSeverity.Info, "b");
            host.Show(LogSeverity.Info, "c");
            host.Expand(host.Shown[0], true);
            host.Show(LogSeverity.Info, "d");
            Assert.Equal(["a", "c", "d"], Words(host));                                     // b went, not the opened a
            host.HoldByPointer(host.Shown[1], true);
            host.HoldByFocus(host.Shown[2], true);
            host.Show(LogSeverity.Info, "e");
            Assert.Equal(["a", "c", "d"], Words(host));                                     // all three held
            Assert.Equal("1 more in the Activity pane", host.OverflowText);
            host.Expand(host.Shown[0], false);
            host.Show(LogSeverity.Info, "f");
            Assert.Equal(["c", "d", "f"], Words(host));                                     // a, let go, is the oldest free
        });
    }

    [Fact]
    public void Three_FAILs_stay_and_newcomers_are_counted_not_shown()
    {
        OnUi(() =>
        {
            var host = Shell().Shell.Toasts;
            foreach (var f in new[] { "f1", "f2", "f3" }) host.Show(LogSeverity.Fail, f);
            host.Show(LogSeverity.Info, "done");
            host.Show(LogSeverity.Fail, "f4");
            Assert.Equal(["f1", "f2", "f3"], Words(host));                                  // no FAIL left before it was dismissed
            Assert.Equal("2 more in the Activity pane", host.OverflowText);
            host.Dismiss(host.Shown[0]);
            Assert.Equal(["f2", "f3"], Words(host));
            Assert.Equal("", host.OverflowText);
        });
    }

    [Fact]
    public void Each_action_result_done_failed_or_thrown_makes_one_line_and_one_toast()
    {
        OnUi(() =>
        {
            var (shell, _) = Shell();
            var lines = shell.Activity.Count;
            ICommand done = shell.Log.Command("Save queue limits", () => Task.FromResult(ActionOutcome.Done));
            ICommand failed = shell.Log.Command("Save queue limits", () => Task.FromResult(ActionOutcome.Failed("the file is read-only")));
            ICommand thrown = shell.Log.Command("Read the queue", () => throw new InvalidOperationException("the queue file is locked"));
            done.Execute(null);
            failed.Execute(null);
            thrown.Execute(null);
            Pump();
            Assert.Equal(lines + 3, shell.Activity.Count);
            Assert.Equal([(LogSeverity.Info, "Save queue limits"), (LogSeverity.Fail, "Save queue limits: the file is read-only"), (LogSeverity.Fail, "Read the queue: the queue file is locked")],
                shell.Activity.Skip(lines).Select(l => (l.Severity, l.Message)));
            Assert.Equal(shell.Activity.Skip(lines).Select(l => l.Message), Words(shell.Toasts)); // the toast is its line
            Assert.Equal(["Done", "Failed", "Failed"], shell.Toasts.Shown.Select(t => t.Word));
        });
    }

    [Fact]
    public void A_toast_goes_after_6_s_a_FAIL_stays_and_no_timer_runs_without_one_to_time()
    {
        OnUi(() =>
        {
            var (shell, clock) = Shell();
            var host = shell.Toasts;
            Assert.Equal(0, host.RunningTimers);
            host.Show(LogSeverity.Info, "done");
            host.Show(LogSeverity.Fail, "failed");
            Assert.Equal(1, host.RunningTimers);                                              // the FAIL has none
            clock.Advance(TimeSpan.FromSeconds(5.9));
            Pump();
            Assert.Equal(["done", "failed"], Words(host));
            clock.Advance(TimeSpan.FromSeconds(0.2));
            Pump();
            Assert.Equal(["failed"], Words(host));
            clock.Advance(TimeSpan.FromSeconds(60));
            Pump();
            Assert.Equal(["failed"], Words(host));                                           // a FAIL stays until dismissed
            Assert.Equal(0, host.RunningTimers);
            host.Dismiss(host.Shown[0]);
            Assert.Equal(0, clock.Live);                                                      // empty: no timer at all
        });
    }

    [Fact]
    public void Hover_and_focus_each_hold_it_and_it_runs_on_with_its_time_left_only_when_neither_does()
    {
        OnUi(() =>
        {
            var (shell, clock) = Shell();
            var host = shell.Toasts;
            host.Show(LogSeverity.Info, "done");
            var toast = host.Shown[0];
            clock.Advance(TimeSpan.FromSeconds(2));
            host.HoldByPointer(toast, true);
            host.HoldByFocus(toast, true);
            clock.Advance(TimeSpan.FromSeconds(30));
            Pump();
            host.HoldByPointer(toast, false);                                                 // focus still holds it
            clock.Advance(TimeSpan.FromSeconds(30));
            Pump();
            Assert.Single(host.Shown);
            host.HoldByFocus(toast, false);                                                   // neither: 4 s left
            clock.Advance(TimeSpan.FromSeconds(3.9));
            Pump();
            Assert.Single(host.Shown);
            clock.Advance(TimeSpan.FromSeconds(0.2));
            Pump();
            Assert.Empty(host.Shown);
        });
    }

    [Fact]
    public void An_expiry_from_another_thread_is_done_on_the_UI_thread_and_a_stale_one_does_nothing()
    {
        OnUi(() =>
        {
            var (shell, clock) = Shell();
            clock.OffThread = true;
            var host = shell.Toasts;
            host.Show(LogSeverity.Info, "first");
            clock.Advance(TimeSpan.FromSeconds(7));                                           // fired off thread, queued here
            Assert.Single(host.Shown);
            Pump();
            Assert.Empty(host.Shown);

            host.Show(LogSeverity.Info, "old");
            clock.Advance(TimeSpan.FromSeconds(7));                                           // its expiry queued...
            shell.WindowHidden();                                                             // ...then dropped by hiding
            shell.WindowShown();
            host.Show(LogSeverity.Info, "new");
            Pump();
            Assert.Equal(["new"], Words(host));                                              // the stale expiry touched nothing
        });
    }

    [Fact]
    public void Hiding_drops_the_stack_results_while_hidden_are_only_logged_and_nothing_comes_back()
    {
        OnUi(() =>
        {
            var (shell, clock) = Shell();
            shell.Toasts.Show(LogSeverity.Fail, "kept until dismissed");
            shell.Toasts.Show(LogSeverity.Info, "done");
            shell.WindowHidden();
            Assert.Empty(shell.Toasts.Shown);
            Assert.Equal(0, clock.Live);
            var lines = shell.Activity.Count;
            ((ICommand)shell.Log.Command("Save settings", () => Task.FromResult(ActionOutcome.Done))).Execute(null);
            Pump();
            Assert.Equal(lines + 1, shell.Activity.Count);                                    // logged
            Assert.Empty(shell.Toasts.Shown);                                                 // not shown
            shell.WindowShown();
            Assert.Empty(shell.Toasts.Shown);                                                 // and nothing comes back

            var (hidden, _) = Shell(windowShown: false);                                     // started in the tray
            hidden.Toasts.Show(LogSeverity.Info, "done");
            Assert.Empty(hidden.Toasts.Shown);
        });
    }

    [Fact]
    public void Cards_are_announced_once_held_by_the_pointer_and_dismissed_by_their_button()
    {
        var announced = new List<string>();
        PageRenderer.InPage("Dark", () =>
        {
            LiveRegion.Raising = peer => announced.Add(peer.GetName());
            var (shell, _) = Shell();
            return new ShellView { DataContext = shell };
        }, page =>
        {
            try
            {
                var shell = (ShellViewModel)ShellSupport.Under<ShellView>(page).Single().DataContext;
                shell.Toasts.Show(LogSeverity.Info, "Save settings");
                shell.Toasts.Show(LogSeverity.Fail, "Save queue limits: the file is read-only");
                PageRenderer.Settle(page);
                PageRenderer.Settle(page);
                var cards = ShellSupport.Under<ToastCard>(page).ToList();
                Assert.Equal(2, cards.Count);
                Assert.Equal(["Done: Save settings", "Failed: Save queue limits: the file is read-only"], announced); // once each, with the outcome
                Assert.Equal(["Done", "Failed"], cards.Select(c => ShellSupport.Under<Bosun.Panel.Controls.StatusPill>(c).Single().ShownWord));
                var dismiss = (Button)cards[1].Template.FindName("Dismiss", cards[1]);
                Assert.Equal("Dismiss", AutomationProperties.GetName(dismiss));
                Assert.Equal("Failed: Save queue limits: the file is read-only", AutomationProperties.GetHelpText(dismiss));

                cards[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                Assert.Equal(0, shell.Toasts.RunningTimers);                                  // held: its timer stopped
                cards[0].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                Assert.Equal(1, shell.Toasts.RunningTimers);

                ((IInvokeProvider)new ButtonAutomationPeer(dismiss)).Invoke();
                PageRenderer.Settle(page);
                Assert.Equal(["Save settings"], Words(shell.Toasts));
                Assert.Equal(2, announced.Count);                                             // dismissing announces nothing

                shell.Toasts.Show(LogSeverity.Fail, "f2");                                    // three FAILs fill the stack
                shell.Toasts.Show(LogSeverity.Fail, "f3");
                shell.Toasts.Show(LogSeverity.Fail, "f4");
                shell.Toasts.Show(LogSeverity.Info, "Save settings again");                   // results past three FAILs: logged, counted
                shell.Toasts.Show(LogSeverity.Fail, "f5");
                PageRenderer.Settle(page);
                PageRenderer.Settle(page);
                Assert.Contains("2 more in the Activity pane", announced);                    // said, so it is not missed
            }
            finally
            {
                LiveRegion.Raising = null;
            }
            return 0;
        }, 1360, 860);
    }

    [Fact]
    public void Three_long_failures_keep_every_Dismiss_on_screen_at_the_least_window_size()
    {
        PageRenderer.InPage("Dark", () => new ShellView { DataContext = Shell().Shell }, page =>
        {
            var shell = (ShellViewModel)ShellSupport.Under<ShellView>(page).Single().DataContext;
            var longest = "bosun: " + string.Join(" ", Enumerable.Repeat("the tool said something long", 70));
            for (var i = 0; i < 3; i++) shell.Toasts.Show(LogSeverity.Fail, $"{i}: {longest}");
            PageRenderer.Settle(page);
            foreach (var card in ShellSupport.Under<ToastCard>(page))
            {
                var words = (TextBlock)card.Template.FindName("Words", card);
                Assert.True(words.ActualHeight <= 57.5, $"the words take {words.ActualHeight} px, more than three lines");
                Assert.Equal(words.Text, words.ToolTip);                                      // the whole result kept
                var dismiss = (Button)card.Template.FindName("Dismiss", card);
                var box = dismiss.TransformToAncestor(page).TransformBounds(new Rect(dismiss.RenderSize));
                Assert.True(box.Top >= 0 && box.Bottom <= page.Height && box.Right <= page.Width, $"Dismiss at {box} is off the window");
            }
            return 0;
        }, 960, 600);
    }
}

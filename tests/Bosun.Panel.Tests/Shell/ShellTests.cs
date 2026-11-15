using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The shell (D02 T02 §4, design/SPEC.md §1), drawn at the renders' size in a page that is never shown: its layout,
/// its rail, its window buttons, its splitter, and the one question asked before a page with unsaved edits is left.
/// </summary>
public class ShellTests
{
    public static TheoryData<string> Themes => ["Dark", "Light"];

    // ---- the shell's state ----

    [Fact]
    public void Selecting_a_section_shows_its_page_and_hides_the_one_before_each_once_and_logs_nothing()
    {
        Sta.Run(() =>
        {
            var (shell, first, second, third) = Shells.WithTestPages();
            Assert.Same(first, shell.Page);
            Assert.Equal((1, 0), (first.ShownCount, first.HiddenCount)); // the first page is shown from the start

            var changed = new List<string?>();
            shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            shell.Show(shell.Sections[1]);
            Assert.Same(second, shell.Page);
            Assert.Same(shell.Sections[1], shell.Selected);
            Assert.Equal((1, 1), (first.ShownCount, first.HiddenCount));
            Assert.Equal((1, 0), (second.ShownCount, second.HiddenCount));
            Assert.Equal([nameof(ShellViewModel.Selected), nameof(ShellViewModel.Page)], changed);

            shell.Show(shell.Sections[1]); // the section already shown: nothing happens
            Assert.Equal((1, 0), (second.ShownCount, second.HiddenCount));

            shell.Show(shell.Sections[2]);
            Assert.Equal((1, 1), (second.ShownCount, second.HiddenCount));
            Assert.Equal((1, 0), (third.ShownCount, third.HiddenCount));
            Assert.Empty(shell.Activity); // navigation is not an action
            Assert.Throws<ArgumentException>(() => shell.Show(new Section("x", "X", Sections.App, new TestPage())));
        });
    }

    [Fact]
    public void The_rail_is_the_seven_pages_in_order_under_three_groups_each_naming_the_section_that_builds_it()
    {
        Assert.Equal(["overview", "memory", "instances", "queue", "optimise", "settings", "about"], Sections.Rows.Select(row => row.Key));
        Assert.Equal(["Monitor", "Monitor", "Monitor", "Monitor", "Machine", "App", "App"], Sections.Rows.Select(row => row.Group));
        Sta.Run(() =>
        {
            var sections = Sections.All();
            Assert.Equal(Sections.Rows.Select(row => (row.Key, row.Title, row.Group)), sections.Select(section => (section.Key, section.Title, section.Group)));
            foreach (var (section, row) in sections.Zip(Sections.Rows))
            {
                // Not a dead control: a page in its empty state, saying it is not built yet and who builds it.
                var page = Assert.IsType<PlaceholderPage>(section.Page);
                Assert.Equal((PageState.Empty, row.Owner, row.Summary), (page.State, page.Owner, page.Summary));
                Assert.Equal($"{row.Title} is not built yet.", page.EmptyReason);
                Assert.Contains(row.Owner, page.EmptyAction);
                Assert.Equal(row.Title, section.ToString()); // the row's name to a screen reader
            }
            Assert.NotSame(sections[0].Page, Sections.All()[0].Page); // two shells never share a page
        });
    }

    // ---- the layout ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_shell_is_a_44_px_title_bar_a_208_px_rail_the_page_on_its_sheet_and_a_132_px_activity_pane(string theme)
    {
        ShellSupport.InShell(null, (view, shell, host) =>
        {
            Assert.Equal(44, view.TitleBar.ActualHeight);
            Assert.Equal(new Point(208, 44), view.Sheet.TranslatePoint(new Point(0, 0), view)); // where the rail ends and the sheet begins
            Assert.Equal(132, view.PaneHeight);
            Assert.Equal(PageRenderer.Width, view.ActualWidth);

            // Seven rows under three headings, the first selected, with its marker.
            var rows = ShellSupport.Under<ListBoxItem>(view.Rail).ToList();
            Assert.Equal(7, rows.Count);
            Assert.All(rows, row => Assert.True(row.ActualHeight >= 32));
            var texts = PageRenderer.TextsOf(view.Rail);
            Assert.Equal(["Monitor", "Overview", "Memory", "Instances", "Queue", "Machine", "Optimise", "App", "Settings", "About"], texts);
            var accent = ((System.Windows.Media.SolidColorBrush)host.FindResource("B.Accent")).Color;
            Assert.Equal(accent, ShellSupport.Paint(ShellSupport.Row(view.Rail, 0).Foreground));
            Assert.Equal(accent, ShellSupport.Paint(((Border)ShellSupport.Row(view.Rail, 0).Template.FindName("mark", ShellSupport.Row(view.Rail, 0))).Background));
            Assert.Equal("Pages", AutomationProperties.GetName(view.Rail));

            // The page, in the frame, and the title bar's words.
            var all = PageRenderer.TextsOf(view);
            Assert.Equal("Bosun Panel", all[0]);
            Assert.Contains("Overview is not built yet.", all);
            Assert.Contains("It arrives with D03 T01 §5 of the plan.", all);
            Assert.NotNull(view.AppIcon.Source);

            // The Activity pane: its three lines, each starting with its word.
            Assert.Equal(["INFO", "09:10:00", "Panel ready", "WARN", "09:11:00", "WSL is not running: its rows show the last data, 4 minutes old", "FAIL", "09:12:00", "Save queue limits: slots must be between 1 and 8"],
                PageRenderer.TextsOf(view.Activity));
            Assert.Equal("Activity", AutomationProperties.GetName(view.Activity));

            // Selecting a row shows its page.
            view.Rail.SelectedIndex = 4;
            PageRenderer.Settle(host);
            Assert.Equal("optimise", shell.Selected.Key);
            Assert.Contains("Optimise is not built yet.", PageRenderer.TextsOf(view));
            Assert.DoesNotContain("Overview is not built yet.", PageRenderer.TextsOf(view));
        }, theme);
    }

    [Fact]
    public void The_shell_gives_its_text_the_theme_s_font_size_and_colour_and_a_theme_swap_repaints_it()
    {
        ShellSupport.InShell(null, (view, _, host) =>
        {
            // Set on the shell itself, so every text in it inherits them wherever the shell is put.
            Assert.NotEqual(DependencyProperty.UnsetValue, view.ReadLocalValue(Control.FontFamilyProperty));
            Assert.NotEqual(DependencyProperty.UnsetValue, view.ReadLocalValue(Control.FontSizeProperty));
            Assert.NotEqual(DependencyProperty.UnsetValue, view.ReadLocalValue(Control.ForegroundProperty));

            var plain = ShellSupport.Under<TextBlock>(view).Single(text => text.Name == "TestPageBody"); // an unstyled text in a page
            Assert.Same(host.FindResource("F.Ui"), plain.FontFamily);
            Assert.Equal((double)host.FindResource("T.Body"), plain.FontSize);
            var dark = ShellSupport.Paint(plain.Foreground);
            Assert.Equal(((System.Windows.Media.SolidColorBrush)host.FindResource("B.Text")).Color, dark);

            // The theme is swapped as the settings will swap it: the colour dictionary is replaced, nothing reloaded.
            ResourceDictionary light;
            using (var stream = System.IO.File.OpenRead(TestPaths.GeneratedTheme("Light.xaml"))) light = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
            host.Resources.MergedDictionaries[0] = light;
            PageRenderer.Settle(host);
            Assert.Equal(((System.Windows.Media.SolidColorBrush)light["B.Text"]).Color, ShellSupport.Paint(plain.Foreground));
            Assert.NotEqual(dark, ShellSupport.Paint(plain.Foreground));
        }, make: () => Shells.WithTestPages().Shell);
    }

    // ---- the window buttons ----

    [Fact]
    public void Each_window_button_has_a_name_and_a_tooltip_and_does_its_work_on_the_window()
    {
        ShellSupport.InShell(null, (view, _, host) =>
        {
            var window = new FakeWindow();
            view.Surface = window;
            Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(view.WindowButtons), "the buttons are the window's to click, not its caption to drag");

            foreach (var (button, name) in new[] { (view.Minimise, "Minimise"), (view.Maximise, "Maximise"), (view.CloseButton, "Close") })
            {
                Assert.Equal(name, AutomationProperties.GetName(button));
                Assert.Equal(name, button.ToolTip);
                Assert.Same(host.FindResource("Btn.Chrome"), button.Style);
                Assert.Equal((46, 44), (button.ActualWidth, button.ActualHeight));
            }
            Assert.Equal("danger", view.CloseButton.Tag); // the close button hovers to danger

            ShellSupport.Invoke(view.Minimise);
            ShellSupport.Invoke(view.Maximise);
            ShellSupport.Invoke(view.CloseButton);
            PageRenderer.Settle(host);
            Assert.Equal(["minimise", "toggle", "close"], window.Calls);

            // Maximised, the button says what it does next.
            view.SetMaximised(true);
            Assert.Equal(("Restore", "Restore"), (AutomationProperties.GetName(view.Maximise), (string)view.Maximise.ToolTip));
            view.SetMaximised(false);
            Assert.Equal("Maximise", AutomationProperties.GetName(view.Maximise));

            // Its hover, which the window sets since Windows keeps the button's mouse to itself.
            var resting = ShellSupport.Paint(view.Maximise.Background);
            view.SetMaximiseHover(true);
            Assert.Equal(((System.Windows.Media.SolidColorBrush)host.FindResource("B.Hover")).Color, ShellSupport.Paint(view.Maximise.Background));
            view.SetMaximiseHover(false);
            Assert.Equal(resting, ShellSupport.Paint(view.Maximise.Background));

            // With no window (off screen), the buttons do nothing and throw nothing.
            view.Surface = null;
            ShellSupport.Invoke(view.CloseButton);
            PageRenderer.Settle(host);
            Assert.Equal(3, window.Calls.Count);
        });
    }

    // ---- the splitter ----

    [Fact]
    public void The_activity_pane_is_resized_by_a_drag_and_by_the_arrow_keys_and_held_at_its_limits()
    {
        ShellSupport.InShell(null, (view, shell, host) =>
        {
            Assert.Equal("Resize the Activity pane", AutomationProperties.GetName(view.PaneSplitter));
            Assert.True(view.PaneSplitter.Focusable);

            ShellSupport.Drag(view.PaneSplitter, -40); // up: the pane grows
            PageRenderer.Settle(host);
            Assert.Equal(172, view.PaneHeight);
            Assert.Equal(172, shell.ActivityHeight); // kept by the shell, for the settings to save

            ShellSupport.Key_(view.PaneSplitter, Key.Up);
            PageRenderer.Settle(host);
            Assert.Equal(188, view.PaneHeight); // one key step
            Assert.Equal(188, shell.ActivityHeight);
            ShellSupport.Key_(view.PaneSplitter, Key.Down);
            PageRenderer.Settle(host);
            Assert.Equal(172, view.PaneHeight);

            ShellSupport.Drag(view.PaneSplitter, -2000);
            PageRenderer.Settle(host);
            Assert.Equal(ShellViewModel.MaxActivityHeight, view.PaneHeight);
            ShellSupport.Key_(view.PaneSplitter, Key.Up);
            PageRenderer.Settle(host);
            Assert.Equal(400, view.PaneHeight);

            ShellSupport.Drag(view.PaneSplitter, 2000);
            PageRenderer.Settle(host);
            Assert.Equal(ShellViewModel.MinActivityHeight, view.PaneHeight);
            for (var presses = 0; presses < 3; presses++) ShellSupport.Key_(view.PaneSplitter, Key.Down);
            PageRenderer.Settle(host);
            Assert.Equal(64, view.PaneHeight);
            Assert.Equal(64, shell.ActivityHeight);

            // And the shell holds a height it is given within the same limits.
            shell.ActivityHeight = 9000;
            Assert.Equal(400, shell.ActivityHeight);
            PageRenderer.Settle(host);
            Assert.Equal(400, view.PaneHeight);
            shell.ActivityHeight = 1;
            Assert.Equal(64, shell.ActivityHeight);
        });
    }

    // ---- leaving a page with unsaved edits ----

    /// <summary>The three ways the rail's selection moves: a click, a key, and UI Automation's select.</summary>
    public static TheoryData<string> Ways => ["click", "key", "automation"];

    private static void MoveToSecond(ShellView view, string way)
    {
        switch (way)
        {
            case "click":
                Assert.True(ShellSupport.Press(view.Rail, 1), "the click was not stopped");
                break;
            case "key":
                Assert.True(ShellSupport.KeyOn(view.Rail, 0, Key.Down), "the key was not stopped");
                break;
            default:
                ShellSupport.Select(view.Rail, 1);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public void A_page_with_unsaved_edits_is_asked_about_and_only_left_on_discard_or_a_save_that_worked(string way)
    {
        TestPage first = null!, second = null!;
        ShellSupport.InShell(null, (view, shell, host) =>
        {
            void Ask()
            {
                MoveToSecond(view, way);
                PageRenderer.Settle(host);
                Assert.True(shell.IsAsking, "the question was not asked");
                Assert.Equal(0, view.Rail.SelectedIndex); // refused at once: the rail never showed the other page
                Assert.Same(first, shell.Page);
                Assert.True(view.Leave.IsVisible);
                Assert.False(view.Body.IsEnabled); // nothing behind the question can be reached
                Assert.Contains("Save your changes to Queue?", PageRenderer.TextsOf(view));
            }
            void Answered(Button button)
            {
                ShellSupport.Invoke(button);
                Assert.True(StartupSupport.Pump(() => !shell.IsAsking && !shell.IsLeaving), "the answer was never taken");
                PageRenderer.Settle(host);
                Assert.False(view.Leave.IsVisible);
                Assert.True(view.Body.IsEnabled);
            }

            first.Edits = "slots 3";

            // Keep editing: the page, its edits and the selection all stay.
            Ask();
            Answered(view.Leave.Keep);
            Assert.Equal((0, "slots 3"), (view.Rail.SelectedIndex, first.Edits));
            Assert.Equal((0, 0), (first.SaveCount, first.DiscardCount));

            // Save, and the save fails: still here, still edited, with why, on the page and in the pane.
            first.OnSave = () => Task.FromResult(ActionOutcome.Failed("the disk is full"));
            Ask();
            Answered(view.Leave.Save);
            Assert.Equal((0, "slots 3"), (view.Rail.SelectedIndex, first.Edits));
            Assert.Same(first, shell.Page);
            Assert.Equal("Not saved: the disk is full", first.SaveFailure);
            Assert.Contains("Not saved: the disk is full", PageRenderer.TextsOf(view));
            Assert.Equal("Save Queue: the disk is full", shell.Activity.Single(entry => entry.Severity == LogSeverity.Fail).Message);

            // Save, and the save throws: the same.
            first.OnSave = () => throw new InvalidOperationException("the file is locked");
            Ask();
            Answered(view.Leave.Save);
            Assert.Equal((0, "slots 3"), (view.Rail.SelectedIndex, first.Edits));
            Assert.Equal("Not saved: the file is locked", first.SaveFailure);

            // Save, and it works: saved, and the move the person asked for goes on.
            first.OnSave = () => Task.FromResult(ActionOutcome.Done);
            Ask();
            Answered(view.Leave.Save);
            Assert.Equal((1, null, "slots 3"), (view.Rail.SelectedIndex, first.Edits, first.Saved));
            Assert.Same(second, shell.Page);
            Assert.Null(first.SaveFailure);
            Assert.Equal((1, 1), (first.HiddenCount, second.ShownCount));
            Assert.Contains(shell.Activity, entry => entry.ToString().EndsWith("Save Queue", StringComparison.Ordinal) && entry.Severity == LogSeverity.Info);
        }, make: () =>
        {
            (var shell, first, second, _) = Shells.WithTestPages();
            return shell;
        });
    }

    [Theory]
    [MemberData(nameof(Ways))]
    public void Discard_drops_the_edits_and_leaves_and_a_page_with_no_edits_is_left_without_a_question(string way)
    {
        TestPage first = null!, second = null!;
        ShellSupport.InShell(null, (view, shell, host) =>
        {
            first.Edits = "slots 3";
            MoveToSecond(view, way);
            PageRenderer.Settle(host);
            Assert.True(shell.IsAsking);
            ShellSupport.Invoke(view.Leave.Discard);
            Assert.True(StartupSupport.Pump(() => ReferenceEquals(shell.Page, second)), "Discard did not leave the page");
            PageRenderer.Settle(host);
            Assert.Equal((1, null, null), (view.Rail.SelectedIndex, first.Edits, first.Saved));
            Assert.Equal((1, 0), (first.DiscardCount, first.SaveCount));

            // Back to the first page, which now has nothing unsaved: no question, by any way.
            view.Rail.SelectedIndex = 0;
            PageRenderer.Settle(host);
            Assert.False(shell.IsAsking);
            Assert.Same(first, shell.Page);
            switch (way)
            {
                case "click":
                    Assert.False(ShellSupport.Press(view.Rail, 1));
                    break;
                case "key":
                    Assert.False(ShellSupport.KeyOn(view.Rail, 0, Key.Down));
                    break;
                default:
                    ShellSupport.Select(view.Rail, 1);
                    PageRenderer.Settle(host);
                    Assert.Same(second, shell.Page);
                    break;
            }
            Assert.False(shell.IsAsking);
        }, make: () =>
        {
            (var shell, first, second, _) = Shells.WithTestPages();
            return shell;
        });
    }

    [Fact]
    public void A_move_asked_for_while_a_save_is_still_running_is_refused_without_a_second_question()
    {
        TestPage first = null!, second = null!, third = null!;
        ShellSupport.InShell(null, (view, shell, host) =>
        {
            var saving = new TaskCompletionSource<ActionOutcome>();
            first.Edits = "slots 3";
            first.OnSave = () => saving.Task;

            Assert.True(ShellSupport.Press(view.Rail, 1));
            PageRenderer.Settle(host);
            ShellSupport.Invoke(view.Leave.Save);
            Assert.True(StartupSupport.Pump(() => first.SaveCount == 1), "the save never started");
            Assert.True(shell.IsLeaving);
            Assert.False(shell.IsAsking);

            // Another row while the save runs: refused, nothing asked, nothing saved twice.
            Assert.True(ShellSupport.Press(view.Rail, 2));
            PageRenderer.Settle(host);
            Assert.False(shell.IsAsking);
            Assert.Equal((0, 1), (view.Rail.SelectedIndex, first.SaveCount));
            Assert.Same(first, shell.Page);

            saving.SetResult(ActionOutcome.Done);
            Assert.True(StartupSupport.Pump(() => ReferenceEquals(shell.Page, second)), "the move the save was for never went on");
            PageRenderer.Settle(host);
            Assert.Equal(1, view.Rail.SelectedIndex); // the move that was asked first, not the one refused
            Assert.Equal(0, third.ShownCount);
        }, make: () =>
        {
            (var shell, first, second, third) = Shells.WithTestPages();
            return shell;
        });
    }

    [Fact]
    public void While_a_save_is_running_nothing_gets_past_it_even_once_the_page_has_cleared_its_edits()
    {
        // Round 1: a page that clears its edits as its save begins looked clean to a second move, which went through
        // and was then undone when the first one finished.
        TestPage first = null!, second = null!, third = null!;
        ShellSupport.InShell(null, (view, shell, host) =>
        {
            var saving = new TaskCompletionSource<ActionOutcome>();
            first.Edits = "slots 3";
            first.OnSave = () =>
            {
                first.Edits = null; // cleared at once; the save itself is still on its way
                return saving.Task;
            };
            var window = new FakeWindow();
            var exit = new ExitGuard(shell, window);

            Assert.True(ShellSupport.Press(view.Rail, 1));
            PageRenderer.Settle(host);
            ShellSupport.Invoke(view.Leave.Save);
            Assert.True(StartupSupport.Pump(() => first.SaveCount == 1), "the save never started");
            Assert.True(shell.IsLeaving);
            Assert.False(first.HasUnsavedEdits);

            // By the rail, from code, and by closing the window: each is refused, and nothing is asked.
            Assert.True(ShellSupport.Press(view.Rail, 2));
            Assert.True(ShellSupport.KeyOn(view.Rail, 0, Key.End));
            var fromCode = shell.GoToAsync(shell.Sections[2]);
            Assert.True(fromCode.IsCompleted);
            Assert.False(fromCode.Result);
            Assert.False(exit.MayCloseNow());
            PageRenderer.Settle(host);
            Assert.Same(first, shell.Page);
            Assert.False(shell.IsAsking);
            Assert.Equal((0, 0), (window.Closed, third.ShownCount));

            saving.SetResult(ActionOutcome.Done);
            Assert.True(StartupSupport.Pump(() => ReferenceEquals(shell.Page, second)), "the move the save was for never went on");
            PageRenderer.Settle(host);
            Assert.Equal((1, 0), (view.Rail.SelectedIndex, third.ShownCount));
            Assert.True(exit.MayCloseNow()); // and now that it is over, the window may close
        }, make: () =>
        {
            (var shell, first, second, third) = Shells.WithTestPages();
            return shell;
        });
    }

    [Fact]
    public void A_move_from_code_asks_the_same_question()
    {
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var (shell, first, second, _) = Shells.WithTestPages();
            first.Edits = "slots 3";

            var going = shell.GoToAsync(shell.Sections[1]);
            Assert.True(shell.IsAsking);
            Assert.Equal(new LeaveQuestion("Queue"), shell.Asking);
            shell.Answer(LeaveChoice.Stay);
            Assert.True(StartupSupport.Pump(() => going.IsCompleted));
            Assert.False(going.Result);
            Assert.Same(first, shell.Page);

            going = shell.GoToAsync(shell.Sections[1]);
            shell.Answer(LeaveChoice.Discard);
            Assert.True(StartupSupport.Pump(() => going.IsCompleted));
            Assert.True(going.Result);
            Assert.Same(second, shell.Page);
            Assert.True(shell.GoToAsync(shell.Sections[1]).Result); // already there
        });
    }

    // ---- the question itself ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_question_has_three_buttons_and_each_gives_its_own_choice(string theme)
    {
        TestPage first = null!;
        ShellSupport.InShell(null, (view, shell, host) =>
        {
            // Each button is pressed with the question up, and what the shell then does says which choice it gave.
            first.OnSave = () => Task.FromResult(ActionOutcome.Failed("not now")); // so a Save is seen and the page is kept
            bool Press(Button button)
            {
                first.Edits = "slots 3";
                var leaving = shell.LeaveAsync();
                PageRenderer.Settle(host);
                Assert.True(view.Leave.IsVisible);
                Assert.Equal(new LeaveQuestion("Queue"), shell.Asking);
                ShellSupport.Invoke(button);
                Assert.True(StartupSupport.Pump(() => leaving.IsCompleted), $"{button.Content} gave no answer");
                PageRenderer.Settle(host);
                Assert.False(view.Leave.IsVisible);
                return leaving.Result;
            }

            Assert.False(Press(view.Leave.Keep));   // Keep editing: nothing saved, nothing dropped, not left
            Assert.Equal((0, 0, "slots 3"), (first.SaveCount, first.DiscardCount, first.Edits));
            Assert.False(Press(view.Leave.Save));   // Save: the page's save ran (and failed here), not left
            Assert.Equal((1, 0, "slots 3"), (first.SaveCount, first.DiscardCount, first.Edits));
            Assert.True(Press(view.Leave.Discard)); // Discard: the edits are dropped, and the page may be left
            Assert.Equal((1, 1, null), (first.SaveCount, first.DiscardCount, first.Edits));

            Assert.Equal(["Keep editing", "Discard", "Save"], new[] { view.Leave.Keep, view.Leave.Discard, view.Leave.Save }.Select(b => (string)b.Content));
            Assert.True(view.Leave.Save.IsDefault); // Enter saves
            Assert.True(view.Leave.Keep.IsCancel);  // Esc keeps editing
            Assert.Same(host.FindResource("Btn.Primary"), view.Leave.Save.Style);
            Assert.Same(host.FindResource("Btn.Danger"), view.Leave.Discard.Style);
            Assert.Same(host.FindResource("Btn"), view.Leave.Keep.Style);
        }, theme, () =>
        {
            (var shell, first, _, _) = Shells.WithTestPages();
            return shell;
        });
    }
}

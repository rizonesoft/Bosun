using System.IO;
using System.Windows;
using System.Windows.Controls;
using Bosun.Core;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The Activity pane's log (D02 T02 §4, design/SPEC.md §1): every line starts with INFO, WARN or FAIL, every action
/// writes one, and an action whose tool failed writes FAIL with the tool's reason, though the tool never throws.
/// </summary>
public class ActivityLogTests
{
    private static readonly ProcessRunner Runner = new();

    private static string Child() => StartupSupport.Built("Bosun.TestChild", "Bosun.TestChild.exe");

    [Fact]
    public void Each_kind_of_line_has_its_word_and_a_line_that_carries_its_own_word_is_read_by_it()
    {
        Sta.Run(() =>
        {
            var log = Shells.Log();
            log.Info("Panel ready");
            log.Warn("WSL is not running");
            log.Fail("Save queue limits: slots must be between 1 and 8");
            log.Write("INFO Recto took slot 1");
            log.Write("WARN the queue file was repaired");
            log.Write("FAIL bosun: no such file");

            Assert.Equal(["INFO", "WARN", "FAIL", "INFO", "WARN", "FAIL"], log.Entries.Select(entry => entry.Tag));
            Assert.Equal(["Panel ready", "WSL is not running", "Save queue limits: slots must be between 1 and 8", "Recto took slot 1", "the queue file was repaired", "bosun: no such file"],
                log.Entries.Select(entry => entry.Message));
            // As a screen reader says a line: the word first.
            Assert.Equal("WARN 09:11:00 WSL is not running", log.Entries[1].ToString());
        });
    }

    [Theory]
    [InlineData("Recto took slot 1")]
    [InlineData("info lower case")]
    [InlineData("WARN")]
    [InlineData("WARN:no space")]
    [InlineData("  WARN leading spaces")]
    [InlineData("")]
    public void A_line_that_does_not_start_with_its_word_is_refused_and_nothing_is_added(string line)
    {
        Sta.Run(() =>
        {
            var log = Shells.Log();
            Assert.Throws<ArgumentException>(() => log.Write(line));
            Assert.Empty(log.Entries);
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void A_line_is_drawn_word_first_then_time_then_message_with_warn_and_fail_in_their_colours(string theme)
    {
        ListBox pane = null!;
        PageRenderer.InPage(theme, () =>
        {
            var log = Shells.Log();
            log.Info("Panel ready");
            log.Warn("WSL is not running");
            log.Fail("bosun: no such file");
            pane = new ListBox { ItemsSource = log.Entries };
            pane.SetResourceReference(FrameworkElement.StyleProperty, "LogBox");
            return pane;
        }, host =>
        {
            Assert.Equal(["INFO", "09:10:00", "Panel ready", "WARN", "09:11:00", "WSL is not running", "FAIL", "09:12:00", "bosun: no such file"], PageRenderer.TextsOf(pane));

            System.Windows.Media.Color Token(string name) => ((System.Windows.Media.SolidColorBrush)host.FindResource("B." + name)).Color;
            var words = ShellSupport.Under<TextBlock>(pane).Where(text => text.Name == "word").ToList();
            Assert.Equal(Token("TextDim"), ShellSupport.Paint(words[0].Foreground));
            Assert.Equal(Token("Warn"), ShellSupport.Paint(words[1].Foreground));
            Assert.Equal(Token("Danger"), ShellSupport.Paint(words[2].Foreground));
            Assert.All(ShellSupport.Under<TextBlock>(pane), text => Assert.Same(host.FindResource("F.Mono"), text.FontFamily));
            return true;
        }, 900, 300);
    }

    // ---- how an action that ran a tool ended ----

    private static async Task<ActionOutcome> Run(ProcessRequest request) =>
        ActionOutcome.From(request, await Runner.RunAsync(request, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_tool_that_exits_0_is_done_and_one_that_fails_gives_its_reason_without_throwing()
    {
        var done = await Run(new ProcessRequest(Child(), ["exit", "0"]));
        Assert.True(done.Succeeded);
        Assert.Null(done.Reason);

        // What the tool said on stderr is the reason: it is the message a failed state quotes.
        var said = await Run(new ProcessRequest(Child(), ["print", "", "bosun: the queue file is damaged\nat line 3", "3"]));
        Assert.False(said.Succeeded);
        Assert.Equal("bosun: the queue file is damaged", said.Reason);

        var silent = await Run(new ProcessRequest(Child(), ["exit", "3"]));
        Assert.False(silent.Succeeded);
        Assert.Equal("Bosun.TestChild exited with code 3", silent.Reason);

        var slow = await Run(new ProcessRequest(Child(), ["sleep", "20000"]) { Timeout = TimeSpan.FromMilliseconds(300) });
        Assert.False(slow.Succeeded);
        Assert.Equal("Bosun.TestChild did not finish within 0.3 s and was ended", slow.Reason);

        var missing = await Run(new ProcessRequest(Path.Combine(Path.GetTempPath(), "bosun-no-such-tool-" + Guid.NewGuid().ToString("N") + ".exe"), []));
        Assert.False(missing.Succeeded);
        Assert.StartsWith("bosun-no-such-tool-", missing.Reason);
        Assert.Contains(" could not be started", missing.Reason);
    }

    [Fact]
    public void An_outcome_needs_words_when_it_has_any()
    {
        Assert.Throws<ArgumentException>(() => ActionOutcome.Failed(" "));
        Assert.Throws<ArgumentException>(() => ActionOutcome.DoneWith(""));
        Assert.Equal("slots 3", ActionOutcome.DoneWith(" slots 3 ").Reason);
    }

    // ---- every action writes its line ----

    [Fact]
    public void A_command_writes_info_when_done_and_fail_with_the_reason_when_its_tool_failed_or_it_threw()
    {
        var child = Child();
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var log = Shells.Log();

            var done = log.Command("Refresh memory", () => Task.FromResult(ActionOutcome.Done));
            done.Execute();
            Assert.True(StartupSupport.Pump(() => log.Entries.Count == 1 && !done.IsRunning));
            Assert.Equal("INFO 09:10:00 Refresh memory", log.Entries[0].ToString());

            var detailed = log.Command("Save queue limits", () => Task.FromResult(ActionOutcome.DoneWith("slots 3")));
            detailed.Execute();
            Assert.True(StartupSupport.Pump(() => log.Entries.Count == 2));
            Assert.Equal("INFO 09:11:00 Save queue limits: slots 3", log.Entries[1].ToString());

            // A real tool that exits nonzero: no exception anywhere, and the line is FAIL with what the tool said.
            var request = new ProcessRequest(child, ["print", "", "bosun: slots must be between 1 and 8", "64"]);
            var failed = log.Command("Save queue limits", async () => ActionOutcome.From(request, await Runner.RunAsync(request)));
            failed.Execute();
            Assert.True(StartupSupport.Pump(() => log.Entries.Count == 3 && !failed.IsRunning, TimeSpan.FromSeconds(30)), "the failed tool wrote no line");
            Assert.Equal((LogSeverity.Fail, "Save queue limits: bosun: slots must be between 1 and 8"), (log.Entries[2].Severity, log.Entries[2].Message));

            var threw = log.Command("Open folder", () => throw new InvalidOperationException("the folder is gone"));
            threw.Execute();
            Assert.True(StartupSupport.Pump(() => log.Entries.Count == 4 && !threw.IsRunning));
            Assert.Equal((LogSeverity.Fail, "Open folder: the folder is gone"), (log.Entries[3].Severity, log.Entries[3].Message));

            // And each can run again: one line per run.
            failed.Execute();
            threw.Execute();
            Assert.True(StartupSupport.Pump(() => log.Entries.Count == 6, TimeSpan.FromSeconds(30)));
            Assert.Equal([LogSeverity.Fail, LogSeverity.Fail], log.Entries.Skip(4).Select(entry => entry.Severity));

            Assert.Throws<ArgumentException>(() => log.Command(" ", () => Task.FromResult(ActionOutcome.Done)));
        });
    }
}

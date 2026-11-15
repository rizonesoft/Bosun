using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Bosun.Panel.Shell;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// A page of the tests' own (D02 T02 §4): it can be put in any of the four states, can hold unsaved edits, and counts
/// what the shell did to it. No real page has edits yet; this one stands where they will.
/// </summary>
public sealed class TestPage(string title = "Queue", string summary = "Who holds the build slots.") : PageBase(title, summary)
{
    private string? _edits;

    /// <summary>What the page shows in its data state.</summary>
    public string Body { get; set; } = "two slots, one waiting";

    /// <summary>What the person typed and has not saved; null when there is nothing unsaved.</summary>
    public string? Edits
    {
        get => _edits;
        set => Set(ref _edits, value);
    }

    /// <summary>What was last saved.</summary>
    public string? Saved { get; private set; }

    /// <summary>How a save ends. Done by default.</summary>
    public Func<Task<ActionOutcome>> OnSave { get; set; } = () => Task.FromResult(ActionOutcome.Done);

    public int ShownCount { get; private set; }
    public int HiddenCount { get; private set; }
    public int SaveCount { get; private set; }
    public int DiscardCount { get; private set; }
    public int RetryCount { get; private set; }

    public override bool HasUnsavedEdits => _edits is not null;

    public override async Task<ActionOutcome> SaveAsync()
    {
        SaveCount++;
        var outcome = await OnSave();
        if (!outcome.Succeeded) return outcome;
        Saved = _edits;
        Edits = null;
        return outcome;
    }

    public override void Discard()
    {
        DiscardCount++;
        Edits = null;
    }

    protected override void Shown() => ShownCount++;

    protected override void Hidden() => HiddenCount++;

    public TestPage Loading()
    {
        ShowLoading();
        return this;
    }

    public TestPage Data()
    {
        ShowData();
        return this;
    }

    public TestPage Empty(string reason = "Nothing is waiting.", string action = "Start a build with bosun run to see it here.")
    {
        ShowEmpty(reason, action);
        return this;
    }

    public TestPage Failed(string message = "bosun: the queue file is damaged at line 3", ActivityLog? log = null)
    {
        Retry = (log ?? Shells.Log()).Command("Retry " + Title, () =>
        {
            RetryCount++;
            return Task.FromResult(ActionOutcome.Done);
        });
        ShowFailed(message);
        return this;
    }

    /// <summary>What the page's refresh reads: the body to show. A test ends each call as it chooses.</summary>
    public Func<CancellationToken, Task<string>> OnLoad { get; set; } = _ => Task.FromResult("fresh");

    /// <summary>Gives the page a refresh, as a real page does in its constructor: each run shows what was read.</summary>
    public TestPage Polling(Bosun.Core.RefreshScheduler scheduler, ActivityLog log, TimeSpan? interval = null)
    {
        RefreshEvery(scheduler, log, interval ?? Bosun.Core.RefreshScheduler.Fast, async token =>
        {
            var read = await OnLoad(token);
            return () =>
            {
                Body = read;
                Raise(nameof(Body));
                ShowData();
            };
        });
        return this;
    }

    /// <summary>The failed state with no Retry set: what a page must not do.</summary>
    public void FailedWithoutRetry() => ShowFailed("no retry");

    /// <summary>How the frame draws this page's data: its body, through the implicit template for its type.</summary>
    public static void AddTemplate(FrameworkElement to)
    {
        var body = new FrameworkElementFactory(typeof(TextBlock));
        body.SetBinding(TextBlock.TextProperty, new Binding(nameof(Body)));
        body.SetValue(FrameworkElement.NameProperty, "TestPageBody");
        to.Resources.Add(new DataTemplateKey(typeof(TestPage)), new DataTemplate(typeof(TestPage)) { VisualTree = body });
    }
}

/// <summary>Shells for the tests and the renders, made on the thread that will draw them.</summary>
public static class Shells
{
    /// <summary>An activity log on this thread, its clock fixed so two renders of it are the same picture.</summary>
    public static ActivityLog Log()
    {
        var second = 0;
        return new ActivityLog(new UiLogSink(Dispatcher.CurrentDispatcher, () => new DateTime(2026, 11, 15, 9, 10, 0, DateTimeKind.Local).AddMinutes(second++)));
    }

    /// <summary>The panel's rail with every page a placeholder, and a line of each kind in the pane: the frame itself.</summary>
    public static ShellViewModel Panel()
    {
        var log = Log();
        var shell = new ShellViewModel(Sections.All(), log);
        log.Info("Panel ready");
        log.Warn("WSL is not running: its rows show the last data, 4 minutes old");
        log.Fail("Save queue limits: slots must be between 1 and 8");
        return shell;
    }

    /// <summary>A shell whose first page is a test page, followed by two more, for the leave flow.</summary>
    public static (ShellViewModel Shell, TestPage First, TestPage Second, TestPage Third) WithTestPages()
    {
        var first = new TestPage("Queue", "Who holds the build slots.").Data();
        var second = new TestPage("Memory", "Live headroom.").Data();
        var third = new TestPage("Settings", "How the panel starts.").Data();
        var shell = new ShellViewModel(
        [
            new Section("queue", "Queue", Sections.Monitor, first),
            new Section("memory", "Memory", Sections.Monitor, second),
            new Section("settings", "Settings", Sections.App, third),
        ], Log());
        return (shell, first, second, third);
    }
}

using System.Collections.ObjectModel;
using Bosun.Panel.Mvvm;

namespace Bosun.Panel.Shell;

/// <summary>What the person chose when asked about a page's unsaved edits.</summary>
public enum LeaveChoice
{
    /// <summary>Save the edits, and leave if that worked.</summary>
    Save,

    /// <summary>Drop the edits and leave.</summary>
    Discard,

    /// <summary>Keep editing: stay on the page.</summary>
    Stay,
}

/// <summary>The question put when a page with unsaved edits is being left: which page it is.</summary>
public sealed record LeaveQuestion(string PageTitle);

/// <summary>
/// The shell's state (D02 T02 §4): the rail's sections, the one selected, its page, and the activity lines. It also
/// holds the one flow for leaving a page that has unsaved edits, whether to another page or out of the panel
/// (<see cref="LeaveAsync"/>), so the rail, the window's close and the tray's Exit all keep the same promise.
/// </summary>
public sealed class ShellViewModel : Observable
{
    public const double DefaultActivityHeight = 132, MinActivityHeight = 64, MaxActivityHeight = 400;

    private Section _selected;
    private LeaveQuestion? _asking;
    private TaskCompletionSource<LeaveChoice>? _answer;
    private bool _leaving;
    private double _activityHeight = DefaultActivityHeight;

    public ShellViewModel(IReadOnlyList<Section> sections, ActivityLog log)
    {
        if (sections.Count == 0) throw new ArgumentException("the rail needs at least one section", nameof(sections));
        Sections = sections;
        Log = log;
        AskToLeave = AskInTheShell;
        _selected = sections[0];
        _selected.Page.Show();
    }

    public IReadOnlyList<Section> Sections { get; }

    public ActivityLog Log { get; }

    public ObservableCollection<LogEntry> Activity => Log.Entries;

    /// <summary>The section shown. Moved by <see cref="Show"/> (the rail, once its guard has agreed) or <see cref="GoToAsync"/>.</summary>
    public Section Selected => _selected;

    /// <summary>The page shown.</summary>
    public PageBase Page => _selected.Page;

    /// <summary>The Activity pane's height, kept within its limits (design/SPEC.md §1: 132 px, resizable).</summary>
    public double ActivityHeight
    {
        get => _activityHeight;
        set => Set(ref _activityHeight, Math.Clamp(value, MinActivityHeight, MaxActivityHeight));
    }

    /// <summary>
    /// Shows a section: the page being left is told it is hidden, the new one that it is shown. Navigation is not an
    /// action, so nothing is logged. This does not ask about unsaved edits: the rail's guard has, before it calls.
    /// </summary>
    public void Show(Section section)
    {
        if (ReferenceEquals(section, _selected)) return;
        if (!Sections.Contains(section)) throw new ArgumentException("not a section of this shell", nameof(section));
        _selected.Page.Hide();
        _selected = section;
        Raise(nameof(Selected));
        Raise(nameof(Page));
        _selected.Page.Show();
    }

    /// <summary>Goes to a section from code (a page's Open, the palette): asks about unsaved edits first; false when the person stayed.</summary>
    public async Task<bool> GoToAsync(Section section)
    {
        if (ReferenceEquals(section, _selected)) return true;
        if (!await LeaveAsync()) return false;
        Show(section);
        return true;
    }

    // ---- leaving a page with unsaved edits ----

    /// <summary>How the person is asked. The shell's own dialog by default (<see cref="Asking"/>).</summary>
    public Func<LeaveQuestion, Task<LeaveChoice>> AskToLeave { get; set; }

    /// <summary>The question on screen, when one is; the shell's view draws its dialog while this is set.</summary>
    public LeaveQuestion? Asking
    {
        get => _asking;
        private set
        {
            if (Set(ref _asking, value)) Raise(nameof(IsAsking));
        }
    }

    public bool IsAsking => _asking is not null;

    /// <summary>True from the question until the save, if one was chosen, has ended.</summary>
    public bool IsLeaving => _leaving;

    private Task<LeaveChoice> AskInTheShell(LeaveQuestion question)
    {
        _answer = new TaskCompletionSource<LeaveChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        Asking = question;
        return _answer.Task;
    }

    /// <summary>The dialog's answer: one of its three buttons was pressed.</summary>
    public void Answer(LeaveChoice choice)
    {
        Asking = null;
        Interlocked.Exchange(ref _answer, null)?.TrySetResult(choice);
    }

    /// <summary>
    /// Whether the page shown may be left now. A page with no unsaved edits may. One with edits asks the person:
    /// staying keeps the page and its edits; Discard drops them; Save runs the page's own save, and only a save that
    /// was done lets the move go on. A save that failed, or threw, keeps the page and its edits with the reason shown
    /// on it. While one question or save is under way, another move is refused without a second question.
    /// </summary>
    public async Task<bool> LeaveAsync()
    {
        var page = Page;
        if (!page.HasUnsavedEdits) return true;
        if (_leaving) return false;
        _leaving = true;
        try
        {
            switch (await AskToLeave(new LeaveQuestion(page.Title)))
            {
                case LeaveChoice.Discard:
                    page.Discard();
                    page.SaveFailure = null;
                    return true;
                case LeaveChoice.Save:
                    ActionOutcome outcome;
                    try
                    {
                        outcome = await page.SaveAsync();
                    }
                    // Whatever the page's save throws: the person asked to leave, and what they must hear is that
                    // their edits were not saved and are still here, with why.
                    catch (Exception e)
                    {
                        outcome = ActionOutcome.Failed(e.Message);
                    }
                    Log.Record($"Save {page.Title}", outcome);
                    page.SaveFailure = outcome.Succeeded ? null : $"Not saved: {outcome.Reason}";
                    return outcome.Succeeded;
                default:
                    return false;
            }
        }
        finally
        {
            _leaving = false;
        }
    }
}

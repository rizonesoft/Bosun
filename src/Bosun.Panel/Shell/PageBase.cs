using System.Globalization;
using System.Windows.Input;
using Bosun.Core;
using Bosun.Panel.Controls;
using Bosun.Panel.Mvvm;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Shell;

/// <summary>The four states every page draws (design/SPEC.md §1).</summary>
public enum PageState
{
    /// <summary>Its data is on the way.</summary>
    Loading,

    /// <summary>It shows its data.</summary>
    Data,

    /// <summary>There is nothing to show: it says why, and what to do.</summary>
    Empty,

    /// <summary>The tool failed: it quotes the tool's message and offers Retry.</summary>
    Failed,
}

/// <summary>
/// A page of the panel (D02 T02 §4): its title and summary for the header, which of the four states it is in, whether
/// it holds unsaved edits, and when it is on screen. A page that is hidden polls nothing: the shell says when it
/// comes and goes through <see cref="Shown"/> and <see cref="Hidden"/>.
/// </summary>
public abstract class PageBase(string title, string summary) : Observable
{
    private PageState _state = PageState.Loading;
    private string? _emptyReason, _emptyAction, _failureMessage, _saveFailure;
    private bool _isShown;
    private ICommand? _retry, _refreshNow;
    private object? _actions;
    private Refresh? _refresh;
    private readonly List<Refresh> _also = [];

    /// <summary>How many reads a page may hold: its own and two more (D03 T01 §2 and §3).</summary>
    public const int MaxRefreshes = 3;
    private bool _refreshFailing;
    private string? _staleNote, _staleWhy;
    private DateTimeOffset _staleAt;
    private int _waiting, _needsYou;
    private object? _keptWhenFailed;

    public string Title { get; } = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("a page needs a title", nameof(title)) : title;

    /// <summary>The line under the title: what the page is for.</summary>
    public string Summary { get; } = summary ?? "";

    /// <summary>
    /// How many things on this page wait for the person (D02 T03 §3, design/SPEC.md §19: a rail row shows a count only
    /// when its page has something waiting): its rail row's badge, and its spoken name, follow it. 0 by default; a page
    /// sets its own (the queue its waiting jobs, D03 T02 §3). Never below 0.
    /// </summary>
    public int Waiting
    {
        get => _waiting;
        set
        {
            if (Set(ref _waiting, Math.Max(0, value))) Raise(nameof(Badge));
        }
    }

    /// <summary>
    /// How many rows of the Overview's Needs you table this page owns (D03 T01 §5): set by the shell from the table, so
    /// a page's rail badge counts what needs the person there as well as what waits. Never below 0.
    /// </summary>
    public int NeedsYou
    {
        get => _needsYou;
        set
        {
            if (Set(ref _needsYou, Math.Max(0, value))) Raise(nameof(Badge));
        }
    }

    /// <summary>The rail badge's count: what waits on this page and what needs the person there (design/SPEC.md §19).</summary>
    public int Badge => _waiting + _needsYou;

    /// <summary>
    /// What the page keeps on screen in its failed state, above the tool's message (D03 T01 §5): something that must stay
    /// reachable when the page's data can't be read, as the Overview's Needs you table keeps active pressure reachable.
    /// Drawn by the frame through the implicit template for its type; null keeps nothing.
    /// </summary>
    public object? KeptWhenFailed
    {
        get => _keptWhenFailed;
        protected set => Set(ref _keptWhenFailed, value);
    }

    public PageState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>
    /// The shape of the page's data, which its loading state's skeleton takes (D02 T03 §7, design/SPEC.md §19): cards
    /// unless the page says otherwise.
    /// </summary>
    public SkeletonShape LoadingShape { get; init; } = SkeletonShape.Card;

    /// <summary>The page's clock, for the age of the data it shows. The system's own unless a test gives one.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// Said over the data when it is not current (a tool that has stopped): what happened and how old the data is,
    /// as <see cref="ShowLastKnown"/> set it; null when the data is current.
    /// </summary>
    public string? StaleNote
    {
        get => _staleNote;
        private set => Set(ref _staleNote, value);
    }

    /// <summary>Why there is nothing to show, in the empty state.</summary>
    public string? EmptyReason
    {
        get => _emptyReason;
        private set => Set(ref _emptyReason, value);
    }

    /// <summary>What to do about it, in the empty state.</summary>
    public string? EmptyAction
    {
        get => _emptyAction;
        private set => Set(ref _emptyAction, value);
    }

    /// <summary>The tool's own message, in the failed state.</summary>
    public string? FailureMessage
    {
        get => _failureMessage;
        private set => Set(ref _failureMessage, value);
    }

    /// <summary>
    /// What the failed state's Retry button runs. A page that can fail sets it before it does; it says when it
    /// changes, since a page is usually on screen, bound, before it first fails.
    /// </summary>
    public ICommand? Retry
    {
        get => _retry;
        protected set => Set(ref _retry, value);
    }

    /// <summary>The page's own actions, shown at the right of its header; a view model the page's templates draw.</summary>
    public object? Actions
    {
        get => _actions;
        protected set => Set(ref _actions, value);
    }

    // ---- refreshing while on screen ----

    /// <summary>
    /// Runs the page's refresh now: the header's Refresh button, and Retry in the failed state. Null for a page that
    /// has no refresh, which has no button.
    /// </summary>
    public ICommand? RefreshNow
    {
        get => _refreshNow;
        private set => Set(ref _refreshNow, value);
    }

    /// <summary>
    /// What the command palette lists for this page while it is on screen (D02 T03 §8): Refresh when it has one, Retry
    /// while it has failed, then the page's own <see cref="Commands"/>, each the command its button runs.
    /// </summary>
    public IReadOnlyList<PageCommand> PaletteCommands
    {
        get
        {
            var list = new List<PageCommand>();
            if (RefreshNow is { } refresh) list.Add(new PageCommand("Refresh", GlyphAction.Refresh, refresh));
            if (State == PageState.Failed && Retry is { } retry) list.Add(new PageCommand("Retry", GlyphAction.Refresh, retry));
            list.AddRange(Commands);
            return list;
        }
    }

    /// <summary>The page's own actions as its buttons offer them now; none by default.</summary>
    protected virtual IEnumerable<PageCommand> Commands => [];

    /// <summary>
    /// Gives the page a refresh (D02 T02 §8, design/SPEC.md §1): it runs when the page comes on screen and then
    /// every <paramref name="interval"/>, and stops when the page leaves the screen, the window hidden or minimised
    /// included. A hidden page polls nothing.
    ///
    /// <paramref name="load"/> reads the tool and touches nothing of the page: it returns what to show, which is
    /// applied here, on the UI thread, and only for a run that was not stopped, so a run that ends after the page
    /// was hidden can't put old data on it. What it throws is the page's failed state, with its message and Retry;
    /// the next refresh that works takes the page out of it. The Activity pane says once when it starts failing,
    /// and once when it works again.
    /// </summary>
    protected void RefreshEvery(RefreshScheduler scheduler, ActivityLog log, TimeSpan interval, Func<CancellationToken, Task<Action>> load)
    {
        if (_refresh is not null) throw new InvalidOperationException("a page has one refresh");
        Action? show = null;
        var refresh = scheduler.Register(Title, interval, async token =>
        {
            show = null;
            show = await load(token);
        });
        var command = log.Command($"Refresh {Title}", async () =>
        {
            // The page's other reads (RefreshAlso) run beside the first: Refresh, Retry and the palette read them all.
            var also = _also.Select(a => a.RunNowAsync()).ToList();
            var outcome = await refresh.RunNowAsync();
            await Task.WhenAll(also);
            // Stopped because the page left the screen: the tool did not fail, and the line does not say it did.
            if (outcome == RefreshOutcome.Stale || outcome == RefreshOutcome.NotStarted) return ActionOutcome.DoneWith("stopped, the page is not on screen");
            return outcome.Succeeded ? ActionOutcome.Done : ActionOutcome.Failed(outcome.Message ?? "It did not refresh.");
        });
        refresh.Finished += outcome =>
        {
            if (outcome.Succeeded)
            {
                show?.Invoke();
                if (!_refreshFailing) return;
                _refreshFailing = false;
                log.Info($"{Title} is refreshing again");
                return;
            }
            ShowFailed(outcome.Message ?? "It did not refresh.");
            if (_refreshFailing) return;
            _refreshFailing = true;
            log.Warn($"{Title} could not be refreshed: {outcome.Message} Trying again in {InWords(outcome.NextIn)}, and less often while it keeps failing.");
        };
        RefreshNow = command;
        Retry ??= command;
        _refresh = refresh;
        if (IsShown) refresh.Start();
    }

    /// <summary>
    /// Another read on its own interval (D03 T01 §2: the Memory page reads its snapshots every 5 s and its day report
    /// every 30 s; §3: its fortnight's report every 5 minutes), registered after <see cref="RefreshEvery"/>, up to
    /// <see cref="MaxRefreshes"/> in all. Each starts and stops with the page, its work cancelled and a late result
    /// dropped when the page leaves the screen, and the page's Refresh, Retry and palette Refresh run every one. None
    /// fails the page: <paramref name="load"/> returns how to show what it read, its own failure included, so a slow or
    /// failing source never holds back or replaces another.
    /// </summary>
    protected Refresh RefreshAlso(RefreshScheduler scheduler, string name, TimeSpan interval, Func<CancellationToken, Task<Action>> load)
    {
        if (_refresh is null) throw new InvalidOperationException("RefreshAlso comes after RefreshEvery");
        if (_also.Count + 1 >= MaxRefreshes) throw new InvalidOperationException($"a page has at most {MaxRefreshes} refreshes");
        Action? show = null;
        var also = scheduler.Register($"{Title}: {name}", interval, async token =>
        {
            show = null;
            show = await load(token);
        });
        also.Finished += outcome =>
        {
            if (outcome.Succeeded) show?.Invoke();
        };
        _also.Add(also);
        if (IsShown) also.Start();
        return also;
    }

    private static string InWords(TimeSpan wait) => wait.TotalSeconds < 120
        ? string.Create(CultureInfo.InvariantCulture, $"{wait.TotalSeconds:0} s")
        : string.Create(CultureInfo.InvariantCulture, $"{wait.TotalMinutes:0} min");

    protected void ShowLoading() => State = PageState.Loading;

    /// <summary>The page shows its data, which is current: a stale line from <see cref="ShowLastKnown"/> goes.</summary>
    protected void ShowData()
    {
        _staleWhy = null;
        StaleNote = null;
        State = PageState.Data;
    }

    /// <summary>
    /// The page shows the last data it read, which is no longer current (WSL stopped, D02 T03 §7): the data stays, and
    /// the frame says over it <c>&lt;why&gt;. Showing what was read &lt;age&gt; ago.</c> The age is worked out again on each
    /// call (each refresh), when the page comes back on screen and when the person comes back to the window, with no
    /// timer of its own, so a hidden page does nothing.
    /// </summary>
    protected void ShowLastKnown(string why, DateTimeOffset readAt)
    {
        if (string.IsNullOrWhiteSpace(why)) throw new ArgumentException("stale data says why it is not current", nameof(why));
        _staleWhy = why.Trim().TrimEnd('.');
        _staleAt = readAt;
        SayAge();
        State = PageState.Data;
    }

    private void SayAge()
    {
        if (_staleWhy is null) return;
        StaleNote = $"{_staleWhy}. Showing what was read {Age(Clock.GetUtcNow() - _staleAt)} ago.";
    }

    /// <summary>An age in words: "less than a minute", "1 minute", "12 minutes", "2 hours", "3 days".</summary>
    public static string Age(TimeSpan age)
    {
        static string Count(long n, string unit) => n == 1 ? $"1 {unit}" : string.Create(CultureInfo.InvariantCulture, $"{n} {unit}s");
        if (age < TimeSpan.FromMinutes(1)) return "less than a minute";
        if (age < TimeSpan.FromHours(1)) return Count((long)age.TotalMinutes, "minute");
        if (age < TimeSpan.FromDays(1)) return Count((long)age.TotalHours, "hour");
        return Count((long)age.TotalDays, "day");
    }

    /// <summary>Nothing to show: it must say why, and what to do (design/SPEC.md §1).</summary>
    protected void ShowEmpty(string reason, string action)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("an empty page says why it is empty", nameof(reason));
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("an empty page says what to do", nameof(action));
        EmptyReason = reason;
        EmptyAction = action;
        State = PageState.Empty;
    }

    /// <summary>The tool failed: its message is quoted, and Retry is offered (design/SPEC.md §1).</summary>
    protected void ShowFailed(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("a failed page quotes the tool's message", nameof(message));
        if (Retry is null) throw new InvalidOperationException("a page that can fail offers Retry: set Retry before ShowFailed");
        FailureMessage = message;
        State = PageState.Failed;
    }

    // ---- unsaved edits ----

    /// <summary>Whether the page holds edits that leaving it would lose.</summary>
    public virtual bool HasUnsavedEdits => false;

    /// <summary>Saves the page's edits; a page with edits overrides it. Only a done save lets the person leave.</summary>
    public virtual Task<ActionOutcome> SaveAsync() => Task.FromResult(ActionOutcome.Done);

    /// <summary>Drops the page's edits; a page with edits overrides it.</summary>
    public virtual void Discard()
    {
    }

    /// <summary>Why the last save, asked for on the way out, did not happen; shown on the page until one does.</summary>
    public string? SaveFailure
    {
        get => _saveFailure;
        internal set => Set(ref _saveFailure, value);
    }

    // ---- on screen or not ----

    public bool IsShown
    {
        get => _isShown;
        private set => Set(ref _isShown, value);
    }

    /// <summary>The shell says the page is on screen. Once per showing.</summary>
    internal void Show()
    {
        if (IsShown) return;
        IsShown = true;
        SayAge(); // back on screen: the stale line says the age as it is now
        Shown();
        _refresh?.Start();
        foreach (var also in _also) also.Start();
    }

    /// <summary>The shell says the page has left the screen. Once per hiding.</summary>
    internal void Hide()
    {
        if (!IsShown) return;
        IsShown = false;
        _refresh?.Stop();
        foreach (var also in _also) also.Stop();
        Hidden();
    }

    /// <summary>The page is on screen: it may start what it only does while looked at.</summary>
    protected virtual void Shown()
    {
    }

    /// <summary>The shell says the person has come back to the panel's window while this page is on screen.</summary>
    internal void ComeBack()
    {
        if (!IsShown) return;
        SayAge();
        CameBack();
    }

    /// <summary>
    /// The person came back to the window with this page on screen: what the page shows of the world outside the
    /// panel (a Windows setting it sent them to change) may be different now.
    /// </summary>
    protected virtual void CameBack()
    {
    }

    /// <summary>The page has left the screen: it stops what it only does while looked at.</summary>
    protected virtual void Hidden()
    {
    }
}

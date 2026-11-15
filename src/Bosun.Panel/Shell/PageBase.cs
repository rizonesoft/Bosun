using System.Globalization;
using System.Windows.Input;
using Bosun.Core;
using Bosun.Panel.Mvvm;

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
    private bool _refreshFailing;

    public string Title { get; } = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("a page needs a title", nameof(title)) : title;

    /// <summary>The line under the title: what the page is for.</summary>
    public string Summary { get; } = summary ?? "";

    public PageState State
    {
        get => _state;
        private set => Set(ref _state, value);
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
            var outcome = await refresh.RunNowAsync();
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

    private static string InWords(TimeSpan wait) => wait.TotalSeconds < 120
        ? string.Create(CultureInfo.InvariantCulture, $"{wait.TotalSeconds:0} s")
        : string.Create(CultureInfo.InvariantCulture, $"{wait.TotalMinutes:0} min");

    protected void ShowLoading() => State = PageState.Loading;

    protected void ShowData() => State = PageState.Data;

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
        Shown();
        _refresh?.Start();
    }

    /// <summary>The shell says the page has left the screen. Once per hiding.</summary>
    internal void Hide()
    {
        if (!IsShown) return;
        IsShown = false;
        _refresh?.Stop();
        Hidden();
    }

    /// <summary>The page is on screen: it may start what it only does while looked at.</summary>
    protected virtual void Shown()
    {
    }

    /// <summary>The shell says the person has come back to the panel's window while this page is on screen.</summary>
    internal void ComeBack()
    {
        if (IsShown) CameBack();
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

using System.Collections.ObjectModel;
using System.Windows.Threading;
using Bosun.Panel.Controls;
using Bosun.Panel.Mvvm;

namespace Bosun.Panel.Shell;

/// <summary>One toast: an action's result, how it ended, and whether the person is holding it on screen.</summary>
public sealed class Toast : Observable
{
    internal Toast(LogSeverity severity, string message)
    {
        Severity = severity;
        Message = message;
    }

    public LogSeverity Severity { get; }

    /// <summary>The result's words, as its Activity line says them.</summary>
    public string Message { get; }

    public bool IsFail => Severity == LogSeverity.Fail;

    /// <summary>The state pill's kind: Danger for a failure, Ok for done.</summary>
    public StatusKind Kind => IsFail ? StatusKind.Danger : Severity == LogSeverity.Warn ? StatusKind.Warn : StatusKind.Ok;

    /// <summary>The state pill's word.</summary>
    public string Word => IsFail ? "Failed" : Severity == LogSeverity.Warn ? "Warning" : "Done";

    /// <summary>What a screen reader announces: the word and the result.</summary>
    public string Spoken => $"{Word}: {Message}";

    internal bool Pointer, Focus;

    private bool _expanded;

    /// <summary>
    /// Whether its whole message is shown (D02 T04 §7). An expanded toast is held until it is collapsed or dismissed,
    /// whatever the pointer and the keyboard then do: its own hold, not theirs.
    /// </summary>
    public bool Expanded { get => _expanded; internal set => Set(ref _expanded, value); }
    internal ITimer? Timer;
    internal TimeSpan Left;
    internal DateTimeOffset Since;
    internal int Generation;
}

/// <summary>
/// The window's toasts (D02 T03 §9, design/SPEC.md §19 Toast): a panel action's result where the person is looking, at
/// most three at once, the Activity pane keeping the record. A new toast takes the place of the oldest that is neither
/// a FAIL nor held (opened in full, under the pointer or holding the keyboard, D02 T04 §7); when every one is, the
/// newcomer is only counted ("2 more in the Activity pane"), since a FAIL stays until dismissed and a held toast is
/// being read. A toast that is not a FAIL goes after 6 s, paused while the pointer is over it or the keyboard is in it,
/// and on with the time it had left when neither holds. Its timer runs on the shell's clock; its expiry is handed to the
/// UI thread and does nothing for a toast already dismissed or dropped. While the window is hidden nothing is shown:
/// the stack is dropped, results are only logged, and nothing comes back when it is shown again.
/// </summary>
public sealed class ToastHost : Observable
{
    public const int Cap = 3;

    public static readonly TimeSpan Life = TimeSpan.FromSeconds(6);

    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private bool _windowShown;
    private int _overflow;

    public ToastHost(Dispatcher dispatcher, TimeProvider? clock = null, bool windowShown = true)
    {
        _dispatcher = dispatcher;
        _clock = clock ?? TimeProvider.System;
        _windowShown = windowShown;
    }

    /// <summary>The toasts on screen, oldest first.</summary>
    public ObservableCollection<Toast> Shown { get; } = [];

    /// <summary>Results not shown because FAILs and held toasts fill the stack; their lines are in the Activity pane.</summary>
    public int Overflow
    {
        get => _overflow;
        private set
        {
            if (Set(ref _overflow, value)) Raise(nameof(OverflowText));
        }
    }

    public string OverflowText => _overflow == 0 ? "" : $"{_overflow} more in the Activity pane";

    /// <summary>Timers running now: none with the stack empty, all FAILs, everything held, or the window hidden.</summary>
    public int RunningTimers => Shown.Count(t => t.Timer is not null);

    /// <summary>Whether the window is on screen; hiding it drops every toast.</summary>
    public bool WindowShown
    {
        get => _windowShown;
        set
        {
            if (!Set(ref _windowShown, value) || value) return;
            foreach (var toast in Shown.ToList()) Drop(toast);
            Overflow = 0;
        }
    }

    /// <summary>Shows a result whose Activity line has just been written (<see cref="ActivityLog.Record"/>).</summary>
    public void Show(LogSeverity severity, string message)
    {
        if (!_windowShown) return;
        var toast = new Toast(severity, message);
        if (Shown.Count >= Cap)
        {
            // The oldest that is neither a FAIL nor being read: opened in full, under the pointer or holding the keyboard
            // (D02 T04 §7). When every one is, the newcomer is counted, as when all are FAILs.
            if (Shown.FirstOrDefault(t => !t.IsFail && !t.Expanded && !t.Pointer && !t.Focus) is not { } oldest)
            {
                Overflow++;
                return;
            }
            Drop(oldest);
        }
        Shown.Add(toast);
        if (!toast.IsFail)
        {
            toast.Left = Life;
            Run(toast);
        }
    }

    /// <summary>Dismisses a toast: its Dismiss button, or its time running out.</summary>
    public void Dismiss(Toast toast)
    {
        if (!Shown.Contains(toast)) return;
        Drop(toast);
        // The stack has room again: the count of results only in the Activity pane has done its job.
        Overflow = 0;
    }

    /// <summary>The pointer came over or left a toast's card.</summary>
    public void HoldByPointer(Toast toast, bool held) => Hold(toast, held, toast.Focus);

    /// <summary>Shows a toast's whole message, or its first lines again; expanded, it is held until collapsed (D02 T04 §7).</summary>
    public void Expand(Toast toast, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(toast);
        if (toast.Expanded == expanded) return;
        var was = toast.Pointer || toast.Focus || toast.Expanded;
        toast.Expanded = expanded;
        var now = toast.Pointer || toast.Focus || toast.Expanded;
        if (toast.IsFail || !Shown.Contains(toast) || was == now) return;
        if (now) Pause(toast);
        else Run(toast);
    }

    /// <summary>The keyboard came into or left a toast's card.</summary>
    public void HoldByFocus(Toast toast, bool held) => Hold(toast, toast.Pointer, held);

    private void Hold(Toast toast, bool pointer, bool focus)
    {
        var was = toast.Pointer || toast.Focus || toast.Expanded;
        (toast.Pointer, toast.Focus) = (pointer, focus);
        var now = pointer || focus || toast.Expanded;
        if (toast.IsFail || !Shown.Contains(toast) || was == now) return;
        if (now) Pause(toast);
        else Run(toast);
    }

    private void Run(Toast toast)
    {
        Stop(toast);
        if (toast.Pointer || toast.Focus || toast.Expanded) return;
        var generation = ++toast.Generation;
        toast.Since = _clock.GetUtcNow();
        // A real clock calls back off the UI thread: the expiry is handed to it, and does nothing for a toast that has
        // been dismissed, dropped, held or run again since.
        toast.Timer = _clock.CreateTimer(_ => _dispatcher.BeginInvoke(() =>
        {
            if (toast.Generation == generation && toast.Timer is not null && Shown.Contains(toast)) Dismiss(toast);
        }), null, toast.Left, Timeout.InfiniteTimeSpan);
        Raise(nameof(RunningTimers));
    }

    private void Pause(Toast toast)
    {
        if (toast.Timer is null) return;
        var ran = _clock.GetUtcNow() - toast.Since;
        toast.Left = ran >= toast.Left ? TimeSpan.Zero : toast.Left - ran;
        Stop(toast);
    }

    private void Stop(Toast toast)
    {
        toast.Generation++;
        toast.Timer?.Dispose();
        toast.Timer = null;
        Raise(nameof(RunningTimers));
    }

    private void Drop(Toast toast)
    {
        Stop(toast);
        Shown.Remove(toast);
    }
}

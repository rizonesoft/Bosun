using System.Windows.Input;
using Bosun.Panel.Mvvm;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Shell;

/// <summary>
/// One thing a page offers that the command palette lists (D02 T03 §8, design/SPEC.md §19 Command palette): its name,
/// its glyph and the very command its button runs, so the palette adds no action of its own. A danger command's
/// confirmation lives inside that command, the same one its button asks, so nothing reaches the action without it.
/// <see cref="Reason"/> says why it can't run now, when its command says it can't.
/// </summary>
public sealed record PageCommand(string Name, GlyphAction Glyph, ICommand Command)
{
    /// <summary>Why it can't run while its command says so, in the page's words ("There are no changes to save").</summary>
    public Func<string?>? Reason { get; init; }

    /// <summary>Whether it changes something that can't simply be taken back: drawn in the danger colour.</summary>
    public bool Danger { get; init; }

    public const string Running = "It is already running";

    public const string NotNow = "It can't run now";

    /// <summary>Why it can't run now, asked of its command as it is; null when it may run.</summary>
    public string? WhyNot()
    {
        if (Command is AsyncCommand { IsRunning: true }) return Running;
        if (Command.CanExecute(null)) return null;
        return Reason?.Invoke() is { Length: > 0 } reason ? reason : NotNow;
    }
}

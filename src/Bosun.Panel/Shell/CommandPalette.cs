using System.Windows.Input;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Mvvm;
using Bosun.Panel.Theme;
using MahApps.Metro.IconPacks;

namespace Bosun.Panel.Shell;

/// <summary>
/// The panel's own commands for the palette (D02 T03 §8): the tray's Pause or Resume and Exit, as its menu has them
/// now. <see cref="Changed"/> says when they changed, a pause running out included, so the palette's rows always say
/// what they do.
/// </summary>
public interface IPanelCommands
{
    IReadOnlyList<PaletteRow> Commands();

    event Action? Changed;
}

/// <summary>One row of the palette: its name, the group it is in, its glyph, why it can't run now, and what it runs.</summary>
public sealed record PaletteRow(string Name, string Group, PackIconLucideKind Glyph, string? Reason, Func<Task> Run, bool Danger = false)
{
    /// <summary>Whether it may run now.</summary>
    public bool Enabled => Reason is null;

    /// <summary>What a screen reader hears: the name, its group, and why it can't run when it can't.</summary>
    public string Spoken => Reason is null ? $"{Name}, {Group}" : $"{Name}, {Group}, unavailable: {Reason}";
}

/// <summary>
/// The command palette (D02 T03 §8, design/SPEC.md §19): a keyboard route to every page, the page's own commands and
/// the panel's, opened by Ctrl+K. Typing ranks the rows through <see cref="CommandIndex"/>, the best first and
/// selected; Up and Down move the selection; Enter runs it. It adds no action of its own: a row runs the very command
/// its button runs, checked as it runs, so a disabled one runs nothing and says why, a running one is not started
/// twice, and a danger one asks its own question. The palette closes before what it starts takes the keyboard, and a
/// page is left only through <see cref="ShellViewModel.GoToAsync"/>, which asks about unsaved edits. While that
/// question is up it does not open.
/// </summary>
public sealed class CommandPalette : Observable
{
    public const string NothingMatches = "Nothing matches";

    public const string PagesGroup = "Pages";

    public const string PanelGroup = "Panel";

    private readonly ShellViewModel _shell;
    private readonly List<ICommand> _watched = [];
    private IReadOnlyList<PaletteRow> _rows = [], _all = [];
    private string _query = "";
    private int _selected = -1;
    private bool _isOpen;
    private string? _message;

    public CommandPalette(ShellViewModel shell) => _shell = shell;

    /// <summary>The panel's own commands; set where the tray is made, none until then.</summary>
    public IPanelCommands? Panel { get; set; }

    public bool IsOpen
    {
        get => _isOpen;
        private set => Set(ref _isOpen, value);
    }

    /// <summary>What was typed.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? "")) return;
            Message = null;
            Filter(keep: null);
        }
    }

    /// <summary>The rows matching the query, best first.</summary>
    public IReadOnlyList<PaletteRow> Rows
    {
        get => _rows;
        private set
        {
            if (Set(ref _rows, value)) Raise(nameof(NothingMatched));
        }
    }

    /// <summary>The selected row's place in <see cref="Rows"/>; -1 when there are none.</summary>
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            // The row itself is what the list shows selected, so it is said after the rows it is one of.
            if (Set(ref _selected, _rows.Count == 0 ? -1 : Math.Clamp(value, 0, _rows.Count - 1))) Raise(nameof(Selected));
        }
    }

    public PaletteRow? Selected => _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null;

    /// <summary>True while nothing matches what was typed.</summary>
    public bool NothingMatched => _rows.Count == 0;

    /// <summary>Why the row last chosen could not run; null otherwise.</summary>
    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>Raised as it closes, so the view gives the keyboard back to where it was.</summary>
    public event Action? Closed;

    /// <summary>Opens it with nothing typed, unless it is open or the shell is asking about unsaved edits.</summary>
    public bool Open()
    {
        if (IsOpen || _shell.IsAsking || _shell.IsLeaving) return false;
        _query = "";
        Raise(nameof(Query));
        Message = null;
        if (Panel is { } panel) panel.Changed += Rebuild;
        Rebuild();
        IsOpen = true;
        return true;
    }

    /// <summary>Closes it, and the keyboard goes back to where it was.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Watch([]);
        if (Panel is { } panel) panel.Changed -= Rebuild;
        Closed?.Invoke();
    }

    /// <summary>Moves the selection by <paramref name="by"/> rows, staying on the list.</summary>
    public void Move(int by)
    {
        if (_rows.Count > 0) SelectedIndex = (_selected < 0 ? 0 : _selected) + by;
    }

    /// <summary>Runs the selected row; nothing when nothing matches.</summary>
    public Task<bool> RunSelectedAsync() => Selected is { } row ? RunAsync(row) : Task.FromResult(false);

    /// <summary>
    /// Runs a row, asked again whether it may: one that can't says why and stays; one that can closes the palette and
    /// runs once the keyboard is back, so a question it asks is answered by the person, not by the key that chose it.
    /// </summary>
    public async Task<bool> RunAsync(PaletteRow row)
    {
        var now = Build().FirstOrDefault(r => r.Name == row.Name && r.Group == row.Group) ?? row;
        if (now.Reason is { } why)
        {
            Message = $"{now.Name}: {why}";
            Rebuild();
            return false;
        }
        Close();
        await Dispatcher.Yield(DispatcherPriority.Background);
        await now.Run();
        return true;
    }

    private void Rebuild()
    {
        _all = Build();
        Filter(keep: Selected?.Name);
        Watch(_shell.Page.PaletteCommands.Select(c => c.Command));
    }

    private void Filter(string? keep)
    {
        Rows = CommandIndex.Rank(_all, r => r.Name, _query);
        var at = keep is null ? -1 : Rows.ToList().FindIndex(r => r.Name == keep);
        _selected = -2;
        SelectedIndex = at >= 0 ? at : 0;
    }

    /// <summary>Every row as things are now: each page, the page's own commands, then the panel's.</summary>
    private List<PaletteRow> Build()
    {
        var rows = new List<PaletteRow>();
        foreach (var section in _shell.Sections)
            rows.Add(new PaletteRow(section.Title, PagesGroup, Glyphs.ForPage(section.Key), null, () => _shell.GoToAsync(section)));
        var page = _shell.Page;
        foreach (var command in page.PaletteCommands)
            rows.Add(new PaletteRow(command.Name, page.Title, Glyphs.For(command.Glyph), command.WhyNot(), () =>
            {
                command.Command.Execute(null);
                return Task.CompletedTask;
            }, command.Danger));
        if (Panel is { } panel) rows.AddRange(panel.Commands());
        return rows;
    }

    /// <summary>Follows each listed command's availability while open, so a row's state is its command's.</summary>
    private void Watch(IEnumerable<ICommand> commands)
    {
        foreach (var c in _watched) c.CanExecuteChanged -= OnCanExecute;
        _watched.Clear();
        if (!IsOpen && !commands.Any()) return;
        foreach (var c in commands.Distinct())
        {
            c.CanExecuteChanged += OnCanExecute;
            _watched.Add(c);
        }
    }

    private void OnCanExecute(object? sender, EventArgs e)
    {
        _all = Build();
        Filter(keep: Selected?.Name);
    }
}

using Bosun.Panel.Shell;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Tray;

/// <summary>
/// The tray menu's own lines in the command palette (D02 T03 §8): Pause notifications for 1 hour, or Resume while
/// paused, and Exit, taken from <see cref="TrayViewModel.Menu"/> each time, so the palette says and does what the menu
/// does (Exit asks about unsaved edits there too). Open and the queue line are pages the palette already lists.
/// </summary>
public sealed class TrayPaletteCommands(TrayViewModel tray) : IPanelCommands
{
    public IReadOnlyList<PaletteRow> Commands() =>
        tray.Menu().Where(i => i.Id is TrayViewModel.Pause or TrayViewModel.Exit)
            .Select(i => new PaletteRow(i.Text, CommandPalette.PanelGroup, Glyphs.For(i.Id == TrayViewModel.Exit ? GlyphAction.Exit : GlyphAction.Pause), null, i.Run))
            .ToList();

    public event Action? Changed
    {
        add => tray.Changed += value;
        remove => tray.Changed -= value;
    }
}

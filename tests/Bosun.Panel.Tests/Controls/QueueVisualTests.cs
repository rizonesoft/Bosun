using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bosun.Core.Memory;
using Bosun.Panel.Controls;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Bosun.Panel.Theme;
using MahApps.Metro.IconPacks;
using Xunit;

namespace Bosun.Panel.Tests.Controls;

/// <summary>
/// The queue's visuals (D02 T03 §6, design/SPEC.md §19 Queue slot): a tile per slot with its state in a word, the
/// holder's job, side and times, and the waiting jobs in order with why in words, all read from <c>bosun status
/// --json</c> through <see cref="StatusDoc"/>, so a renamed field fails here.
/// </summary>
public class QueueVisualTests
{
    /// <summary>A real <c>bosun status --json</c>, from build/Release/bosun.exe on 2026-11-16 with nothing queued.</summary>
    const string Captured = """
        {"schema_version":1,"ns":"Bosun","at":"2026-11-16T15:07:49Z","slots":[{"index":0,"busy":false,"holder":null},{"index":1,"busy":false,"holder":null}],"waiters":[],"memory":{"available_mb":4607,"total_mb":32601,"min_avail_mb":6144},"guest":null,"logger":{"windows":"running","wsl":null},"records":{"readable":true,"skipped":0,"dir":"C:\\Users\\DerickPayne\\AppData\\Local\\Bosun\\run\\1\\ns-%42osun"}}
        """;

    /// <summary>A holder's record as <c>dm::status_json</c> writes it.</summary>
    static string Holder(int slot, string phase = "\"running\"", string repo = "\"Recto\"", string side = "windows", string state = "live",
        string waited = "12", string running = "240") =>
        $$"""{"schema_version":1,"kind":"holder","pid":4120,"process_start":133000000000,"started_at":"2026-11-16T15:03:37Z","command":"cmake --build build","repo":{{repo}},"side":"{{side}}","slot":{{slot}},"session":1,"ns":"Bosun","acquired_at":"2026-11-16T15:03:49Z","command_started_at":"2026-11-16T15:03:49Z","min_guest_mb":null,"state":"{{state}}","waited_s":{{waited}},"running_s":{{running}},"phase":{{phase}}}""";

    /// <summary>A waiter's record as <c>dm::status_json</c> writes it.</summary>
    static string Waiter(int pid, string why, string floor = "null", string repo = "\"Office\"", string side = "wsl", string state = "live", string waited = "190") =>
        $$"""{"schema_version":1,"kind":"waiter","pid":{{pid}},"process_start":133000000001,"started_at":"2026-11-16T15:04:37Z","command":"dotnet test","repo":{{repo}},"side":"{{side}}","session":1,"ns":"Bosun","waiting_for":{{why}},"min_avail_mb":{{floor}},"max_wait_s":3600,"state":"{{state}}","waited_s":{{waited}}}""";

    static StatusDoc Doc(string slots, string waiters = "", string available = "4607", string floor = "6144", bool readable = true, int skipped = 0) =>
        JsonSerializer.Deserialize($$$"""{"schema_version":1,"ns":"Bosun","at":"2026-11-16T15:07:49Z","slots":[{{{slots}}}],"waiters":[{{{waiters}}}],"memory":{"available_mb":{{{available}}},"total_mb":32601,"min_avail_mb":{{{floor}}}},"guest":null,"logger":{"windows":"running","wsl":null},"records":{"readable":{{{(readable ? "true" : "false")}}},"skipped":{{{skipped}}},"dir":"C:\\run"}}""",
            MemoryJson.Default.StatusDoc)!;

    static SlotDoc Slot(string json) => Doc(json).Slots[0];

    static string Said(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element).GetName();

    static Color Paint(object? brush) => ((SolidColorBrush)brush!).Color;

    static Color Brush(FrameworkElement at, string key) => ((SolidColorBrush)at.FindResource(key)).Color;

    static Border TileBorder(QueueSlotTile tile) => (Border)tile.Template.FindName("tile", tile);

    static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [Fact]
    public void A_real_status_reads_as_two_free_slots_and_nothing_waiting()
    {
        var status = JsonSerializer.Deserialize(Captured, MemoryJson.Default.StatusDoc)!;
        Assert.Null(status.Fault());
        PageRenderer.InPage("Dark", () =>
        {
            var panel = new StackPanel();
            foreach (var slot in status.Slots) panel.Children.Add(new QueueSlotTile { Slot = slot });
            panel.Children.Add(new WaitingRow { Status = status });
            return panel;
        }, page =>
        {
            var tiles = ShellSupport.Under<QueueSlotTile>(page).ToList();
            Assert.Equal(["Slot 1, Free", "Slot 2, Free"], tiles.Select(Said));
            Assert.All(tiles, t => Assert.Equal(SlotState.Free, t.State));
            var row = ShellSupport.Under<WaitingRow>(page).Single();
            Assert.Empty(row.Lines);
            Assert.Contains(WaitingRow.NothingWaiting, PageRenderer.TextsOf(page));
            return 0;
        }, 480, 400);
    }

    public static TheoryData<string, string> States() => new()
    {
        { """{"index":0,"busy":false,"holder":null}""", "Free" },
        { $$"""{"index":0,"busy":true,"holder":{{Holder(0)}}}""", "Running" },
        { $$"""{"index":0,"busy":true,"holder":{{Holder(0, "\"starting\"", running: "null")}}}""", "Starting" },
        { $$"""{"index":0,"busy":true,"holder":{{Holder(0, "\"waiting for guest memory\"", side: "wsl", running: "null")}}}""", "Waiting for guest memory" },
        { $$"""{"index":0,"busy":true,"holder":{{Holder(0, "null")}}}""", "Held" },
        { """{"index":0,"busy":true,"holder":null}""", "Held" },
        { """{"index":0,"busy":null,"holder":null}""", "Not available" },
    };

    static readonly (string Theme, string? Palette)[] Themes = [("Dark", null), ("Light", null), ("HighContrast", "NightSky"), ("HighContrast", "Desert")];

    [Theory]
    [MemberData(nameof(States))]
    public void Each_state_has_its_word_and_glyph_in_every_theme(string slot, string word)
    {
        foreach (var (theme, name) in Themes)
        {
            var palette = name is null ? null : Helpers.ContrastPalettes.Both.Single(p => p.Name == name).Palette;
            PageRenderer.InPage(theme, () => new QueueSlotTile { Slot = Slot(slot) }, page =>
            {
                var tile = ShellSupport.Under<QueueSlotTile>(page).Single();
                Assert.Equal(word, tile.Word);
                Assert.Equal(word, ShellSupport.Under<StatusPill>(tile).Single().ShownWord);           // the word is drawn
                var glyph = ShellSupport.Under<PackIconLucide>(tile).First(g => g.Kind == tile.Glyph);
                Assert.Equal(tile.State switch
                {
                    SlotState.Free => Glyphs.For(GlyphAction.SlotFree),
                    SlotState.NotAvailable => Glyphs.For(GlyphAction.SlotUnknown),
                    _ => Glyphs.For(GlyphAction.SlotHeld),
                }, glyph.Kind);
                Assert.StartsWith($"Slot 1, {word}", Said(tile));
                var border = TileBorder(tile);
                var dash = (Rectangle)tile.Template.FindName("dash", tile);
                if (palette is not null)
                {
                    Assert.Equal(palette.WindowText, Paint(tile.Foreground));
                    Assert.Equal(tile.Held ? palette.Highlight : tile.State == SlotState.Free ? Colors.Transparent : palette.WindowText, Paint(border.BorderBrush));
                    if (tile.State == SlotState.Free) Assert.Equal(palette.WindowText, Paint(dash.Stroke));
                }
                else if (tile.Held)
                {
                    Assert.Equal(Brush(page, "B.Accent"), Paint(border.BorderBrush));
                    Assert.Equal(Brush(page, "B.AccentDim"), Paint(border.Background));
                }
                else if (tile.State == SlotState.Free)
                {
                    Assert.Equal(Visibility.Visible, dash.Visibility);                                 // the mockup's dashed outline
                    Assert.Equal(Colors.Transparent, Paint(border.Background));
                    Assert.Equal(Brush(page, "B.TextMute"), Paint(tile.Foreground));
                }
                return 0;
            }, palette: palette);
        }
    }

    [Fact]
    public void A_held_tile_names_the_slot_job_side_and_times_and_says_what_it_cannot_tell()
    {
        PageRenderer.InPage("Dark", () => new QueueSlotTile { Slot = Doc($$"""{"index":1,"busy":true,"holder":{{Holder(1)}}}""").Slots[0] }, page =>
        {
            var tile = ShellSupport.Under<QueueSlotTile>(page).Single();
            Assert.Equal("Slot 2, Running, Recto: cmake --build build, Windows, waited 12 s, running 4 min", Said(tile));
            foreach (var text in new[] { "Slot 2", "Running", "Recto: cmake --build build", "Windows, waited 12 s, running 4 min" })
                Assert.Contains(text, PageRenderer.TextsOf(tile));
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(tile).GetChildren()); // said once, as a whole
            Assert.False(tile.Focusable);

            tile.Slot = Slot($$"""{"index":0,"busy":true,"holder":{{Holder(0, "null", repo: "null", side: "wsl", state: "unknown", waited: "null", running: "4500")}}}""");
            PageRenderer.Settle(page);                                                           // a new document, in place
            Assert.Equal("Slot 1, Held, no repo: cmake --build build, WSL, running 1 h 15 min, may have ended", Said(tile));
            Assert.Contains("may have ended", PageRenderer.TextsOf(tile));

            tile.Slot = Slot("""{"index":0,"busy":true,"holder":null}""");
            PageRenderer.Settle(page);
            Assert.Equal("Slot 1, Held, holder not known", Said(tile));
            return 0;
        });
    }

    [Fact]
    public void A_slot_above_the_limit_that_a_job_still_holds_says_so_in_words_seen_and_said()
    {
        // D03 T02 §3: slots lowered to 1 while slot 2's job runs; bosun status still lists it, marked above the limit.
        PageRenderer.InPage("Dark", () => new QueueSlotTile { Slot = Slot($$"""{"index":1,"busy":true,"above_limit":true,"holder":{{Holder(1)}}}""") }, page =>
        {
            var tile = ShellSupport.Under<QueueSlotTile>(page).Single();
            Assert.Equal($"Slot 2, Running, Recto: cmake --build build, Windows, waited 12 s, running 4 min, {QueueSlotTile.AboveLimit}", Said(tile));
            Assert.Contains(QueueSlotTile.AboveLimit, PageRenderer.TextsOf(tile));
            Assert.True(((TextBlock)tile.Template.FindName("limit", tile)).IsVisible);
            tile.Slot = Slot($$"""{"index":1,"busy":true,"holder":{{Holder(1)}}}""");      // within the limit: nothing said
            PageRenderer.Settle(page);
            Assert.False(((TextBlock)tile.Template.FindName("limit", tile)).IsVisible);
            Assert.DoesNotContain("limit", Said(tile), StringComparison.Ordinal);
            return 0;
        });
    }

    [Fact]
    public void An_unavailable_slot_says_why_seen_and_said()
    {
        // Completion review: "Not available" alone gave no reason.
        PageRenderer.InPage("Dark", () => new QueueSlotTile { Slot = Slot("""{"index":1,"busy":null,"holder":null}""") }, page =>
        {
            var tile = ShellSupport.Under<QueueSlotTile>(page).Single();
            Assert.Equal($"Slot 2, Not available, {QueueSlotTile.NotKnown}", Said(tile));
            Assert.Contains(QueueSlotTile.NotKnown, PageRenderer.TextsOf(tile));
            Assert.True(((TextBlock)tile.Template.FindName("job", tile)).IsVisible);
            return 0;
        });
    }

    [Fact]
    public void Long_commands_are_shown_whole_never_cut()
    {
        // Completion review: two commands that differ only past the tile's width read the same when cut with an ellipsis.
        const string prefix = "cmake --build build --config Release --target bosun_panel_tests --parallel 8 -- /p:Verbosity=minimal";
        var long1 = Holder(0).Replace("\"cmake --build build\"", $"\"{prefix} /p:Case=one\"");
        var waiter = Waiter(11, "\"slot\"").Replace("\"dotnet test\"", $"\"{prefix} /p:Case=two\"");
        PageRenderer.InPage("Dark", () =>
        {
            var panel = new StackPanel { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(new QueueSlotTile { Slot = Slot($$"""{"index":0,"busy":true,"holder":{{long1}}}""") });
            panel.Children.Add(new WaitingRow { Status = Doc(FreeSlot, waiter) });
            return panel;
        }, page =>
        {
            var tile = ShellSupport.Under<QueueSlotTile>(page).Single();
            var line = ShellSupport.Under<WaitingLineView>(page).Single();
            foreach (var (job, text) in new[] { ((TextBlock)tile.Template.FindName("job", tile), "/p:Case=one"), ((TextBlock)line.Template.FindName("job", line), "/p:Case=two") })
            {
                Assert.EndsWith(text, job.Text);
                Assert.Equal(TextTrimming.None, job.TextTrimming);
                Assert.Equal(TextWrapping.Wrap, job.TextWrapping);
                Assert.True(job.ActualHeight > job.FontSize * 2, "the whole command shows, over more than one line");
            }
            return 0;
        }, 480, 600);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(45L, "45 s")]
    [InlineData(240L, "4 min")]
    [InlineData(3600L, "1 h")]
    [InlineData(4320L, "1 h 12 min")]
    [InlineData(-1L, null)]
    public void Times_are_said_in_words(long? seconds, string? words) => Assert.Equal(words, QueueWords.Elapsed(seconds));

    sealed class FakeAnimation(bool on) : IWindowsAnimation
    {
        public bool On { get; private set; } = on;

        public event Action? Changed;

        public void Set(bool on)
        {
            On = on;
            Changed?.Invoke();
        }
    }

    static void WithGate(bool animates, string slot, Action<Border, QueueSlotTile, SlotBar, FakeAnimation, Motion> test)
    {
        PageRenderer.InPage("Dark", () => new QueueSlotTile { Slot = Slot(slot) }, page =>
        {
            var windows = new FakeAnimation(animates);
            var gate = new Motion(page.Resources, windows);
            page.Resources[Motion.GateKey] = gate;
            var tile = ShellSupport.Under<QueueSlotTile>(page).Single();
            var bar = ShellSupport.Under<SlotBar>(tile).Single();
            tile.Visibility = Visibility.Collapsed;                                               // shown again with the gate in place,
            tile.Visibility = Visibility.Visible;                                                 // as the application installs it first
            PageRenderer.Settle(page);
            test(page, tile, bar, windows, gate);
            return 0;
        });
    }

    static readonly string Running = $$"""{"index":0,"busy":true,"holder":{{Holder(0)}}}""";

    [Fact]
    public void A_held_slot_moves_its_bar_on_the_pulse_and_a_free_one_has_none()
    {
        WithGate(true, Running, (_, tile, bar, _, gate) =>
        {
            Assert.True(bar.Moving && bar.HasAnimatedProperties);
            var at = bar.Offset;
            Pump(TimeSpan.FromMilliseconds(gate.Pulse.TimeSpan.TotalMilliseconds / 5));
            Assert.NotEqual(at, bar.Offset);                                                    // it moves
            tile.Slot = Slot("""{"index":0,"busy":false,"holder":null}""");
            Assert.False(bar.Active || bar.Moving || bar.HasAnimatedProperties);                 // freed: no bar, no clock
        });
    }

    [Fact]
    public void The_bar_is_still_with_motion_off_or_turned_off_and_stops_hidden_or_unloaded()
    {
        WithGate(false, Running, (_, _, bar, _, _) =>
        {
            Assert.True(bar.Active);
            Assert.False(bar.Moving || bar.HasAnimatedProperties);
            Assert.Equal(0, bar.Offset);
        });
        WithGate(true, Running, (page, tile, bar, windows, gate) =>
        {
            windows.Set(false);
            Assert.False(bar.Moving || bar.HasAnimatedProperties);
            windows.Set(true);
            Assert.True(bar.Moving);
            tile.Visibility = Visibility.Collapsed;
            Assert.False(bar.Moving || bar.HasAnimatedProperties);
            tile.Visibility = Visibility.Visible;
            PageRenderer.Settle(page);
            Assert.True(bar.Moving);
            page.Child = null;
            PageRenderer.Settle(page);
            Assert.False(bar.Moving || bar.HasAnimatedProperties);
            int Listeners() => (typeof(Motion).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate) as Delegate)?
                .GetInvocationList().Length ?? 0;
            Assert.Equal(0, Listeners());
        });
    }

    const string FreeSlot = """{"index":0,"busy":false,"holder":null}""";

    [Fact]
    public void A_memory_held_job_says_its_own_floor_and_the_free_figure_in_words()
    {
        var status = Doc(FreeSlot, string.Join(",", Waiter(11, "\"memory\"", "8192"), Waiter(12, "\"memory\"", "4096", repo: "\"Recto\"", side: "windows"),
            Waiter(13, "\"slot\"", waited: "30")));
        PageRenderer.InPage("Dark", () => new WaitingRow { Status = status }, page =>
        {
            var row = ShellSupport.Under<WaitingRow>(page).Single();
            Assert.Equal([
                new WaitingLine(1, "Office: dotnet test", "Waiting for memory: slot 2 opens when 8.0 GB is free; 4.5 GB is free now", "WSL, waited 3 min", false),
                new WaitingLine(2, "Recto: dotnet test", "Waiting for memory: slot 2 opens when 4.0 GB is free; 4.5 GB is free now", "Windows, waited 3 min", false),
                new WaitingLine(3, "Office: dotnet test", "Waiting for a free slot", "WSL, waited 30 s", false),
            ], row.Lines);                                                                      // each its own floor, not the 6.0 GB default
            var texts = PageRenderer.TextsOf(page);
            Assert.Contains("Waiting for memory: slot 2 opens when 8.0 GB is free; 4.5 GB is free now", texts);
            Assert.DoesNotContain(WaitingRow.NothingWaiting, texts);
            var lines = ShellSupport.Under<WaitingLineView>(row).ToList();
            Assert.Equal(3, lines.Count);
            var peer = UIElementAutomationPeer.CreatePeerForElement(lines[0]);
            Assert.Equal(("1. Office: dotnet test. Waiting for memory: slot 2 opens when 8.0 GB is free; 4.5 GB is free now. WSL, waited 3 min",
                AutomationControlType.ListItem), (peer.GetName(), peer.GetAutomationControlType()));
            Assert.Null(peer.GetChildren());                                                    // each line said once
            Assert.Equal("Waiting, 3 jobs", Said(row));
            return 0;
        }, 640, 480);
    }

    [Fact]
    public void What_a_record_or_reading_does_not_say_is_said_never_guessed()
    {
        var status = Doc(FreeSlot, string.Join(",", Waiter(11, "\"memory\""), Waiter(12, "null", repo: "null", state: "unknown", waited: "null")),
            available: "null", floor: "0");
        var (lines, notice) = WaitingRow.Read(status);
        Assert.Equal("Waiting for memory, its floor not recorded; free memory is not available", lines[0].Why);
        Assert.Equal(new WaitingLine(2, "no repo: dotnet test", "Waiting for a reason not recorded", "WSL, waited time not known", true), lines[1]);
        Assert.DoesNotContain(lines, l => l.Why.Contains(" 0") || l.Why.Contains("0.0"));       // never a 0 for what is unknown
        Assert.Equal("", notice);

        var fallback = WaitingRow.Read(Doc(FreeSlot, Waiter(11, "\"memory\"")));
        Assert.Equal("Waiting for memory: slot 2 opens when 6.0 GB is free; 4.5 GB is free now", fallback.Lines[0].Why); // no floor of its own

        PageRenderer.InPage("Dark", () => new WaitingRow { Status = status }, page =>
        {
            Assert.Contains("May have ended", PageRenderer.TextsOf(page).Where(t => ShellSupport.Under<TextBlock>(page).Any(b => b.IsVisible && b.Text == t)));
            return 0;
        });
    }

    [Fact]
    public void Records_that_could_not_be_read_are_said_never_Nothing_waiting()
    {
        var unread = WaitingRow.Read(Doc(FreeSlot, readable: false));
        Assert.Empty(unread.Lines);
        Assert.Equal("The queue's records could not be read, so waiting jobs can't be shown", unread.Notice);

        var partial = WaitingRow.Read(Doc(FreeSlot, Waiter(11, "\"slot\""), skipped: 2));
        Assert.Single(partial.Lines);                                                           // the jobs it has, kept
        Assert.Equal("2 records could not be read, so a job may be missing", partial.Notice);

        var none = WaitingRow.Read(Doc(FreeSlot, skipped: 1));
        Assert.Equal("1 record could not be read, so a job may be missing", none.Notice);       // not "Nothing waiting"
        Assert.Equal((Array.Empty<WaitingLine>(), WaitingRow.NotAvailable), (WaitingRow.Read(null).Lines.ToArray(), WaitingRow.Read(null).Notice));

        PageRenderer.InPage("Dark", () => new WaitingRow { Status = Doc(FreeSlot, readable: false) }, page =>
        {
            var row = ShellSupport.Under<WaitingRow>(page).Single();
            var texts = PageRenderer.TextsOf(page);
            Assert.Contains(unread.Notice, texts);
            Assert.DoesNotContain(WaitingRow.NothingWaiting, texts);
            Assert.Equal($"Waiting, {unread.Notice}", Said(row));
            row.Status = Doc(FreeSlot, Waiter(11, "\"slot\""));                                // and it recovers
            PageRenderer.Settle(page);
            Assert.Equal("Waiting, 1 job", Said(row));
            return 0;
        });
    }

    [Theory]
    [InlineData("Dark", null)]
    [InlineData("Light", null)]
    [InlineData("HighContrast", "NightSky")]
    [InlineData("HighContrast", "Desert")]
    public void Waiting_lines_take_the_theme_and_Windows_own_colours_in_contrast(string theme, string? name)
    {
        var palette = name is null ? null : Helpers.ContrastPalettes.Both.Single(p => p.Name == name).Palette;
        PageRenderer.InPage(theme, () => new WaitingRow { Status = Doc(FreeSlot, Waiter(11, "\"slot\"", state: "unknown")) }, page =>
        {
            var line = ShellSupport.Under<WaitingLineView>(page).Single();
            var meta = (TextBlock)line.Template.FindName("meta", line);
            var doubt = (TextBlock)line.Template.FindName("doubt", line);
            Assert.Equal(Visibility.Visible, doubt.Visibility);
            if (palette is not null)
            {
                Assert.Equal(palette.WindowText, Paint(meta.Foreground));
                Assert.Equal(palette.WindowText, Paint(doubt.Foreground));
            }
            else
            {
                Assert.Equal(Brush(page, "B.TextDim"), Paint(meta.Foreground));
                Assert.Equal(Brush(page, "B.Warn"), Paint(doubt.Foreground));
            }
            return 0;
        }, palette: palette);
    }
}

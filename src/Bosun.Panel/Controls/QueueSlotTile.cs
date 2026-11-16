using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Bosun.Core.Memory;
using Bosun.Panel.Theme;
using MahApps.Metro.IconPacks;

namespace Bosun.Panel.Controls;

/// <summary>What a queue slot is doing, as one word says it.</summary>
public enum SlotState
{
    Free,
    Running,
    Starting,
    WaitingForGuest,
    Held,
    NotAvailable,
}

/// <summary>
/// The words the queue's visuals share (D02 T03 §6): times, sides, repos and memory, said the same way everywhere.
/// </summary>
public static class QueueWords
{
    /// <summary>A length of time in words ("45 s", "4 min", "1 h 12 min"); null when it can't be told.</summary>
    public static string? Elapsed(long? seconds)
    {
        if (seconds is not { } s || s < 0) return null;
        if (s < 60) return $"{s} s";
        if (s < 3600) return $"{s / 60} min";
        var (h, m) = (s / 3600, s % 3600 / 60);
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    /// <summary>A side as a person says it.</summary>
    public static string Side(string? side) => side switch { "windows" => "Windows", "wsl" => "WSL", null or "" => "side not known", _ => side };

    /// <summary>A repo, or "no repo" for a job run outside one.</summary>
    public static string Repo(string? repo) => string.IsNullOrWhiteSpace(repo) ? "no repo" : repo.Trim();

    /// <summary>Megabytes as gigabytes to one place ("6.0 GB").</summary>
    public static string Gb(long mb) => (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    /// <summary>A job's repo and command, as its line or tile heads it.</summary>
    public static string Job(QueueRecordDoc record) => $"{Repo(record.Repo)}: {record.Command}";
}

/// <summary>
/// One build slot (D02 T03 §6, design/SPEC.md §19 Queue slot): free, or who holds it and what it is doing, from one
/// <see cref="SlotDoc"/> of <c>bosun status --json</c>. Every state has its word, so colour is never the only key:
/// Free, Running, Starting, Waiting for guest memory, Held (a holder whose record gives no phase, or none at all) and Not
/// available (its lock couldn't be probed and no holder is known). A held tile names the slot, the holder's repo,
/// command and side, how long it waited and how long it has run, and "may have ended" when its process couldn't be
/// checked; under it the mockup's moving bar runs on the motion gate's pulse. A screen reader hears the tile as one
/// sentence.
/// </summary>
public class QueueSlotTile : Control
{
    public static readonly DependencyProperty SlotProperty = DependencyProperty.Register(
        nameof(Slot), typeof(SlotDoc), typeof(QueueSlotTile), new FrameworkPropertyMetadata(null, OnSlotChanged));

    private static readonly DependencyPropertyKey StateKey = DependencyProperty.RegisterReadOnly(
        nameof(State), typeof(SlotState), typeof(QueueSlotTile), new FrameworkPropertyMetadata(SlotState.NotAvailable));

    public static readonly DependencyProperty StateProperty = StateKey.DependencyProperty;

    private static readonly DependencyPropertyKey TitleKey = Text(nameof(Title), "Slot");
    private static readonly DependencyPropertyKey WordKey = Text(nameof(Word), "Not available");
    private static readonly DependencyPropertyKey JobKey = Text(nameof(Job), "");
    private static readonly DependencyPropertyKey TimingKey = Text(nameof(Timing), "");
    private static readonly DependencyPropertyKey DoubtKey = Text(nameof(Doubt), "");

    public static readonly DependencyProperty TitleProperty = TitleKey.DependencyProperty;
    public static readonly DependencyProperty WordProperty = WordKey.DependencyProperty;
    public static readonly DependencyProperty JobProperty = JobKey.DependencyProperty;
    public static readonly DependencyProperty TimingProperty = TimingKey.DependencyProperty;
    public static readonly DependencyProperty DoubtProperty = DoubtKey.DependencyProperty;

    private static readonly DependencyPropertyKey KindKey = DependencyProperty.RegisterReadOnly(
        nameof(Kind), typeof(StatusKind), typeof(QueueSlotTile), new FrameworkPropertyMetadata(StatusKind.Neutral));

    public static readonly DependencyProperty KindProperty = KindKey.DependencyProperty;

    private static readonly DependencyPropertyKey GlyphKey = DependencyProperty.RegisterReadOnly(
        nameof(Glyph), typeof(PackIconLucideKind), typeof(QueueSlotTile), new FrameworkPropertyMetadata(Glyphs.For(GlyphAction.SlotUnknown)));

    public static readonly DependencyProperty GlyphProperty = GlyphKey.DependencyProperty;

    private static readonly DependencyPropertyKey HeldKey = DependencyProperty.RegisterReadOnly(
        nameof(Held), typeof(bool), typeof(QueueSlotTile), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty HeldProperty = HeldKey.DependencyProperty;

    private static DependencyPropertyKey Text(string name, string initial) =>
        DependencyProperty.RegisterReadOnly(name, typeof(string), typeof(QueueSlotTile), new FrameworkPropertyMetadata(initial));

    static QueueSlotTile() => FocusableProperty.OverrideMetadata(typeof(QueueSlotTile), new FrameworkPropertyMetadata(false));

    /// <summary>The slot as <c>bosun status --json</c> gave it; null while there is none to show.</summary>
    public SlotDoc? Slot { get => (SlotDoc?)GetValue(SlotProperty); set => SetValue(SlotProperty, value); }

    public SlotState State => (SlotState)GetValue(StateProperty);

    /// <summary>"Slot 1": the slot's number, counted from one.</summary>
    public string Title => (string)GetValue(TitleProperty);

    /// <summary>The state in a word.</summary>
    public string Word => (string)GetValue(WordProperty);

    /// <summary>The holder's repo and command, "holder not known" for a held slot without one, else empty.</summary>
    public string Job => (string)GetValue(JobProperty);

    /// <summary>The holder's side and times ("Windows, waited 12 s, running 4 min"), else empty.</summary>
    public string Timing => (string)GetValue(TimingProperty);

    /// <summary>"may have ended" when the holder's process couldn't be checked, else empty.</summary>
    public string Doubt => (string)GetValue(DoubtProperty);

    /// <summary>The pill's kind for the state.</summary>
    public StatusKind Kind => (StatusKind)GetValue(KindProperty);

    /// <summary>The state's glyph.</summary>
    public PackIconLucideKind Glyph => (PackIconLucideKind)GetValue(GlyphProperty);

    /// <summary>Whether something holds the slot: its tile is the held one, with the moving bar.</summary>
    public bool Held => (bool)GetValue(HeldProperty);

    /// <summary>What a screen reader hears: the tile as one sentence.</summary>
    public string SpokenName => string.Join(", ", new[] { Title, Word, Job, Timing, Doubt }.Where(t => !string.IsNullOrEmpty(t)));

    /// <summary>A slot's state from its document.</summary>
    public static SlotState StateOf(SlotDoc? slot) => slot switch
    {
        null => SlotState.NotAvailable,
        { Busy: false } => SlotState.Free,
        { Holder: { } holder } => holder.Phase switch
        {
            "running" => SlotState.Running,
            "starting" => SlotState.Starting,
            "waiting for guest memory" => SlotState.WaitingForGuest,
            _ => SlotState.Held,
        },
        { Busy: true } => SlotState.Held,
        _ => SlotState.NotAvailable,
    };

    private static void OnSlotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tile = (QueueSlotTile)d;
        var was = tile.SpokenName;
        var slot = tile.Slot;
        var state = StateOf(slot);
        var holder = slot?.Holder;
        tile.SetValue(StateKey, state);
        tile.SetValue(TitleKey, slot is null ? "Slot" : $"Slot {slot.Index + 1}");
        tile.SetValue(WordKey, state switch
        {
            SlotState.Free => "Free",
            SlotState.Running => "Running",
            SlotState.Starting => "Starting",
            SlotState.WaitingForGuest => "Waiting for guest memory",
            SlotState.Held => "Held",
            _ => "Not available",
        });
        tile.SetValue(KindKey, state switch
        {
            SlotState.Running => StatusKind.Ok,
            SlotState.Starting => StatusKind.Info,
            SlotState.WaitingForGuest => StatusKind.Warn,
            _ => StatusKind.Neutral,
        });
        tile.SetValue(GlyphKey, Glyphs.For(state switch
        {
            SlotState.Free => GlyphAction.SlotFree,
            SlotState.NotAvailable => GlyphAction.SlotUnknown,
            _ => GlyphAction.SlotHeld,
        }));
        var held = state is not (SlotState.Free or SlotState.NotAvailable);
        tile.SetValue(HeldKey, held);
        tile.SetValue(JobKey, holder is not null ? QueueWords.Job(holder) : held ? "holder not known" : "");
        tile.SetValue(TimingKey, holder is null ? "" : string.Join(", ", new[]
        {
            QueueWords.Side(holder.Side),
            QueueWords.Elapsed(holder.WaitedS) is { } waited ? $"waited {waited}" : null,
            QueueWords.Elapsed(holder.RunningS) is { } running ? $"running {running}" : null,
        }.Where(t => t is not null)));
        tile.SetValue(DoubtKey, holder is { State: "unknown" } ? "may have ended" : "");
        if (was != tile.SpokenName && UIElementAutomationPeer.FromElement(tile) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, was, tile.SpokenName);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Said(this);

    private sealed class Said(QueueSlotTile owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.SpokenName;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(QueueSlotTile);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>
/// A held slot's moving bar (D02 T03 §6, the mockup's <c>.slot .bar</c>): a 3 px bar 40% of the tile wide, sliding
/// across and back over the motion gate's pulse while <see cref="Active"/>. Still at the left with animation effects
/// off, or turned off while it moves; no clock while it is inactive, hidden or unloaded.
/// </summary>
public class SlotBar : FrameworkElement
{
    /// <summary>The bar's share of the tile's width.</summary>
    public const double Share = 0.4;

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(SlotBar), new FrameworkPropertyMetadata(false, (d, _) => ((SlotBar)d).Run()));

    public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
        nameof(Offset), typeof(double), typeof(SlotBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SlotBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private Motion? _gate;

    static SlotBar() => HeightProperty.OverrideMetadata(typeof(SlotBar), new FrameworkPropertyMetadata(3.0));

    public SlotBar()
    {
        Loaded += (_, _) => Run();
        Unloaded += (_, _) =>
        {
            Stop();
            if (_gate is not null) _gate.Changed -= Run;
            _gate = null;
        };
        IsVisibleChanged += (_, _) => Run();
    }

    /// <summary>Whether the bar shows and moves: while the slot is held.</summary>
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

    /// <summary>Where the bar is, from 0 (at the left) to 1 (at the right).</summary>
    public double Offset { get => (double)GetValue(OffsetProperty); set => SetValue(OffsetProperty, value); }

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Whether it is moving now.</summary>
    public bool Moving { get; private set; }

    private Motion? Gate()
    {
        if (_gate is null && IsLoaded && TryFindResource(Motion.GateKey) is Motion gate)
        {
            _gate = gate;
            _gate.Changed += Run;
        }
        return _gate;
    }

    private void Run()
    {
        var gate = Gate();
        if (!Active || !IsLoaded || !IsVisible || gate is not { Animates: true } || gate.Pulse.TimeSpan <= TimeSpan.Zero)
        {
            Stop();
            return;
        }
        if (Moving) return;
        var half = TimeSpan.FromTicks(gate.Pulse.TimeSpan.Ticks / 2);
        var slide = new DoubleAnimationUsingKeyFrames { Duration = gate.Pulse.TimeSpan, RepeatBehavior = RepeatBehavior.Forever };
        slide.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(half), gate.Ease));
        slide.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(gate.Pulse.TimeSpan), gate.Ease));
        Moving = true;
        BeginAnimation(OffsetProperty, slide);
    }

    private void Stop()
    {
        BeginAnimation(OffsetProperty, null);
        Offset = 0;
        Moving = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Active || Fill is not { } fill) return;
        var width = ActualWidth * Share;
        dc.DrawRectangle(fill, null, new Rect((ActualWidth - width) * Offset, 0, width, ActualHeight));
    }
}


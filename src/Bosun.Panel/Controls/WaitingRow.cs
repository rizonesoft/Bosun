using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Bosun.Core.Memory;

namespace Bosun.Panel.Controls;

/// <summary>One waiting job as its line shows and says it.</summary>
public sealed record WaitingLine(int Place, string Job, string Why, string Meta, bool Doubtful)
{
    /// <summary>What a screen reader hears: the whole line as one sentence.</summary>
    public string Spoken => $"{Place}. {Job}. {Why}. {Meta}" + (Doubtful ? ". May have ended" : "");
}

/// <summary>
/// The jobs waiting for a slot, in the order they will get one (D02 T03 §6, design/SPEC.md §19 Queue slot), from
/// <c>bosun status --json</c>: each line its place, repo and command, side and waited time, and why it waits in words. A
/// job held by memory names its own floor, the limit its job was given ("Waiting for memory: slot 2 opens when 6.0 GB
/// is free; 4.5 GB is free now"), and a figure that couldn't be read says so, never 0. What a record doesn't say is said
/// as unknown, never guessed. Records the tool couldn't read are said too: none read is never "Nothing waiting", and
/// some skipped keeps the jobs it has with how many it couldn't read.
/// </summary>
public class WaitingRow : Control
{
    public const string NothingWaiting = "Nothing waiting";

    public const string NotAvailable = "Not available";

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(StatusDoc), typeof(WaitingRow), new FrameworkPropertyMetadata(null, OnStatusChanged));

    private static readonly DependencyPropertyKey LinesKey = DependencyProperty.RegisterReadOnly(
        nameof(Lines), typeof(IReadOnlyList<WaitingLine>), typeof(WaitingRow), new FrameworkPropertyMetadata(Array.Empty<WaitingLine>()));

    public static readonly DependencyProperty LinesProperty = LinesKey.DependencyProperty;

    private static readonly DependencyPropertyKey NoticeKey = DependencyProperty.RegisterReadOnly(
        nameof(Notice), typeof(string), typeof(WaitingRow), new FrameworkPropertyMetadata(NotAvailable));

    public static readonly DependencyProperty NoticeProperty = NoticeKey.DependencyProperty;

    static WaitingRow() => FocusableProperty.OverrideMetadata(typeof(WaitingRow), new FrameworkPropertyMetadata(false));

    /// <summary>The queue as <c>bosun status --json</c> gave it; null while there is none to show.</summary>
    public StatusDoc? Status { get => (StatusDoc?)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    /// <summary>The waiting jobs' lines, oldest first.</summary>
    public IReadOnlyList<WaitingLine> Lines => (IReadOnlyList<WaitingLine>)GetValue(LinesProperty);

    /// <summary>What is said under the lines: nothing waiting, records that couldn't be read, or empty.</summary>
    public string Notice => (string)GetValue(NoticeProperty);

    /// <summary>What a screen reader hears for the list itself; each line is said by its own element.</summary>
    public string SpokenName => Lines.Count == 0 ? $"Waiting, {Notice}"
        : $"Waiting, {Lines.Count} {(Lines.Count == 1 ? "job" : "jobs")}" + (Notice.Length > 0 ? $", {Notice}" : "");

    /// <summary>Why a waiter waits, in words.</summary>
    public static string WhyOf(QueueRecordDoc waiter, StatusDoc status)
    {
        switch (waiter.WaitingFor)
        {
            case "slot":
                return "Waiting for a free slot";
            case "memory":
                var floor = waiter.MinAvailMb is > 0 ? waiter.MinAvailMb : status.Memory.MinAvailMb > 0 ? status.Memory.MinAvailMb : null;
                var free = status.Memory.AvailableMb is { } mb && mb >= 0 ? $"{QueueWords.Gb(mb)} is free now" : "free memory is not available";
                return floor is { } f
                    ? $"Waiting for memory: slot 2 opens when {QueueWords.Gb(f)} is free; {free}"
                    : $"Waiting for memory, its floor not recorded; {free}";
            case null or "":
                return "Waiting for a reason not recorded";
            default:
                return $"Waiting for {waiter.WaitingFor}";
        }
    }

    /// <summary>The lines and the notice for a status document.</summary>
    public static (IReadOnlyList<WaitingLine> Lines, string Notice) Read(StatusDoc? status)
    {
        if (status is null) return ([], NotAvailable);
        if (!status.Records.Readable) return ([], "The queue's records could not be read, so waiting jobs can't be shown");
        var lines = status.Waiters.Select((w, i) => new WaitingLine(i + 1, QueueWords.Job(w), WhyOf(w, status),
            QueueWords.Side(w.Side) + (QueueWords.Elapsed(w.WaitedS) is { } waited ? $", waited {waited}" : ", waited time not known"),
            w.State == "unknown")).ToList();
        var skipped = status.Records.Skipped > 0
            ? $"{status.Records.Skipped} {(status.Records.Skipped == 1 ? "record" : "records")} could not be read, so a job may be missing"
            : "";
        return (lines, lines.Count == 0 && skipped.Length == 0 ? NothingWaiting : skipped);
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (WaitingRow)d;
        var was = row.SpokenName;
        var (lines, notice) = Read(row.Status);
        row.SetValue(LinesKey, lines);
        row.SetValue(NoticeKey, notice);
        if (was != row.SpokenName && UIElementAutomationPeer.FromElement(row) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, was, row.SpokenName);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Said(this);

    private sealed class Said(WaitingRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.SpokenName;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(WaitingRow);
    }
}

/// <summary>One line of the waiting row: shown in its template, said once as a whole by a screen reader.</summary>
public class WaitingLineView : ContentControl
{
    static WaitingLineView() => FocusableProperty.OverrideMetadata(typeof(WaitingLineView), new FrameworkPropertyMetadata(false));

    protected override AutomationPeer OnCreateAutomationPeer() => new Said(this);

    private sealed class Said(WaitingLineView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => (owner.Content as WaitingLine)?.Spoken ?? "";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetClassNameCore() => nameof(WaitingLineView);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

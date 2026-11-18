using System.Globalization;
using Bosun.Core.Memory;
using Bosun.Core.Queue;
using Bosun.Panel.Controls;
using Bosun.Panel.Shell;
using Bosun.Panel.Tray;

namespace Bosun.Panel.Queue;

/// <summary>
/// The queue in a few words, for the tray's Queue line and the Queue page's live area (D03 T02 §3): "1 running, 2
/// waiting". "Idle" only when the status was read whole and nothing runs or waits; what the status could not tell (a
/// slot it could not check, records it could not read) is said, never taken as idle; null ("Not available") when the
/// status can't be read, with the reason in the Activity pane once each time it changes.
///
/// It never polls (design/SPEC.md §1: a hidden panel polls nothing). While the Queue page is on screen it takes the
/// page's own readings; otherwise it reads once when the tray's menu opens (<see cref="MenuOpening"/>), the menu
/// showing the last words at once.
/// </summary>
public sealed class QueueSummary(IQueueSource source, ActivityLog log) : IQueueSummary
{
    public const string Idle = "Idle";

    /// <summary>Before the first reading: the queue is being read, not unavailable (design/SPEC.md §1 keeps Not available for a value that can't be read).</summary>
    public const string Checking = "Checking";

    private string? _failure;
    private Task? _reading;

    /// <summary>The words now: <see cref="Checking"/> until the first reading, null while the queue can't be read.</summary>
    public string? Words { get; private set; } = Checking;

    /// <summary>Raised when <see cref="Words"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Whether the Queue page is on screen, reading every 5 s: its readings are then the words, and the menu reads nothing.</summary>
    public bool PageShowing { get; set; }

    /// <summary>The tray's menu is opening: one read, unless the page reads already or a read is under way.</summary>
    public void MenuOpening() => ReadOnce();

    /// <summary>
    /// One read, unless the page reads already or a read is under way: at each opening of the menu, and once as the
    /// panel starts, so its first opening has the queue's words (one read, never a poll).
    /// </summary>
    public void ReadOnce()
    {
        if (PageShowing || _reading is { IsCompleted: false }) return;
        _reading = ReadAsync();
    }

    private async Task ReadAsync()
    {
        Reading<StatusDoc> reading;
        try
        {
            reading = await source.StatusAsync();
        }
        // A reader that throws is a reading that failed, said like any other.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            reading = Reading<StatusDoc>.Fail(ReadingProblem.Failed, string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message);
        }
        Take(reading);
    }

    /// <summary>A reading of the status, the page's or its own, becomes the words.</summary>
    public void Take(Reading<StatusDoc> reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var words = reading.Value is { } status ? WordsOf(status) : null;
        if (reading.Value is null)
        {
            var why = reading.Why ?? "it could not be read";
            if (why != _failure) log.Warn($"The tray's Queue line says Not available: {why}");
            _failure = why;
        }
        else _failure = null;
        if (words == Words) return;
        Words = words;
        Changed?.Invoke();
    }

    /// <summary>The queue in words, from a status that was read: what holds a slot, what waits, and what can't be told.</summary>
    public static string WordsOf(StatusDoc status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var states = status.Slots.Select(QueueSlotTile.StateOf).ToList();
        var parts = new List<string>();
        void Count(int n, string words)
        {
            if (n > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{n} {words}"));
        }
        Count(states.Count(s => s == SlotState.Running), "running");
        Count(states.Count(s => s == SlotState.Starting), "starting");
        Count(states.Count(s => s == SlotState.WaitingForGuest), "waiting for guest memory");
        Count(states.Count(s => s == SlotState.Held), "held");
        if (status.Records.Readable) Count(status.Waiters.Count, "waiting");
        var unknown = states.Count(s => s == SlotState.NotAvailable);
        if (unknown > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{unknown} {(unknown == 1 ? "slot" : "slots")} not known"));
        if (!status.Records.Readable) parts.Add("records not readable");
        else if (status.Records.Skipped > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{status.Records.Skipped} {(status.Records.Skipped == 1 ? "record" : "records")} not readable"));
        return parts.Count == 0 ? Idle : string.Join(", ", parts);
    }

    /// <summary>Whether the status says, with nothing it could not tell, that no slot is held and nothing waits.</summary>
    public static bool IsIdle(StatusDoc status) => WordsOf(status) == Idle;

    /// <summary>
    /// What the status could not tell, a line each, for the page: records it could not read, a slot it could not check,
    /// a held slot with no holder record, and a slot above the limit a job still holds. Slots numbered from one.
    /// </summary>
    public static IReadOnlyList<string> Doubts(StatusDoc status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var said = new List<string>();
        if (!status.Records.Readable)
            said.Add($"The queue's records in {status.Records.Dir} could not be read, so who holds or waits for a slot can't be shown.");
        else if (status.Records.Skipped > 0)
            said.Add(string.Create(CultureInfo.InvariantCulture,
                $"{status.Records.Skipped} {(status.Records.Skipped == 1 ? "record" : "records")} could not be read, so a job may be missing; the jobs that were read are shown."));
        foreach (var slot in status.Slots)
        {
            var n = slot.Index + 1;
            switch (QueueSlotTile.StateOf(slot))
            {
                case SlotState.NotAvailable:
                    said.Add(string.Create(CultureInfo.InvariantCulture, $"Slot {n}: whether it is in use could not be checked."));
                    break;
                case SlotState.Held when slot.Holder is null:
                    said.Add(string.Create(CultureInfo.InvariantCulture, $"Slot {n} is held, but no record says which job holds it."));
                    break;
            }
            if (slot.AboveLimit)
                said.Add(string.Create(CultureInfo.InvariantCulture, $"Slot {n} is above the slot limit: its job keeps it until it ends, and no new job gets it."));
        }
        return said;
    }
}

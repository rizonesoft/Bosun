using System.Text.Json;
using Bosun.Core.Memory;
using Bosun.Core.Queue;

namespace Bosun.Panel.Tests.Queue;

/// <summary>The queue as <c>bosun</c> writes it, for the Queue page's tests (D03 T02 §3): status, job lines and limits as JSON, read by the real readers.</summary>
internal static class QueueFixtures
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A holder's record as <c>dm::status_json</c> writes it.</summary>
    internal static string Holder(int slot, string phase = "\"running\"", string repo = "\"Recto\"", string command = "cmake --build build", string state = "live") =>
        $$"""{"schema_version":1,"kind":"holder","pid":{{4120 + slot}},"process_start":133000000000,"started_at":"2026-11-18T08:50:00Z","command":"{{command}}","repo":{{repo}},"side":"windows","slot":{{slot}},"session":1,"ns":"Bosun","acquired_at":"2026-11-18T08:50:12Z","command_started_at":"2026-11-18T08:50:12Z","min_guest_mb":null,"state":"{{state}}","waited_s":12,"running_s":240,"phase":{{phase}}}""";

    /// <summary>A waiter's record as <c>dm::status_json</c> writes it.</summary>
    internal static string Waiter(int pid, string why, string floor = "null", string repo = "\"Office\"", string command = "dotnet test") =>
        $$"""{"schema_version":1,"kind":"waiter","pid":{{pid}},"process_start":133000000001,"started_at":"2026-11-18T08:55:00Z","command":"{{command}}","repo":{{repo}},"side":"windows","session":1,"ns":"Bosun","waiting_for":{{why}},"min_avail_mb":{{floor}},"max_wait_s":3600,"state":"live","waited_s":190}""";

    internal static string Free(int index, bool above = false) => $$"""{"index":{{index}},"busy":false,{{(above ? "\"above_limit\":true," : "")}}"holder":null}""";

    internal static string Held(int index, string? holder, bool? busy = true, bool above = false) =>
        $$"""{"index":{{index}},"busy":{{(busy is { } b ? (b ? "true" : "false") : "null")}},{{(above ? "\"above_limit\":true," : "")}}"holder":{{holder ?? "null"}}}""";

    internal static StatusDoc Status(string slots, string waiters = "", long available = 4608, long floor = 6144, bool readable = true, long skipped = 0) =>
        JsonSerializer.Deserialize($$$"""{"schema_version":1,"ns":"Bosun","at":"2026-11-18T09:00:00Z","slots":[{{{slots}}}],"waiters":[{{{waiters}}}],"memory":{"available_mb":{{{available}}},"total_mb":32601,"min_avail_mb":{{{floor}}}},"guest":null,"logger":{"windows":"running","wsl":null},"records":{"readable":{{{(readable ? "true" : "false")}}},"skipped":{{{skipped}}},"dir":"C:\\run"}}""",
            MemoryJson.Default.StatusDoc)!;

    /// <summary>Two free slots, nothing waiting, every record read: idle.</summary>
    internal static StatusDoc IdleStatus() => Status($"{Free(0)},{Free(1)}");

    /// <summary>Slot 1 running, slot 2 free, and a job waiting for memory then one for a slot.</summary>
    internal static StatusDoc BusyStatus() => Status($"{Held(0, Holder(0))},{Free(1)}",
        $"{Waiter(500, "\"memory\"", "6144", command: "dotnet build")},{Waiter(501, "\"slot\"", command: "dotnet test")}");

    /// <summary>One finished job as <c>job_line_json</c> writes it.</summary>
    internal static JobLineDoc Job(string command = "cmake --build build", int? slot = 0, double waited = 2, double ran = 30, long? exit = 0, bool timedOut = false,
        string side = "windows", string ended = "2026-11-18T08:40:00Z", long? endedMs = null, string? repo = "Recto") =>
        JsonSerializer.Deserialize($$"""{"schema_version":1,"started_at":"2026-11-18T08:39:00Z","ended_at":"{{ended}}","pid":1,"command":"{{command}}","repo":{{(repo is null ? "null" : $"\"{repo}\"")}},"side":"{{side}}","ns":"Bosun","slot":{{(slot?.ToString() ?? "null")}},"waited_s":{{waited.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"ran_s":{{ran.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"exit_code":{{(exit?.ToString() ?? "null")}},"timed_out_into_queue":{{(timedOut ? "true" : "false")}},"ended_ms":{{(endedMs?.ToString() ?? "null")}}}""",
            QueueJson.Default.JobLineDoc)!;

    /// <summary>One limit as <c>bosun config get --json</c> writes it.</summary>
    internal static string Limit(long value, string source, long? saved, long min, long max, long @default, string variable, string? variableError = null) =>
        $$"""{"value":{{value}},"source":"{{source}}","saved":{{(saved?.ToString() ?? "null")}},"min":{{min}},"max":{{max}},"default":{{@default}},"variable":"{{variable}}","variable_error":{{(variableError is null ? "null" : JsonSerializer.Serialize(variableError))}}}""";

    internal static string SlotsLimit(long value = 2, string source = "default", long? saved = null, string? variableError = null) =>
        Limit(value, source, saved, 1, 64, 2, "BOSUN_SLOTS", variableError);

    internal static string FloorLimit(long value = 6144, string source = "default", long? saved = null) => Limit(value, source, saved, 0, 1099511627776, 6144, "BOSUN_MIN_AVAIL_MB");

    internal static string WaitLimit(long value = 900, string source = "default", long? saved = null) => Limit(value, source, saved, 0, 604800, 900, "BOSUN_MAX_WAIT");

    internal static string GuestLimit(long value = 3072, string source = "default", long? saved = null) => Limit(value, source, saved, 0, 1099511627776, 3072, "BOSUN_MIN_GUEST_MB");

    internal static ConfigDoc Config(string? slots = null, string? floor = null, string? wait = null, string? guest = null, bool readable = true, bool valid = true,
        string? error = null, string refused = "") =>
        JsonSerializer.Deserialize($$"""{"schema_version":1,"path":"C:\\Users\\me\\AppData\\Local\\Bosun\\config.json","exists":true,"readable":{{(readable ? "true" : "false")}},"valid":{{(valid ? "true" : "false")}},"error":{{(error is null ? "null" : JsonSerializer.Serialize(error))}},"refused":[{{refused}}],"limits":{"slots":{{slots ?? SlotsLimit()}},"min_avail_mb":{{floor ?? FloorLimit()}},"max_wait_s":{{wait ?? WaitLimit()}},"min_guest_mb":{{guest ?? GuestLimit()}}""" + "}}",
            QueueJson.Default.ConfigDoc)!;
}

/// <summary>
/// A queue source the test sets: what each read answers, each of which it may hold, and what was asked. Saves change the
/// limits it answers next, as <c>bosun config</c> changes the file, unless the test says otherwise.
/// </summary>
internal sealed class FakeQueueSource : IQueueSource
{
    public Reading<StatusDoc> Status = Reading<StatusDoc>.Of(QueueFixtures.BusyStatus(), QueueFixtures.Now);
    public Reading<JobHistory> History = Reading<JobHistory>.Of(new JobHistory([QueueFixtures.Job()], 0, []), QueueFixtures.Now);
    public Reading<ConfigDoc> Limits = Reading<ConfigDoc>.Of(QueueFixtures.Config(), QueueFixtures.Now);

    /// <summary>The next status read waits for this.</summary>
    public TaskCompletionSource? HoldStatus;

    /// <summary>Every status read waits until it is cancelled: a page that stays loading, drawn again for each theme.</summary>
    public bool StatusNeverAnswers;

    /// <summary>The next limits reads wait for these, one each, in order.</summary>
    public readonly Queue<TaskCompletionSource> HoldLimits = new();

    /// <summary>Each save's answer by key; a key with none saves.</summary>
    public readonly Dictionary<string, Func<long, Reading<LimitSave>>> SaveAnswers = [];

    /// <summary>When set, the read-back answers this in place of the limits as saved.</summary>
    public Func<Reading<ConfigDoc>>? ReadBack;

    public int StatusReads, HistoryReads, LimitsReads;
    public readonly List<string> Calls = [];

    public async Task<Reading<StatusDoc>> StatusAsync(CancellationToken cancellationToken = default)
    {
        StatusReads++;
        var answer = Status;
        if (StatusNeverAnswers) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (HoldStatus is { } hold)
        {
            HoldStatus = null;
            await hold.Task.WaitAsync(cancellationToken);
        }
        return answer;
    }

    public Task<Reading<JobHistory>> HistoryAsync(int count, CancellationToken cancellationToken = default)
    {
        HistoryReads++;
        Calls.Add($"history {count}");
        return Task.FromResult(History);
    }

    public async Task<Reading<ConfigDoc>> LimitsAsync(CancellationToken cancellationToken = default)
    {
        LimitsReads++;
        Calls.Add("get");
        var answer = ReadBack is { } back && Calls.Any(c => c.StartsWith("set", StringComparison.Ordinal) || c.StartsWith("unset", StringComparison.Ordinal)) ? back() : Limits;
        if (HoldLimits.TryDequeue(out var hold)) await hold.Task.WaitAsync(cancellationToken);
        return answer;
    }

    public Task<Reading<LimitSave>> SetLimitAsync(string key, long value, CancellationToken cancellationToken = default)
    {
        Calls.Add($"set {key} {value}");
        if (SaveAnswers.TryGetValue(key, out var answer)) return Task.FromResult(answer(value));
        Change(key, value);
        return Task.FromResult(Reading<LimitSave>.Of(new LimitSave(key, LimitSaveOutcome.Saved, value, "saved"), QueueFixtures.Now));
    }

    public Task<Reading<LimitSave>> UnsetLimitAsync(string key, CancellationToken cancellationToken = default)
    {
        Calls.Add($"unset {key}");
        if (SaveAnswers.TryGetValue(key, out var answer)) return Task.FromResult(answer(0));
        Change(key, null);
        return Task.FromResult(Reading<LimitSave>.Of(new LimitSave(key, LimitSaveOutcome.Saved, null, "saved"), QueueFixtures.Now));
    }

    /// <summary>The limits the next read answers, with <paramref name="key"/> saved as <paramref name="value"/> (null: unset).</summary>
    private void Change(string key, long? value)
    {
        if (Limits.Value is not { } doc) return;
        var limits = doc.Limits.ToDictionary(p => p.Key, p => p.Value);
        var was = limits[key];
        limits[key] = was with
        {
            Saved = value,
            Value = was.Source == "environment" ? was.Value : value ?? was.Default,
            Source = was.Source == "environment" ? "environment" : value is null ? "default" : "file",
        };
        Limits = Reading<ConfigDoc>.Of(doc with { Limits = limits }, QueueFixtures.Now);
    }

    public static Reading<LimitSave> Refused(string key, string message) =>
        Reading<LimitSave>.Of(new LimitSave(key, LimitSaveOutcome.Refused, null, message), QueueFixtures.Now);
}

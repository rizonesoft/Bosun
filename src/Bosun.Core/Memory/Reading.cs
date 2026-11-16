namespace Bosun.Core.Memory;

/// <summary>Why a reading has no value (D03 T01 §1).</summary>
public enum ReadingProblem
{
    /// <summary><c>bosun.exe</c> is not where it is looked for.</summary>
    NotInstalled,

    /// <summary>The tool couldn't start, ran past its time, or ended with an error.</summary>
    Failed,

    /// <summary>The document is of a <c>schema_version</c> this panel doesn't read.</summary>
    Unsupported,

    /// <summary>What came back is not the document: not JSON, missing a field, or too large to read.</summary>
    Invalid,

    /// <summary>The file is not there: its logger has never written it, or not here.</summary>
    Missing,

    /// <summary>The file is there but can't be read: denied, or held by another for longer than a reader waits.</summary>
    Unreadable,
}

/// <summary>
/// What a page is given for one document (D03 T01 §1): the value, when it was read and how old it is, and whether it
/// is stale; or, with no value, which problem and why, in words for the page. Never an exception, and never a value
/// with a default standing for one that was not read.
/// </summary>
public sealed record Reading<T> where T : class
{
    private Reading(T? value, ReadingProblem? problem, string? why, DateTimeOffset? readAt, TimeSpan? age, bool stale) =>
        (Value, Problem, Why, ReadAt, Age, IsStale) = (value, problem, why, readAt, age, stale);

    /// <summary>The document, or null when there is none (<see cref="Problem"/> says why).</summary>
    public T? Value { get; }

    /// <summary>Why there is no value; null when there is one.</summary>
    public ReadingProblem? Problem { get; }

    /// <summary>The reason in words, for the page; for a stale value, how old it is.</summary>
    public string? Why { get; }

    /// <summary>When the client read it.</summary>
    public DateTimeOffset? ReadAt { get; }

    /// <summary>How old the data is, from the time it carries, where it carries one.</summary>
    public TimeSpan? Age { get; }

    /// <summary>The value is there but older than its side's limit: the last data, which a page shows as such.</summary>
    public bool IsStale { get; }

    public bool HasValue => Value is not null;

    public static Reading<T> Of(T value, DateTimeOffset readAt, TimeSpan? age = null) => new(value ?? throw new ArgumentNullException(nameof(value)), null, null, readAt, age, false);

    public static Reading<T> Stale(T value, DateTimeOffset readAt, TimeSpan age, string why) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), null, Words(why), readAt, age, true);

    public static Reading<T> Fail(ReadingProblem problem, string why) => new(null, problem, Words(why), null, null, false);

    private static string Words(string why) => string.IsNullOrWhiteSpace(why) ? throw new ArgumentException("a reading with no value, or a stale one, says why", nameof(why)) : why.Trim();
}

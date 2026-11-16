using System.Globalization;
using Bosun.Core.Memory;

namespace Bosun.Panel.Memory;

/// <summary>A range the history chart shows (D03 T01 §3, design/SPEC.md §3).</summary>
public sealed record HistoryRange(string Name, TimeSpan Length, TimeSpan Bucket)
{
    public static HistoryRange Day { get; } = new("24 hours", TimeSpan.FromHours(24), TimeSpan.FromMinutes(5));

    public static HistoryRange Week { get; } = new("7 days", TimeSpan.FromDays(7), TimeSpan.FromMinutes(30));

    public static HistoryRange Fortnight { get; } = new("14 days", TimeSpan.FromDays(14), TimeSpan.FromHours(1));

    public static IReadOnlyList<HistoryRange> All { get; } = [Day, Week, Fortnight];

    public override string ToString() => Name;
}

/// <summary>
/// One point of the chart and one row of its table: a bucket's start, and its lowest Windows available memory with the
/// minute it was at; both null when the bucket holds no Windows sample.
/// </summary>
public sealed record HistoryPoint(DateTime Start, DateTime? LowestAt, long? LowestMb)
{
    /// <summary>When the point is: its lowest minute, or the bucket's start when it has no sample.</summary>
    public DateTime At => LowestAt ?? Start;

    /// <summary>That time in local time, with its date, as the table says it.</summary>
    public string Time => At.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

    /// <summary>What the table sorts its Lowest column by: the value, a bucket with no sample after every value.</summary>
    public long LowestSort => LowestMb ?? long.MaxValue;

    /// <summary>Its lowest available in GB, or "No sample" for a bucket the logger did not measure.</summary>
    public string Lowest => LowestMb is { } mb ? (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : "No sample";
}

/// <summary>
/// The history chart's points (D03 T01 §3): the report's minutes put into the range's buckets, each bucket its lowest
/// Windows available memory and when, so the chart and its table are the same points and a short low is never averaged
/// away. A bucket with no Windows sample is missing, never 0, and the line breaks across it.
/// </summary>
public static class History
{
    /// <summary>A report minute ("2026-11-16T07:20", the logger's UTC) as a UTC time; null when it can't be read.</summary>
    public static DateTime? MinuteOf(string minute) =>
        DateTime.TryParseExact(minute, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at : null;

    /// <summary>
    /// The buckets that cover the whole of <paramref name="range"/> ending at <paramref name="end"/> (UTC), oldest
    /// first: from the bucket holding the range's first minute to the one holding its last, so a low anywhere in the
    /// range is in a bucket (completion review: the oldest part bucket was dropped). A minute outside the range is not.
    /// </summary>
    public static IReadOnlyList<HistoryPoint> Buckets(IEnumerable<SeriesPointDoc> series, HistoryRange range, DateTime end)
    {
        var ticks = range.Bucket.Ticks;
        var from = end - range.Length;
        var first = new DateTime(from.Ticks / ticks * ticks, DateTimeKind.Utc);
        var last = new DateTime(end.Ticks / ticks * ticks, DateTimeKind.Utc);
        var count = (int)((last - first).Ticks / ticks) + 1;
        var lows = new (DateTime? At, long? Mb)[count];
        foreach (var point in series)
        {
            if (point.WinAvailableMb is not { } mb || MinuteOf(point.Minute) is not { } at || at < from || at > end) continue;
            var i = (int)((at - first).Ticks / ticks);
            if (lows[i].Mb is not { } low || mb < low) lows[i] = (at, mb);
        }
        return [.. Enumerable.Range(0, count).Select(i => new HistoryPoint(first + TimeSpan.FromTicks(ticks * i), lows[i].At, lows[i].Mb))];
    }

    /// <summary>The lowest point of all, or null when none was measured.</summary>
    public static HistoryPoint? Lowest(IEnumerable<HistoryPoint> points) =>
        points.Where(p => p.LowestMb is not null).MinBy(p => p.LowestMb);
}

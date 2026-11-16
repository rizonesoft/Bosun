using System.Globalization;

namespace Bosun.Core.Memory;

/// <summary>What the Decision card can say.</summary>
public enum DecisionKind
{
    /// <summary>Too few days of Windows samples to say anything.</summary>
    NotYet,

    /// <summary>The evidence a verdict needs is incomplete or not measured: said, and no verdict.</summary>
    Withheld,

    /// <summary>The evidence says more memory would help.</summary>
    More,

    /// <summary>The evidence says the installed memory has been enough.</summary>
    Enough,
}

/// <summary>The Decision card's answer: its kind, its words, and every figure it rests on.</summary>
public sealed record Decision(DecisionKind Kind, string Verdict, IReadOnlyList<string> Evidence);

/// <summary>
/// The 64 GB question (D03 T01 §6, design/SPEC.md §3, README "evidence before hardware"), answered from a fortnight's
/// report. There is no written rule; the default taken, and recorded in the TODO, is: under 7 days with Windows samples,
/// no verdict; a history read incomplete, or a figure the verdict needs not measured, no verdict that rests on it; else
/// more memory would help when Windows was below the pressure line for any minute, committed memory reached 90% of its
/// limit, or WSL had out-of-memory kills in the period, and otherwise the installed memory has been enough. Changing the
/// rule is changing this one function and its tests.
/// </summary>
public static class DecisionRule
{
    public const int MinimumDays = 7;

    public const double CommitPressure = 0.9;

    public static Decision Decide(ReportDoc report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var w = report.Windows;
        var evidence = new List<string>();
        string Gb(long mb) => (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

        if (w.DaysWithSamples is not { } days)
            return new(DecisionKind.Withheld, "No verdict: this bosun.exe's report does not say how many days hold Windows samples.",
                ["Update the tools (pwsh scripts/install.ps1) so the report carries its evidence."]);
        evidence.Add($"{days} {(days == 1 ? "day" : "days")} of Windows samples");
        if (w.CapacitySince is { Length: > 0 } since && w.TotalMb is { } now)
            evidence.Add($"counted since the memory changed to {Gb(now)} ({since[..Math.Min(10, since.Length)]})");
        if (days < MinimumDays)
            return new(DecisionKind.NotYet, $"Not yet: {days} of the {MinimumDays} days a verdict needs are logged. Leave the logger running.", evidence);
        if (!report.Read.Complete)
            return new(DecisionKind.Withheld,
                $"No verdict: the history is incomplete ({report.Read.UnreadableFiles} files could not be read, {report.Read.RejectedRows} rows rejected).", evidence);

        var reasons = new List<string>();
        var unknown = new List<string>();
        if (w.LowMinutes is { } minutes)
        {
            evidence.Add($"{minutes} {(minutes == 1 ? "minute" : "minutes")} below the {Gb(report.LowMb)} pressure line");
            if (minutes > 0) reasons.Add("Windows ran below the pressure line");
        }
        else unknown.Add("minutes below the pressure line");
        if (w.PeakCommitRatio is { } ratio)
        {
            evidence.Add($"committed memory peaked at {(ratio * 100).ToString("0", CultureInfo.InvariantCulture)}% of its limit");
            if (ratio >= CommitPressure) reasons.Add("committed memory reached 90% of its limit");
        }
        else unknown.Add("the peak commit");
        if (report.Wsl.OomKillsInPeriod is { } kills)
        {
            var from = report.Wsl.OomBaselineKnown == false ? " (counted from the first sample in the period)" : "";
            evidence.Add($"{kills} WSL out-of-memory {(kills == 1 ? "kill" : "kills")}{from}");
            if (kills > 0) reasons.Add("WSL killed processes for memory");
        }
        else if (report.Wsl.Samples > 0) unknown.Add("WSL out-of-memory kills");

        if (reasons.Count > 0) return new(DecisionKind.More, $"More memory would help: {string.Join("; ", reasons)}.", evidence);
        if (unknown.Count > 0) return new(DecisionKind.Withheld, $"No verdict: {string.Join(" and ", unknown)} not measured.", evidence);
        var installed = w.TotalMb is { } total ? Gb(total) : "The installed memory";
        return new(DecisionKind.Enough, $"{installed} has been enough so far.", evidence);
    }
}

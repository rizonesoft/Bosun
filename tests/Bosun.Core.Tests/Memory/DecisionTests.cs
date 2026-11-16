using System.Text.Json;
using Bosun.Core.Memory;
using Xunit;

namespace Bosun.Core.Tests.Memory;

/// <summary>
/// The 64 GB question (D03 T01 §6): no verdict from fewer than 7 days of Windows samples, an incomplete history or an
/// unmeasured figure; more memory for minutes below the line, 90% commit or WSL out-of-memory kills; else the installed
/// memory has been enough, named by its own total. Each verdict carries its evidence.
/// </summary>
public class DecisionTests
{
    static ReportDoc Report(long? days = 14, bool complete = true, long? rejected = null, long? lowMinutes = 0, double? commit = 0.6, long? oom = 0, bool? baseline = true,
        long? total = 32768, string? since = null, long wslSamples = 100, long lowMb = 2048) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema_version"] = 1, ["generated_at"] = "2026-11-16T09:00:00Z", ["since"] = "2026-11-02T09:00:00Z", ["days_requested"] = 14, ["days_covered"] = 14,
            ["first_sample"] = null, ["last_sample"] = null, ["low_mb"] = lowMb,
            ["read"] = new Dictionary<string, object> { ["complete"] = complete, ["files"] = 28, ["unreadable_files"] = complete ? 0 : 1, ["rejected_rows"] = rejected ?? (complete ? 0 : 7) },
            ["windows"] = new Dictionary<string, object?>
            {
                ["samples"] = 1000, ["low_samples"] = 0, ["min_available_mb"] = 3000, ["worst_minute"] = null, ["worst_repos"] = Array.Empty<object>(),
                ["max_commit_mb"] = 20000, ["commit_limit_mb"] = 40000, ["max_pages_in_per_sec"] = 10,
                ["low_minutes"] = lowMinutes, ["days_with_samples"] = days, ["peak_commit_ratio"] = commit, ["total_mb"] = total, ["capacity_since"] = since,
            },
            ["wsl"] = new Dictionary<string, object?>
            {
                ["samples"] = wslSamples, ["min_available_mb"] = 4000, ["max_swap_mb"] = 0, ["max_psi_full_avg10_x100"] = 0, ["max_oom_kills"] = 0,
                ["oom_kills_in_period"] = oom, ["oom_baseline_known"] = baseline,
            },
            ["builds"] = new Dictionary<string, object?> { ["jobs"] = 0, ["max_overlap"] = 0, ["at"] = null },
            ["active_repos"] = new Dictionary<string, object?> { ["max"] = 0, ["minute"] = null, ["total_private_mb"] = 0 },
            ["repos"] = Array.Empty<object>(), ["series"] = Array.Empty<object>(),
        }), MemoryJson.Default.ReportDoc)!;

    [Fact]
    public void Fewer_than_7_days_of_Windows_samples_gives_no_verdict_and_says_how_many()
    {
        var d = DecisionRule.Decide(Report(days: 6, lowMinutes: 500));                      // even with pressure
        Assert.Equal(DecisionKind.NotYet, d.Kind);
        Assert.Equal("Not yet: 6 of the 7 days a verdict needs are logged. Leave the logger running.", d.Verdict);
        Assert.Contains("6 days of Windows samples", d.Evidence);
    }

    [Fact]
    public void An_incomplete_history_or_an_unmeasured_figure_withholds_the_verdict_that_needs_it()
    {
        Assert.Equal(DecisionKind.Withheld, DecisionRule.Decide(Report(complete: false)).Kind);
        // Rows rejected while the read still counts as complete: samples were thrown away, so no verdict either.
        var rejected = DecisionRule.Decide(Report(rejected: 1));
        Assert.Equal((DecisionKind.Withheld, "No verdict: the history is incomplete: 0 files could not be read and 1 row was rejected."), (rejected.Kind, rejected.Verdict));
        var unmeasured = DecisionRule.Decide(Report(lowMinutes: null));
        Assert.Equal((DecisionKind.Withheld, "No verdict: minutes below the pressure line not measured."), (unmeasured.Kind, unmeasured.Verdict));
        Assert.Equal(DecisionKind.Withheld, DecisionRule.Decide(Report(commit: null)).Kind);
        Assert.Equal(DecisionKind.Withheld, DecisionRule.Decide(Report(days: null)).Kind);     // an older bosun.exe
        // A reason that was measured still stands beside one that wasn't.
        var partly = DecisionRule.Decide(Report(lowMinutes: 12, commit: null));
        Assert.Equal(DecisionKind.More, partly.Kind);
        Assert.Contains("the peak commit: not measured", partly.Evidence);                  // said, though the verdict stands
    }

    [Theory]
    [InlineData(12, 0.6, 0, "More memory would help: Windows ran below the pressure line.")]
    [InlineData(0, 0.93, 0, "More memory would help: committed memory reached 90% of its limit.")]
    [InlineData(0, 0.6, 2, "More memory would help: WSL killed processes for memory.")]
    public void Each_reason_recommends_more_memory_with_its_evidence(long minutes, double commit, long oom, string verdict)
    {
        var d = DecisionRule.Decide(Report(lowMinutes: minutes, commit: commit, oom: oom));
        Assert.Equal((DecisionKind.More, verdict), (d.Kind, d.Verdict));
        Assert.Contains($"{minutes} {(minutes == 1 ? "minute" : "minutes")} below the 2.0 GB pressure line", d.Evidence);
        Assert.Contains(FormattableString.Invariant($"committed memory peaked at {commit * 100:0.0}% of its limit"), d.Evidence);
    }

    [Fact]
    public void Enough_names_the_machine_by_its_own_total_and_an_upgrade_is_said()
    {
        Assert.Equal("32.0 GB has been enough so far.", DecisionRule.Decide(Report()).Verdict);
        var bigger = DecisionRule.Decide(Report(total: 65536, since: "2026-11-05T10:00:05"));
        Assert.Equal("64.0 GB has been enough so far.", bigger.Verdict);
        Assert.Contains("counted since the memory changed to 64.0 GB (2026-11-05)", bigger.Evidence);
        Assert.Equal("The installed memory has been enough so far.", DecisionRule.Decide(Report(total: null)).Verdict);
    }

    [Fact]
    public void A_commit_just_under_the_trigger_is_never_shown_as_the_trigger()
    {
        var d = DecisionRule.Decide(Report(commit: 0.8999));
        Assert.Equal(DecisionKind.Enough, d.Kind);
        Assert.Contains("committed memory peaked at 89.9% of its limit", d.Evidence);
        Assert.Contains("committed memory peaked at 90.0% of its limit", DecisionRule.Decide(Report(commit: 0.9)).Evidence);
    }

    [Fact]
    public void OOM_kills_counted_without_a_baseline_say_so()
    {
        var d = DecisionRule.Decide(Report(oom: 1, baseline: false));
        Assert.Contains("1 WSL out-of-memory kill (counted from the first sample in the period)", d.Evidence);
        Assert.Equal(DecisionKind.Enough, DecisionRule.Decide(Report(oom: null, wslSamples: 0)).Kind); // no WSL at all: nothing withheld
    }
}

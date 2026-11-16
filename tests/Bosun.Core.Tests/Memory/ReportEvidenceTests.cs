using System.Globalization;
using Bosun.Core.Memory;
using Xunit;

namespace Bosun.Core.Tests.Memory;

/// <summary>
/// The decision end to end (D03 T01 §6): the built bosun.exe reports over log files this test writes, MemoryClient reads
/// it with the floor passed as --low-mb, and the rule gives its verdict, so a field renamed or dropped at any layer fails.
/// </summary>
public sealed class ReportEvidenceTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "bosun-evidence-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private static string Bosun()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Bosun.slnx")) && File.Exists(Path.Combine(dir.FullName, "design", "tokens", "colors.json")))
            {
                var exe = Path.Combine(dir.FullName, "build", "Release", "bosun.exe");
                Assert.True(File.Exists(exe), $"the native tools aren't built: {exe} is missing (pwsh scripts\\build.ps1)");
                return exe;
            }
        throw new InvalidOperationException("no checkout above " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task Eight_days_with_a_low_minute_below_the_given_floor_read_as_more_memory()
    {
        var logs = Path.Combine(_home, "Bosun", "logs");
        Directory.CreateDirectory(logs);
        var now = DateTime.UtcNow;
        for (var d = 1; d <= 8; d++)
        {
            var day = now.AddDays(-d).Date.AddHours(12);
            var rows = new List<string> { "ts,side,kind,name,count,private_mb,rss_mb" };
            string Ts(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            rows.Add($"{Ts(day)},win,sys,available_mb,0,{(d == 3 ? 2900 : 9000)},0");                        // below 3000 one minute
            rows.Add($"{Ts(day)},win,sys,commit_mb,0,20000,0");
            rows.Add($"{Ts(day)},win,sys,commit_limit_mb,0,40000,0");
            rows.Add($"{Ts(day)},win,sys,total_mb,0,32768,0");
            File.WriteAllLines(Path.Combine(logs, $"windows-{day:yyyy-MM-dd}.csv"), rows);
        }
        var client = new MemoryClient(Bosun(), "", logs, environment: new Dictionary<string, string?> { ["LOCALAPPDATA"] = _home });
        var reading = await client.ReportAsync(14, 3000, TestContext.Current.CancellationToken);
        Assert.True(reading.Value is not null, reading.Why);
        var report = reading.Value!;
        Assert.Equal(3000, report.LowMb);
        Assert.Equal((long?)1, report.Windows.LowMinutes);
        Assert.Equal((long?)8, report.Windows.DaysWithSamples);
        Assert.Equal((long?)32768, report.Windows.TotalMb);
        Assert.Equal(0.5, report.Windows.PeakCommitRatio);
        var decision = DecisionRule.Decide(report);
        Assert.Equal((DecisionKind.More, "More memory would help: Windows ran below the pressure line."), (decision.Kind, decision.Verdict));

        var lower = DecisionRule.Decide((await client.ReportAsync(14, 2048, TestContext.Current.CancellationToken)).Value!);              // the same logs, a lower floor
        Assert.Equal("32.0 GB has been enough so far.", lower.Verdict);
    }
}

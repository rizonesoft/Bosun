using System.Text;
using Bosun.Core.Memory;
using Bosun.Core.Tests.Settings;
using Xunit;

namespace Bosun.Core.Tests.Memory;

/// <summary>
/// The policy the WSL logger reads (D05 T02 §5): written by the code the Settings save calls, read back, and the file
/// the Python tests read (tests/fixtures/pressure-policy.json) is exactly what that code writes, so the two sides agree on
/// names and units.
/// </summary>
public class PressurePolicyTests
{
    private static readonly PanelSettings Chosen = PanelSettings.Defaults with { AutoThrottleJobs = true, PressureFloorMb = 3072, JobLimitMb = 6144 };

    private static string Fixture()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Bosun.slnx")) && File.Exists(Path.Combine(dir.FullName, "design", "tokens", "colors.json")))
                return Path.Combine(dir.FullName, "tests", "fixtures", PressurePolicy.FileName);
        throw new InvalidOperationException("no checkout above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_policy_follows_the_settings_with_the_panels_recovery_band_and_is_off_by_default()
    {
        Assert.Equal(new PressurePolicy(false, 2048, 1024, 4096), PressurePolicy.From(PanelSettings.Defaults));
        Assert.Equal(new PressurePolicy(true, 3072, PressureWatch.Hysteresis, 6144), PressurePolicy.From(Chosen));
    }

    [Fact]
    public void The_save_path_writes_the_fixture_the_python_tests_read_and_it_reads_back()
    {
        using var scratch = new Scratch();
        Assert.True(PressurePolicy.Write(scratch.Path, Chosen).Succeeded);
        Assert.Equal([PressurePolicy.FileName], Directory.GetFiles(scratch.Path).Select(Path.GetFileName)); // and no temporary file is left

        var written = File.ReadAllBytes(Path.Combine(scratch.Path, PressurePolicy.FileName));
        Assert.True(PressurePolicy.TryParse(written, out var read, out var fault), fault);
        Assert.Equal(PressurePolicy.From(Chosen), read);

        var committed = File.ReadAllText(Fixture()).Replace("\r\n", "\n"); // a checkout may have made the line ends CRLF
        var expected = Encoding.UTF8.GetString(written);
        Assert.True(committed == expected, $"tests/fixtures/pressure-policy.json is not what the save path writes. It must hold exactly:\n{expected}");
    }

    [Fact]
    public void A_later_write_replaces_the_earlier_one_whole()
    {
        using var scratch = new Scratch();
        Assert.True(PressurePolicy.Write(scratch.Path, Chosen).Succeeded);
        Assert.True(PressurePolicy.Write(scratch.Path, Chosen with { AutoThrottleJobs = false }).Succeeded);
        Assert.True(PressurePolicy.TryParse(File.ReadAllBytes(Path.Combine(scratch.Path, PressurePolicy.FileName)), out var read, out _));
        Assert.False(read.Enabled);
    }

    [Fact]
    public void A_file_already_at_the_old_fixed_temporary_name_is_never_opened_or_written()
    {
        using var scratch = new Scratch();
        var old = Path.Combine(scratch.Path, PressurePolicy.FileName + ".tmp");
        File.WriteAllText(old, "someone else's file");

        Assert.True(PressurePolicy.Write(scratch.Path, Chosen).Succeeded);

        Assert.Equal("someone else's file", File.ReadAllText(old));
        Assert.True(PressurePolicy.TryParse(File.ReadAllBytes(Path.Combine(scratch.Path, PressurePolicy.FileName)), out var read, out _));
        Assert.Equal(PressurePolicy.From(Chosen), read);
    }

    [Fact]
    public void The_temporary_file_is_made_new_and_never_over_an_existing_path()
    {
        using var scratch = new Scratch();
        var taken = Path.Combine(scratch.Path, "taken");
        File.WriteAllText(taken, "kept");
        Assert.Throws<IOException>(() => new PanelSettingsFiles().WriteNew(taken, [1, 2, 3]));
        Assert.Equal("kept", File.ReadAllText(taken));

        var names = new List<string>();
        var recording = new Recording(names);
        Assert.True(PressurePolicy.Write(scratch.Path, Chosen, recording).Succeeded);
        Assert.True(PressurePolicy.Write(scratch.Path, Chosen, recording).Succeeded);
        Assert.Equal(2, names.Distinct().Count());                       // a fresh name each time
        Assert.DoesNotContain(PressurePolicy.FileName + ".tmp", names.Select(Path.GetFileName));
    }

    private sealed class Recording(List<string> names) : PanelSettingsFiles
    {
        public override void WriteNew(string path, byte[] bytes)
        {
            names.Add(path);
            base.WriteNew(path, bytes);
        }
    }

    private sealed class Failing : PanelSettingsFiles
    {
        public string? Fail;

        public override void Replace(string from, string to)
        {
            if (Fail == "replace") throw new UnauthorizedAccessException("the file is held");
            base.Replace(from, to);
        }

        public override byte[] Read(string path, int limit) =>
            Fail == "read-back" && path.EndsWith(".tmp", StringComparison.Ordinal) ? [.. base.Read(path, limit), (byte)'x'] : base.Read(path, limit);
    }

    [Theory]
    [InlineData("replace", "the file is held")]
    [InlineData("read-back", "did not read back")]
    public void A_write_that_fails_says_why_and_leaves_the_policy_in_place_as_it_was(string fail, string why)
    {
        using var scratch = new Scratch();
        Assert.True(PressurePolicy.Write(scratch.Path, Chosen).Succeeded); // enabled, as the logger holds it
        var before = File.ReadAllBytes(Path.Combine(scratch.Path, PressurePolicy.FileName));

        var outcome = PressurePolicy.Write(scratch.Path, Chosen with { AutoThrottleJobs = false }, new Failing { Fail = fail });

        Assert.False(outcome.Succeeded);
        Assert.Contains(why, outcome.Reason);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(scratch.Path, PressurePolicy.FileName)));
        Assert.Equal([PressurePolicy.FileName], Directory.GetFiles(scratch.Path).Select(Path.GetFileName)); // the temporary file is gone
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"schema_version\":2,\"enabled\":true,\"floor_mb\":1,\"recovery_band_mb\":1,\"job_limit_mb\":1}")]
    [InlineData("{\"schema_version\":1,\"enabled\":\"yes\",\"floor_mb\":1,\"recovery_band_mb\":1,\"job_limit_mb\":1}")]
    [InlineData("{\"schema_version\":1,\"enabled\":true,\"floor_mb\":1.5,\"recovery_band_mb\":1,\"job_limit_mb\":1}")]
    [InlineData("{\"schema_version\":1,\"enabled\":true,\"floor_mb\":1,\"recovery_band_mb\":1}")]
    public void A_file_that_is_not_a_policy_is_refused_with_why(string text)
    {
        Assert.False(PressurePolicy.TryParse(Encoding.UTF8.GetBytes(text), out _, out var fault));
        Assert.NotEqual("", fault);
    }
}

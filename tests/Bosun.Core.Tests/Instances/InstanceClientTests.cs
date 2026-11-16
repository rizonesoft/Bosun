using System.Text.Json;
using System.Text.Json.Nodes;
using Bosun.Core.Instances;
using Bosun.Core.Memory;
using Xunit;

namespace Bosun.Core.Tests.Instances;

/// <summary>
/// The instance reader (D03 T02 §1): Windows' document and each running distro's merged by repo (a WSL repo under
/// <c>/mnt/&lt;drive&gt;/</c> joining the Windows one, two distros' same path kept apart, a WSL job's two halves joined),
/// a distro never started to be asked, a side that failed keeping the others' rows with its reason, and a document that
/// isn't the shape refused with why. bosun.exe and wsl.exe are the test child's script mode, which logs every call.
/// </summary>
public sealed class InstanceClientTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bosun-instances-" + Guid.NewGuid().ToString("N")[..10]);

    public InstanceClientTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // A file the child held a moment ago: the folder is the system's temp, and goes with it.
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "instances", name);

    private static string Child()
    {
        var mine = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return Path.Combine(mine.Parent!.Parent!.FullName, "Bosun.TestChild", mine.Name, "Bosun.TestChild.exe");
    }

    private string Log => Path.Combine(_work, "calls.jsonl");

    private string Write(string name, string text)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static object Answer(string? file = null, int exit = 0, string? stderr = null) => new Dictionary<string, object?> { ["answer"] = file, ["exit"] = exit, ["stderr"] = stderr };

    private const string Windows = "instances --json";
    private const string List = "--list --running --quiet";

    private static string Guest(string distro) => $"-d {distro} -- bosun instances --json";

    /// <summary>A client whose bosun.exe and wsl.exe are the fake, answering as <paramref name="script"/> says.</summary>
    private InstanceClient Fake(Dictionary<string, object> script, bool wsl = true, string? bosun = "") =>
        new(bosun is null ? null : Child(), "bosun.exe is not installed at C:\\x\\bosun.exe.", wsl ? Child() : null,
            environment: new Dictionary<string, string?> { ["BOSUN_FAKE_SCRIPT"] = JsonSerializer.Serialize(script), ["BOSUN_FAKE_LOG"] = Log });

    private string[][] Calls() => File.Exists(Log) ? File.ReadAllLines(Log).Select(l => JsonSerializer.Deserialize<string[]>(l)!).ToArray() : [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_running_distro_and_Windows_merge_by_repo_with_a_WSL_job_s_halves_joined()
    {
        var view = await Fake(new()
        {
            [Windows] = Answer(Fixture("windows.json")),
            [List] = Answer(Write("running.txt", "Ubuntu\r\nDebian\r\n")),
            [Guest("Ubuntu")] = Answer(Fixture("wsl-ubuntu.json")),
            [Guest("Debian")] = Answer(Fixture("wsl-debian.json")),
        }).ReadAsync(Token);

        Assert.Null(view.WslNotice);
        Assert.Equal(["Windows", "WSL (Ubuntu)", "WSL (Debian)"], view.Sides.Select(s => s.Name));
        Assert.All(view.Sides, s => Assert.True(s.Reading.HasValue, s.Reading.Why));

        // D:/Bosun holds Windows' claude, codex and cmake job, Ubuntu's claude in /mnt/d/bosun (letter case aside), and the
        // WSL job joined: memory and folder from Ubuntu, slot and state from the Windows holder.
        var bosun = Assert.Single(view.Groups, g => g.Repo == "D:/Bosun");
        Assert.Null(bosun.Distro);
        Assert.Equal([("claude", 87544L), ("claude", 4024116L), ("codex", 6060L), ("job", 4321L), ("job", 9100L)],
            bosun.Rows.Select(r => (r.Instance.Kind, r.Instance.Pid)).OrderBy(x => x.Kind).ThenBy(x => x.Pid));
        var job = bosun.Rows.Single(r => r.Instance.Pid == 4321).Instance;
        Assert.Equal((1, "working", 900L, "/mnt/d/bosun", "make -j8", 4321L), (job.Slot, job.State, job.PrivateMb, job.Cwd, job.Command, job.GuestPid));
        Assert.Equal("Ubuntu", bosun.Rows.Single(r => r.Instance.Pid == 4321).Distro);
        Assert.DoesNotContain(view.Groups.SelectMany(g => g.Rows), r => r.Instance.Pid == 9200); // the holder is shown as its half

        // The same Linux path in two distros is two repos, each with its distro.
        var apps = view.Groups.Where(g => g.Repo == "/home/dev/projects/app").ToList();
        Assert.Equal(["Debian", "Ubuntu"], apps.Select(g => g.Distro).Order());
        Assert.Equal("claude", Assert.Single(apps.Single(g => g.Distro == "Debian").Rows).Instance.Kind);

        // Outside any checkout: one group, last.
        var loose = view.Groups[^1];
        Assert.Null(loose.Repo);
        Assert.Equal([31500L, 3346257L], loose.Rows.Select(r => r.Instance.Pid).Order());

        // Each side's problems and unreadable count stay with its reading.
        var ubuntu = view.Sides[1].Reading.Value!;
        Assert.Equal(("sessions", 1L), (Assert.Single(ubuntu.Problems).Source, ubuntu.Unreadable));
    }

    [Theory]
    [InlineData("", 0)]                                         // an empty list
    [InlineData("There are no running distributions.\r\n", 1)] // as wsl.exe says it, exiting 1
    public async Task No_distro_running_starts_none_and_keeps_the_Windows_rows_with_a_notice(string listed, int exit)
    {
        var view = await Fake(new() { [Windows] = Answer(Fixture("windows.json")), [List] = Answer(Write("running.txt", listed), exit) }).ReadAsync(Token);
        Assert.Equal([["--list", "--running", "--quiet"], ["instances", "--json"]], Calls().OrderBy(c => c[0], StringComparer.Ordinal)); // no guest command
        Assert.StartsWith("No WSL distro is", view.WslNotice);
        Assert.Contains("only Windows is listed", view.WslNotice);
        Assert.Equal(["Windows"], view.Sides.Select(s => s.Name));
        Assert.Contains(view.Groups, g => g.Repo == "D:/Bosun");
    }

    [Fact]
    public async Task WSL_not_installed_is_said_and_never_asked()
    {
        var view = await Fake(new() { [Windows] = Answer(Fixture("windows.json")) }, wsl: false).ReadAsync(Token);
        Assert.Equal("WSL is not installed, so only Windows is listed.", view.WslNotice);
        Assert.Equal([["instances", "--json"]], Calls());
    }

    [Fact]
    public async Task A_side_that_fails_keeps_the_others_rows_and_says_why()
    {
        var view = await Fake(new()
        {
            [Windows] = Answer(Fixture("windows.json")),
            [List] = Answer(Write("running.txt", "Ubuntu\nDebian\n")),
            [Guest("Ubuntu")] = Answer(exit: 127, stderr: "bash: bosun: command not found"),
            [Guest("Debian")] = Answer(Fixture("wsl-debian.json")),
        }).ReadAsync(Token);
        var ubuntu = view.Sides.Single(s => s.Distro == "Ubuntu").Reading;
        Assert.Equal(ReadingProblem.Failed, ubuntu.Problem);
        Assert.Equal("bosun instances --json in WSL (Ubuntu) ended with exit code 127: bash: bosun: command not found", ubuntu.Why);
        Assert.Contains(view.Groups, g => g.Repo == "D:/Bosun");
        Assert.Contains(view.Groups, g => g.Distro == "Debian");
        // With Ubuntu's half missing, the WSL holder is still shown, as Windows saw it.
        Assert.Contains(view.Groups.SelectMany(g => g.Rows), r => r.Instance.Pid == 9200 && r.Instance.Slot == 1);

        // And the reverse: Windows failed, the distro's rows kept.
        var reverse = await Fake(new()
        {
            [Windows] = Answer(exit: 2, stderr: "[bosun] the process list can't be taken"),
            [List] = Answer(Write("one.txt", "Debian\n")),
            [Guest("Debian")] = Answer(Fixture("wsl-debian.json")),
        }).ReadAsync(Token);
        Assert.Equal(ReadingProblem.Failed, reverse.Sides[0].Reading.Problem);
        Assert.Contains("the process list can't be taken", reverse.Sides[0].Reading.Why);
        Assert.Equal("Debian", Assert.Single(reverse.Groups).Distro);

        // No bosun.exe: said, and WSL still asked.
        var missing = await Fake(new() { [List] = Answer(Write("none.txt", "")) }, bosun: null).ReadAsync(Token);
        Assert.Equal((ReadingProblem.NotInstalled, "bosun.exe is not installed at C:\\x\\bosun.exe."), (missing.Sides[0].Reading.Problem, missing.Sides[0].Reading.Why));
    }

    public static TheoryData<string, string> NotTheShape => new()
    {
        { "kind", "is of kind 'vim', which no scanner writes" },
        { "state", "state is 'busy', which no scanner writes" },
        { "missing", "missing required properties" },
        { "side", "wrote the wsl document, not the windows one" },
        { "source", "names the source 'disk', which no scanner reads" },
        { "foreign", "is a wsl claude in the windows document" },
        { "text", "is not its document" },
    };

    [Theory]
    [MemberData(nameof(NotTheShape))]
    public async Task A_document_that_isn_t_the_shape_is_refused_with_why(string wrong, string said)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Fixture("windows.json")))!.AsObject();
        var first = doc["instances"]![1]!.AsObject();
        switch (wrong)
        {
            case "kind": first["kind"] = "vim"; break;
            case "state": first["state"] = "busy"; break;
            case "missing": first.Remove("cwd"); break;
            case "side": doc["side"] = "wsl"; break;
            case "source": doc["problems"] = new JsonArray(new JsonObject { ["source"] = "disk", ["why"] = "x" }); break;
            case "foreign": first["side"] = "wsl"; break;
        }
        var text = wrong == "text" ? "{not json" : doc.ToJsonString();
        var view = await Fake(new() { [Windows] = Answer(Write("doc.json", text)) }, wsl: false).ReadAsync(Token);
        var reading = view.Sides[0].Reading;
        Assert.Equal(ReadingProblem.Invalid, reading.Problem);
        Assert.Contains(said, reading.Why);
        Assert.Empty(view.Groups);
    }

    [Fact]
    public async Task Another_schema_version_is_unsupported()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Fixture("windows.json")))!.AsObject();
        doc["schema_version"] = 2;
        var view = await Fake(new() { [Windows] = Answer(Write("v2.json", doc.ToJsonString())) }, wsl: false).ReadAsync(Token);
        Assert.Equal(ReadingProblem.Unsupported, view.Sides[0].Reading.Problem);
    }
}

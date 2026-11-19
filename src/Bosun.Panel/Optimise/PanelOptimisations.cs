using System.IO;
using Bosun.Core.Optimise;

namespace Bosun.Panel.Optimise;

/// <summary>
/// The panel's registry of optimisation items (D05 T02 §6), resolved by the panel and by the elevated helper alike.
/// D05 T02 §3 registers the real items here.
///
/// A test registration: when <see cref="TestItemVariable"/> names a file, one disposable item whose value is that file
/// is registered as <see cref="TestFileItem.ItemId"/>, so a test can run the real executable against a setting that
/// touches nothing of the machine. <see cref="TestModeVariable"/> makes it misbehave on purpose. The test item only adds
/// one harmless item whose value is a file the test names: it grants nothing, and nothing in the executable lets a
/// variable stand in for the helper's elevation, which is only ever the process token's (see
/// <see cref="WindowsHelperStanding"/>). Neither variable is set in a normal start, and without
/// <see cref="TestItemVariable"/> nothing of the test registration exists.
/// </summary>
public static class PanelOptimisations
{
    public const string TestItemVariable = "BOSUN_PANEL_TEST_ITEM";
    public const string TestModeVariable = "BOSUN_PANEL_TEST_ITEM_MODE";

    public static OptimisationRegistry Registry(Func<string, string?> environment)
    {
        var registry = new OptimisationRegistry();
        if (environment(TestItemVariable) is { Length: > 0 } file) registry.Register(new TestFileItem(file, environment(TestModeVariable)));
        return registry;
    }
}

/// <summary>
/// The disposable item of the test registration: its value is the text of one file. <paramref name="mode"/> makes it
/// fail the way a test needs: <c>apply-throws</c> (Apply throws without writing), <c>verify-fails</c> (Verify reads another value), <c>explode</c> (its title throws, a
/// fault outside any item's own handling), <c>die-before-backup</c>, <c>die-before-write</c> and <c>die-after-write</c>
/// (the process ends abruptly at that point, as a lost helper does).
/// </summary>
internal sealed class TestFileItem(string path, string? mode = null) : IOptimisation
{
    public const string ItemId = "test.disposable";
    public const string AppliedValue = "applied";

    public string Id => ItemId;

    public string Title => mode == "explode" ? throw new InvalidOperationException("the test item's title faults") : "Test setting";

    public string Target => "test:disposable-file";

    public bool NeedsElevation => true;

    public Fact<string> Read()
    {
        try
        {
            return Fact<string>.Read(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fact<string>.Unavailable(e.Message);
        }
    }

    public Fact<Proposal> Propose()
    {
        if (mode == "die-before-backup") Environment.Exit(9);
        var now = Read();
        return now.IsRead ? Fact<Proposal>.Read(new Proposal(Target, now.Value!, AppliedValue)) : Fact<Proposal>.Unavailable(now.Reason!);
    }

    public void Apply(string expectedBefore, string after)
    {
        if (mode == "apply-throws") throw new IOException("the test item refuses to write");
        if (mode == "die-before-write") Environment.Exit(9);
        Write(expectedBefore, after);
        if (mode == "die-after-write") Environment.Exit(9);
    }

    public Fact<string> Verify(Proposal proposal) => mode == "verify-fails" ? Fact<string>.Read("unexpected") : Read();

    public void Restore(string expectedCurrent, string value) => Write(expectedCurrent, value);

    private void Write(string expected, string value)
    {
        var now = Read();
        if (!now.IsRead || now.Value != expected) throw new ChangeConflictException($"it reads '{now.Value}', not '{expected}'");
        File.WriteAllText(path, value);
    }
}

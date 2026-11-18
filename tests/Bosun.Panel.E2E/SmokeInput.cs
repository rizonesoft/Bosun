using System.Runtime.CompilerServices;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// The smoke's real input is opt-in (D02 T02 §12, the operator's decision of 2026-11-18): a test that sends keys,
/// clicks, drags or pointer moves, or takes the foreground, runs only when <c>BOSUN_SMOKE_INPUT</c> is <c>1</c>, which
/// <c>scripts/smoke-input.ps1</c> sets on an idle desktop or at the 03:00 run. Any other run skips it, so no command
/// takes the operator's mouse or keyboard by accident; such a test also carries <c>[Trait("Input", "Real")]</c>, which
/// the runner selects by.
/// </summary>
public static class SmokeInput
{
    public const string Variable = "BOSUN_SMOKE_INPUT";
    public const string Trait = "Input", Real = "Real";
    public const string SkipReason = "real input: run pwsh scripts/smoke-input.ps1";

    /// <summary>Whether this run is the real-input one.</summary>
    public static bool OptedIn => Environment.GetEnvironmentVariable(Variable) == "1";

    /// <summary>Whether the running test is one that may send real input (its trait).</summary>
    public static bool ThisTestMay =>
        TestContext.Current.Test?.Traits is { } traits && traits.TryGetValue(Trait, out var values) && values.Contains(Real);

    /// <summary>
    /// Refuses real input outside the real-input run, or in a test not marked for it: every way the smoke takes the
    /// desktop (<see cref="RealInput"/>, <see cref="PanelProcess.Front"/>) asks here first.
    /// </summary>
    public static void Require(string what)
    {
        if (!ThisTestMay)
            throw new RealInputRefused($"{what} is real input, which only a test with [Trait(\"{Trait}\", \"{Real}\")] and [RealInputFact] may send");
        if (!OptedIn)
            throw new RealInputRefused($"{what} is real input, sent only in the real-input run ({Variable}=1, pwsh scripts/smoke-input.ps1)");
    }
}

/// <summary>A test that sends real input or takes the foreground: skipped unless the run opted in.</summary>
public sealed class RealInputFactAttribute : FactAttribute
{
    public RealInputFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = SmokeInput.SkipReason;
        SkipType = typeof(SmokeInput);
        SkipUnless = nameof(SmokeInput.OptedIn);
    }
}

/// <summary>A theory that sends real input or takes the foreground: skipped unless the run opted in.</summary>
public sealed class RealInputTheoryAttribute : TheoryAttribute
{
    public RealInputTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = SmokeInput.SkipReason;
        SkipType = typeof(SmokeInput);
        SkipUnless = nameof(SmokeInput.OptedIn);
    }
}

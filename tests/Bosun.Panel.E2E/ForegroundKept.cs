using System.Reflection;
using Xunit.v3;

namespace Bosun.Panel.E2E;

/// <summary>
/// Holds the default smoke off the operator's keyboard (D02 T02 §12): outside the real-input run, a test after which a
/// process the run started owns the foreground took it (a window activated by a second launch as much as one brought
/// forward), and what the operator types would land there, so the test fails. A real-input test is let be: taking the
/// foreground is what it is for.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ForegroundKeptAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (SmokeInput.OptedIn) return;
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;
        var owner = Native.ProcessOf(foreground);
        bool ours;
        lock (PanelProcess.Started) ours = PanelProcess.Started.Contains(owner);
        if (ours)
            throw new RealInputRefused($"{methodUnderTest.Name} left a panel it started (process {owner}) in the foreground, which takes the operator's keyboard: "
                + $"mark it [RealInputFact] with [Trait(\"{SmokeInput.Trait}\", \"{SmokeInput.Real}\")]");
    }
}

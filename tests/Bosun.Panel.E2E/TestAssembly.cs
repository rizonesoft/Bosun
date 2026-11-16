using Xunit;

// One test at a time. Every test here owns the desktop while it runs: it asks for the foreground, moves the pointer
// and presses keys, and two at once would take each other's window or pointer.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Bosun.Panel.E2E;

public class TestAssemblyTests
{
    [Fact]
    public void The_smoke_runs_one_test_at_a_time()
    {
        var parallel = Assert.Single(typeof(TestAssemblyTests).Assembly.GetCustomAttributes(typeof(Xunit.v3.ParallelizationAttribute), false));
        Assert.Equal(Xunit.Sdk.ParallelMode.None, ((Xunit.v3.ParallelizationAttribute)parallel).GetMode());
    }
}

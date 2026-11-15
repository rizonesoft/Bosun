using Xunit;

// The panel's tests run one class at a time. WPF loads a compiled view or resource dictionary through one shared
// package for the assembly (Application.LoadComponent, and every view's InitializeComponent), and that package is
// not safe to read from two threads at once: on 2026-11-15 two classes loading Theme/Controls.xaml in the same
// moment, each on its own STA thread, failed one of them with a NullReferenceException inside
// System.IO.Packaging.PackagePart.GetStream. The application itself loads everything on its one UI thread, so the
// race is only the tests'; the classes are small, and running them in turn costs seconds.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Bosun.Panel.Tests;

/// <summary>The rule above, held: parallel classes are the one way these tests have failed for no fault of the code.</summary>
public class TestAssemblyTests
{
    [Fact]
    public void The_panel_s_test_classes_run_one_at_a_time()
    {
        var parallel = Assert.Single(typeof(TestAssemblyTests).Assembly.GetCustomAttributes(typeof(Xunit.v3.ParallelizationAttribute), false));
        Assert.Equal(Xunit.Sdk.ParallelMode.None, ((Xunit.v3.ParallelizationAttribute)parallel).GetMode());
    }
}

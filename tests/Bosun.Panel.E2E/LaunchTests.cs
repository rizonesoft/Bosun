using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>A second launch with the first panel's window minimised: it hands over and exits, and the first comes back.</summary>
public class LaunchTests
{
    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public async Task A_second_launch_restores_the_first_window_and_exits()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Minimized);
        Ui.Until(() => Native.IsIconic(handle), Ui.Short, "the first panel's window to be minimised");

        using (var second = panel.StartAgain())
        {
            var ended = await Task.WhenAny(second.Exited, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.True(ended == second.Exited, "the second launch exits within 15 s");
            Assert.Equal(0, second.ExitCode());
        }

        Ui.Until(() => !Native.IsIconic(handle) && Native.IsWindowVisible(handle), Ui.Short, "the first panel's window to be restored");
        Ui.Until(() => Native.GetForegroundWindow() == handle, Ui.Short, "the first panel's window to be in front");
        Assert.True(panel.Running);
        Assert.Equal([panel.Id], PanelsFrom(PanelProcess.PanelExe));
    }

    /// <summary>The running processes started from a given program file.</summary>
    private static List<int> PanelsFrom(string program)
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program)))
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, program, StringComparison.OrdinalIgnoreCase)) found.Add(process.Id);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // A process that ended while it was read, or one of another user: not this panel.
                }
            }
        return found;
    }
}

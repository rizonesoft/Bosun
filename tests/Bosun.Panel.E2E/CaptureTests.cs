using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Bosun.Core;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// scripts/capture-window.ps1 on the real window (handed over by D02 T02 §9, which can only self-test it without a
/// desktop): a capture of the panel in front, and its refusals when the panel is covered or in the background.
/// </summary>
public class CaptureTests
{
    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public async Task The_panel_in_front_is_captured_at_its_size_and_the_pair_verifies()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var png = Path.Combine(panel.DataFolder, "shot", "panel.png");

        var run = await Capture("-ProcessId", panel.Id.ToString(), "-Out", png, "-Describes", "the end-to-end smoke's panel", "-WaitSeconds", "1");
        Assert.True(run.ExitCode == 0, $"the capture exits 0: {run.StandardOutput}{run.StandardError}");
        var frame = Frame(handle);
        using (var image = Image.FromFile(png)) Assert.Equal((frame.Right - frame.Left, frame.Bottom - frame.Top), (image.Width, image.Height));

        var verify = await Capture("-Verify", png);
        Assert.True(verify.ExitCode == 0, $"-Verify accepts the pair: {verify.StandardOutput}{verify.StandardError}");
    }

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public async Task A_covered_panel_is_refused_with_exit_3_and_nothing_written()
    {
        using var panel = PanelProcess.Start();
        var box = Ui.Box(panel.Front());
        // Always on top over the panel, without the foreground, which the panel keeps.
        using var cover = Cover.Open(new Rect(box.X + box.Width / 3, box.Y + box.Height / 3, box.Width / 3, box.Height / 3), activate: false);
        var window = panel.Front();
        Assert.Equal(new IntPtr(window.Current.NativeWindowHandle), Native.GetForegroundWindow());
        await Refused(panel);
    }

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public async Task A_panel_in_the_background_is_refused_with_exit_3_and_nothing_written()
    {
        using var panel = PanelProcess.Start();
        var box = Ui.Box(panel.Front());
        // Beside the panel, not over it, holding the foreground.
        using var cover = Cover.Open(new Rect(box.Right + 40, box.Top, 200, 200));
        await Refused(panel);
    }

    private static async Task Refused(PanelProcess panel)
    {
        var folder = Path.Combine(panel.DataFolder, "refused");
        var png = Path.Combine(folder, "panel.png");
        var run = await Capture("-ProcessId", panel.Id.ToString(), "-Out", png, "-Describes", "a capture that must be refused", "-WaitSeconds", "1");
        Assert.True(run.ExitCode == 3, $"the capture is refused with exit 3, not {run.ExitCode}: {run.StandardOutput}{run.StandardError}");
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(), "a refused capture writes nothing");
    }

    private static Task<ProcessResult> Capture(params string[] arguments) =>
        new ProcessRunner().RunAsync(new ProcessRequest("pwsh", ["-NoProfile", "-File", Path.Combine(KeyboardTests.Repo(), "scripts", "capture-window.ps1"), .. arguments])
        {
            Timeout = TimeSpan.FromSeconds(90),
        }, TestContext.Current.CancellationToken);

    /// <summary>The frame DWM draws for a window (DWMWA_EXTENDED_FRAME_BOUNDS), which the script captures.</summary>
    private static Native.RECT Frame(IntPtr window)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, 9, out var frame, Marshal.SizeOf<Native.RECT>()));
        return frame;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Native.RECT value, int size);
}

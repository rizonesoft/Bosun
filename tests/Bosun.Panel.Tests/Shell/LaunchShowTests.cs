using Bosun.Panel.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The panel's first window takes the foreground only when the program that started it did not ask otherwise
/// (D02 T02 §12): the show command in STARTUPINFO, read only when STARTF_USESHOWWINDOW says it was given.
/// </summary>
public class LaunchShowTests
{
    [Theory]
    [InlineData(0x0, (short)4, true)]     // no show command given: an ordinary start activates
    [InlineData(0x100, (short)4, true)]   // a show command present but not flagged is not one
    [InlineData(0x1, (short)1, true)]     // SW_SHOWNORMAL
    [InlineData(0x1, (short)3, true)]     // SW_SHOWMAXIMIZED
    [InlineData(0x1, (short)4, false)]    // SW_SHOWNOACTIVATE, as the smoke starts it
    [InlineData(0x101, (short)4, false)]  // with the standard handles beside it
    [InlineData(0x1, (short)8, false)]    // SW_SHOWNA
    [InlineData(0x1, (short)7, false)]    // SW_SHOWMINNOACTIVE, a shortcut set to start minimised
    public void The_first_window_activates_unless_its_start_asked_it_not_to(int flags, short show, bool activates) =>
        Assert.Equal(activates, LaunchShow.Activates(flags, show));
}

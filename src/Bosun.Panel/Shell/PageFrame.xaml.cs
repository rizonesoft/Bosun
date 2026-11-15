using System.Windows.Controls;

namespace Bosun.Panel.Shell;

/// <summary>The frame a page is drawn in: its header and one of its four states (D02 T02 §4).</summary>
public partial class PageFrame : UserControl
{
    /// <summary>The least the failed state's message is given, however little room there is: three lines or so.</summary>
    public const double MinMessageHeight = 48;

    public PageFrame()
    {
        InitializeComponent();
        StateArea.SizeChanged += (_, _) => HoldTheMessage();
        Retry.SizeChanged += (_, _) => HoldTheMessage();
        FailedHeading.SizeChanged += (_, _) => HoldTheMessage();
    }

    /// <summary>
    /// Holds the failed state's message to the height left once the heading and Retry have theirs: a long message
    /// scrolls there, and Retry is never pushed out of the page by what the tool said.
    /// </summary>
    private void HoldTheMessage()
    {
        var others = FailedHeading.ActualHeight + FailureScroll.Margin.Top + FailureScroll.Margin.Bottom
                     + Retry.ActualHeight + Retry.Margin.Top + Retry.Margin.Bottom;
        // Rounded down: text heights are fractions of a pixel, and a fraction over would put Retry's last pixel
        // outside the frame.
        FailureScroll.MaxHeight = Math.Max(MinMessageHeight, Math.Floor(StateArea.ActualHeight - others));
    }
}

using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace Bosun.Panel.Shell;

/// <summary>
/// A toast's message area (D02 T04 §7): a scroll area that, once the toast is opened in full, is a keyboard stop a
/// screen reader can find and read. WPF leaves a <see cref="ScrollViewer"/> inside a control template out of the
/// automation tree, and the text block in it too, so the opened message was focusable and yet nowhere a screen
/// reader looks: the focus was reported on the toast's list item instead. This one is in the tree while it is a tab
/// stop, under its own name ("The whole message") with the message as its help text.
/// </summary>
public sealed class ToastMessage : ScrollViewer
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>
    /// Home and End go to the message's first and last lines. A <see cref="ScrollViewer"/> takes plain Home and End to
    /// its left and right edges (only Ctrl with them goes to the top and the bottom), and a message never scrolls
    /// sideways, so End did nothing a reader could see (found by its test, D02 T04 §7).
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Home or Key.End)
        {
            if (e.Key == Key.Home) ScrollToTop(); else ScrollToBottom();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private sealed class Peer(ToastMessage owner) : ScrollViewerAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => ((ToastMessage)Owner).IsTabStop;

        protected override bool IsContentElementCore() => ((ToastMessage)Owner).IsTabStop;

        protected override string GetClassNameCore() => nameof(ToastMessage);
    }
}

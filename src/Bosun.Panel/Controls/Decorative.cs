using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Bosun.Panel.Controls;

/// <summary>
/// Holds something seen but not said (design/SPEC.md §19: a glyph beside a word is decorative): a rail row's glyph,
/// and its count badge, whose count the row's own name already says. Its automation peer is in neither the control
/// nor the content view and has no children, so a screen reader finds the row once, by its name, and never the glyph
/// or the badge again on their own; it is never a keyboard stop.
/// </summary>
public class Decorative : Decorator
{
    public Decorative() => Focusable = false;

    protected override AutomationPeer OnCreateAutomationPeer() => new Unspoken(this);

    private sealed class Unspoken(Decorative owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

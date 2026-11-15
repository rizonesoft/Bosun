using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Bosun.Panel.Accessibility;

/// <summary>
/// A text a screen reader announces when it appears or changes (D02 T02 §2, after Steward's LiveRegion). WPF reads
/// <c>AutomationProperties.LiveSetting</c> only when the element raises
/// <see cref="AutomationEvents.LiveRegionChanged"/>, which it never does by itself, so a <see cref="TextBlock"/> with
/// <c>LiveRegion.Announce="True"</c> raises it whenever its text becomes non-empty, and is given a polite live
/// setting when it has none.
///
/// An announcement is made after layout, and checked again when it is made: a line cleared or unloaded since says
/// nothing, and several changes before it runs say the latest text once.
/// </summary>
public static class LiveRegion
{
    public static readonly DependencyProperty AnnounceProperty = DependencyProperty.RegisterAttached(
        "Announce", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false, OnAnnounceChanged));

    public static bool GetAnnounce(DependencyObject element) => (bool)element.GetValue(AnnounceProperty);

    public static void SetAnnounce(DependencyObject element, bool value) => element.SetValue(AnnounceProperty, value);

    // Whether an announcement of this line is already queued: one pending per line.
    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false));

    private static readonly DependencyPropertyDescriptor Text = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    [ThreadStatic]
    private static Action<AutomationPeer>? _raising;

    /// <summary>
    /// Test seam, per thread: called with the line's automation peer in place of raising the event, so a test sees
    /// each raise and what the peer would say. Null raises the event.
    /// </summary>
    internal static Action<AutomationPeer>? Raising { get => _raising; set => _raising = value; }

    private static void OnAnnounceChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock text) return;
        // Taken off first, so turning it on twice never watches twice.
        text.Loaded -= OnLoaded;
        text.Unloaded -= OnUnloaded;
        Text.RemoveValueChanged(text, Changed);
        if (e.NewValue is not true) return;

        // A live region that is "off" is not one: polite, unless the line asked for something else.
        if (AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Off) AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);

        // Watched only while loaded: the descriptor holds the element strongly until removed.
        text.Loaded += OnLoaded;
        text.Unloaded += OnUnloaded;
        if (text.IsLoaded) Text.AddValueChanged(text, Changed);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var text = (TextBlock)sender;
        Text.RemoveValueChanged(text, Changed);
        Text.AddValueChanged(text, Changed);
        // A line that loads already showing text (a page rebuilt over the same error) is announced then, since no
        // change of its text will follow.
        Changed(text, EventArgs.Empty);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Text.RemoveValueChanged((TextBlock)sender, Changed);

    private static void Changed(object? sender, EventArgs e)
    {
        if (sender is not TextBlock { Text.Length: > 0 } text || (bool)text.GetValue(PendingProperty)) return;
        text.SetValue(PendingProperty, true);
        // After layout has made it visible, so the announcement reads the line now shown.
        text.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Announce(text));
    }

    private static void Announce(TextBlock text)
    {
        text.SetValue(PendingProperty, false);
        // Checked again now: the line may have been cleared, unloaded or switched off since it was queued.
        if (!text.IsLoaded || text.Text.Length == 0 || !GetAnnounce(text)) return;
        if (UIElementAutomationPeer.CreatePeerForElement(text) is not { } peer) return;
        if (_raising is { } seam) seam(peer);
        else peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}

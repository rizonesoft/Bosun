using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Controls;

/// <summary>The states a status pill says (design/SPEC.md §19), each with its tint and its glyph from the icon map.</summary>
public enum StatusKind
{
    Neutral,
    Ok,
    Warn,
    Danger,
    Info,
}

/// <summary>
/// A state at a glance (D02 T03 §4, design/SPEC.md §19 Status pill): a glyph, a word and a soft tint of the state's
/// colour. The word is required, because status is never colour alone: a view that writes a pill without one fails the
/// XAML rules test, and a word that is empty or blank at run time reads "Unknown". A screen reader hears the word as the
/// pill's name; the glyph beside it is decorative. The pill is seen, never focused: the control it describes is.
/// </summary>
public class StatusPill : Control
{
    /// <summary>What a pill says when it was given no word.</summary>
    public const string NoWord = "Unknown";

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusKind), typeof(StatusPill), new FrameworkPropertyMetadata(StatusKind.Neutral));

    public static readonly DependencyProperty WordProperty = DependencyProperty.Register(
        nameof(Word), typeof(string), typeof(StatusPill), new FrameworkPropertyMetadata("", OnWordChanged));

    private static readonly DependencyPropertyKey ShownWordKey = DependencyProperty.RegisterReadOnly(
        nameof(ShownWord), typeof(string), typeof(StatusPill), new FrameworkPropertyMetadata(NoWord));

    public static readonly DependencyProperty ShownWordProperty = ShownWordKey.DependencyProperty;

    static StatusPill() => FocusableProperty.OverrideMetadata(typeof(StatusPill), new FrameworkPropertyMetadata(false));

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The state in a word ("Running", "Stale", "Not available"). Required.</summary>
    public string Word
    {
        get => (string)GetValue(WordProperty);
        set => SetValue(WordProperty, value);
    }

    /// <summary>The word as drawn and said: <see cref="Word"/>, or <see cref="NoWord"/> when it is empty or blank.</summary>
    public string ShownWord => (string)GetValue(ShownWordProperty);

    private static void OnWordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (StatusPill)d;
        var shown = string.IsNullOrWhiteSpace(e.NewValue as string) ? NoWord : ((string)e.NewValue).Trim();
        var was = pill.ShownWord;
        pill.SetValue(ShownWordKey, shown);
        if (was != shown && UIElementAutomationPeer.FromElement(pill) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, was, shown);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Said(this);

    /// <summary>A pill to a screen reader: text, named by its word, with nothing inside said again.</summary>
    private sealed class Said(StatusPill owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.ShownWord;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(StatusPill);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>A status kind's glyph, through the one icon map (Theme/Glyphs.cs).</summary>
public sealed class StatusGlyph : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StatusKind.Ok => Glyphs.For(GlyphAction.StatusOk),
        StatusKind.Warn => Glyphs.For(GlyphAction.StatusWarn),
        StatusKind.Danger => Glyphs.For(GlyphAction.StatusFail),
        StatusKind.Info => Glyphs.For(GlyphAction.StatusInfo),
        _ => Glyphs.For(GlyphAction.StatusNeutral),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

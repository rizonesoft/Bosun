using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Bosun.Panel.Tests.Render;

/// <summary>
/// Draws an element's own focus visual the way WPF's keyboard focus does (D02 T04 §1): a <see cref="Control"/> styled
/// with the element's <see cref="FrameworkElement.FocusVisualStyle"/>, in the adorner layer, laid at the element's
/// bounds. Off screen nothing can hold keyboard focus (the hidden host never activates), so the renders and
/// FocusOutlineTests draw the visual through this; the E2E KeyboardTests prove a real Tab draws it on screen.
/// An element with no focus visual draws nothing, so a style that loses its ring fails the tests that look for one.
/// </summary>
public static class FocusShown
{
    /// <summary>Adds the element's focus visual over it; false when it has none.</summary>
    public static bool Show(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.FocusVisualStyle is not { } style) return false;
        var layer = AdornerLayer.GetAdornerLayer(element)
            ?? throw new InvalidOperationException($"{element.GetType().Name} has no adorner layer above it: host it in an AdornerDecorator");
        layer.Add(new FocusVisualAdorner(element, style));
        return true;
    }

    private sealed class FocusVisualAdorner : Adorner
    {
        private readonly Control _visual;

        public FocusVisualAdorner(FrameworkElement adorned, Style style) : base(adorned)
        {
            _visual = new Control { Style = style, Focusable = false, IsHitTestVisible = false };
            IsHitTestVisible = false;
            AddVisualChild(_visual);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _visual;

        protected override Size MeasureOverride(Size constraint)
        {
            _visual.Measure(AdornedElement.RenderSize);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _visual.Arrange(new Rect(AdornedElement.RenderSize));
            return finalSize;
        }
    }
}

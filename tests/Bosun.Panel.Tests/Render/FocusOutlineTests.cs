using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Render;

/// <summary>
/// One focus outline per control (D02 T04 §1, design/SPEC.md §1): every keyboard target, drawn focused and unfocused
/// in each theme, differs by exactly one ring of its focus style's geometry: all four sides, 2 px thick, at its
/// clearance (2 px outside for FocusRing, at the bounds for FocusRing.Inside, 2 px inside for FocusRing.Segment), and
/// nothing else. A border that also turns accent, a missing focus style or a missing side fails.
/// </summary>
public class FocusOutlineTests
{
    /// <summary>Where a focus style's ring lies, from the control's bounds inward (negative: outside it).</summary>
    static readonly Dictionary<string, int> Offset = new() { ["FocusRing"] = -4, ["FocusRing.Inside"] = 0, ["FocusRing.Segment"] = 2 };

    public sealed record Case(string Name, Func<FrameworkElement> Make, Func<FrameworkElement, FrameworkElement> Target, bool Selected = false)
    {
        public override string ToString() => Name;
    }

    static FrameworkElement Styled(FrameworkElement element, string? key)
    {
        if (key is not null) element.SetResourceReference(FrameworkElement.StyleProperty, key);
        return element;
    }

    static T First<T>(FrameworkElement root, Func<T, bool>? where = null) where T : DependencyObject =>
        ShellSupport.Under<T>(root).First(t => where?.Invoke(t) ?? true);

    static DataGrid Grid()
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Width = 260, ItemsSource = new[] { new { Repo = "Recto", Gb = "6.2" }, new { Repo = "Bosun", Gb = "0.9" } } };
        grid.Columns.Add(new DataGridTextColumn { Header = "Repo", Binding = new System.Windows.Data.Binding("Repo"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "GB", Binding = new System.Windows.Data.Binding("Gb"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        return grid;
    }

    /// <summary>The grid with its first cell, or first row, chosen once it is laid out.</summary>
    static DataGrid Chosen(DataGrid grid, DataGridSelectionUnit unit)
    {
        grid.SelectionUnit = unit;
        grid.Loaded += (_, _) =>
        {
            if (unit == DataGridSelectionUnit.Cell) grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[0], grid.Columns[0]));
            else grid.SelectedIndex = 0;
        };
        return grid;
    }

    internal static ListBox Listed(string style, bool selectFirst = false)
    {
        var list = new ListBox { ItemsSource = new[] { "Overview", "Memory", "Instances" }, Width = 200 };
        list.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (selectFirst) list.SelectedIndex = 0;
        return list;
    }

    /// <summary>Every keyboard target, for this test and the focus renders (RenderCatalog).</summary>
    public static readonly IReadOnlyList<Case> All =
    [
        new Case("field", () => Styled(new TextBox { Text = "2048", Width = 140 }, "Field"), r => r),
        // The palette's search (D02 T04 §7): no border of its own, its glyph inside it, one ring round both.
        new Case("palette search", () => Styled(new TextBox { Text = "set", Width = 320 }, "PaletteSearch"), r => r),
        // An invalid field (D02 T04 §2): its danger border is its own, and focus is still the one ring outside it.
        new Case("field, invalid", () =>
        {
            var field = Styled(new TextBox { Text = "lots", Width = 140 }, "Field");
            Bosun.Panel.Theme.FieldState.SetInvalid(field, true);
            return field;
        }, r => r),
        new Case("button", () => Styled(new Button { Content = "Refresh" }, "Btn"), r => r),
        new Case("small button", () => Styled(new Button { Content = "Retry" }, "Btn.Small"), r => r),
        new Case("primary button", () => Styled(new Button { Content = "Save" }, "Btn.Primary"), r => r),
        new Case("danger button", () => Styled(new Button { Content = "Discard" }, "Btn.Danger"), r => r),
        new Case("window button", () => Styled(new Button { Content = "x", Width = 46, Height = 32 }, "Btn.Chrome"), r => r),
        new Case("check box, off", () => new CheckBox { Content = "Notify me" }, r => r),
        new Case("check box, on", () => new CheckBox { Content = "Notify me", IsChecked = true }, r => r),
        new Case("combo box", () => new ComboBox { ItemsSource = new[] { "Windows", "WSL" }, SelectedIndex = 0, Width = 160 }, r => r),
        new Case("combo item", () => new StackPanel { Width = 160, Children = { new ComboBoxItem { Content = "Windows" } } }, r => First<ComboBoxItem>(r)),
        new Case("segment, chosen", () => new Bosun.Panel.Controls.SegmentedControl { ItemsSource = new[] { "Follow Windows", "Dark", "Light" }, SelectedIndex = 1 },
            r => (FrameworkElement)((Bosun.Panel.Controls.SegmentedControl)r).ItemContainerGenerator.ContainerFromIndex(1), Selected: true),
        new Case("segment, not chosen", () => new Bosun.Panel.Controls.SegmentedControl { ItemsSource = new[] { "Follow Windows", "Dark", "Light" }, SelectedIndex = 1 },
            r => (FrameworkElement)((Bosun.Panel.Controls.SegmentedControl)r).ItemContainerGenerator.ContainerFromIndex(2)),
        new Case("rail row, chosen", () => Listed("NavList", selectFirst: true), r => First<ListBoxItem>(r), Selected: true),
        new Case("rail row, not chosen", () => Listed("NavList", selectFirst: true), r => ShellSupport.Under<ListBoxItem>(r).Skip(1).First()),
        new Case("column header", Grid, r => First<DataGridColumnHeader>(r, h => h.Column is not null)),
        new Case("cell", Grid, r => First<DataGridCell>(r)),
        new Case("cell, chosen", () => Chosen(Grid(), DataGridSelectionUnit.Cell), r => First<DataGridCell>(r), Selected: true),
        new Case("table row", Grid, r => First<DataGridRow>(r)),
        new Case("table row, chosen", () => Chosen(Grid(), DataGridSelectionUnit.FullRow), r => First<DataGridRow>(r), Selected: true),
        new Case("tab", () => Styled(new TabControl { Width = 360, Height = 80, Items = { new TabItem { Header = "Overview" }, new TabItem { Header = "Logs" } } }, "TabStrip"),
            r => ShellSupport.Under<TabItem>(r).Skip(1).First()),
        new Case("log", () => Listed("LogBox"), r => r),
    ];

    public static TheoryData<Case> Cases => new(All);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_keyboard_target_shows_one_ring_of_its_focus_style(Case c)
    {
        foreach (var theme in PageRenderer.Themes)
        {
            PageRenderer.InPage(theme, () => new AdornerDecorator
            {
                Child = new Grid { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Children = { c.Make() } },
            }, page =>
            {
                var made = ((Grid)((AdornerDecorator)page.Child).Child).Children[0] as FrameworkElement;
                var target = c.Target(made!);
                var style = Assert.IsType<Style>(target.FocusVisualStyle, exactMatch: false);
                var key = Offset.Keys.SingleOrDefault(k => ReferenceEquals(page.TryFindResource(k), style))
                    ?? throw new Xunit.Sdk.XunitException($"{c.Name}: its focus visual is not one of {string.Join(", ", Offset.Keys)}");
                var box = target.TransformToAncestor(page).TransformBounds(new Rect(target.RenderSize));
                var before = PageRenderer.Pixels(page);
                Assert.True(FocusShown.Show(target));
                PageRenderer.Settle(page);
                var after = PageRenderer.Pixels(page);
                // A chosen row or segment in a contrast theme is filled with the highlight, the ring's own colour: there the
                // ring shows by its inner line, 2 px in, so the band is the ring and that line (FocusRing.Inside).
                var contrastChosen = theme == "HighContrast" && c.Selected && key != "FocusRing";
                // Where the selection is filled with the highlight the ring blends in and shows by its inner line alone;
                // where it is not, the ring shows as anywhere else.
                var ring = Ring.Measure(before, after, (int)page.Width, box, Offset[key]);
                if (contrastChosen && ring.Problem is not null)
                {
                    // The ring itself may still show where the fill stops (a row's gridline): outside the line is not checked.
                    var inner = Ring.Measure(before, after, (int)page.Width, box, Offset[key] + 2, thickness: 1, sole: false, clearOutside: false);
                    if (inner.Problem is null) ring = inner;
                }
                Assert.True(ring.Problem is null, $"{c.Name} in {theme} ({key}): {ring.Problem}");
                return 0;
            });
        }
    }

    [Fact]
    public void An_item_in_an_open_dropdown_shows_one_ring_in_its_popup()
    {
        foreach (var theme in PageRenderer.Themes)
        {
            PageRenderer.InPage(theme, () => new AdornerDecorator
            {
                Child = new Grid { Margin = new Thickness(24), Children = { new ComboBox { ItemsSource = new[] { "Windows", "WSL", "Both" }, SelectedIndex = 0, Width = 160 } } },
            }, page =>
            {
                var combo = ShellSupport.Under<ComboBox>(page).Single();
                combo.IsDropDownOpen = true;
                PageRenderer.Settle(page);
                var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.NotNull(item);
                // The popup is a window of its own: its content is drawn and measured on its own.
                var root = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(PopupChild(item));
                var host = new Border { Width = Math.Ceiling(root.ActualWidth) + 16, Height = Math.Ceiling(root.ActualHeight) + 16 };
                var box = item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));
                var before = Draw(root, (int)host.Width, (int)host.Height);
                Assert.True(FocusShown.Show(item));
                root.UpdateLayout();
                PageRenderer.Settle(page);
                var after = Draw(root, (int)host.Width, (int)host.Height);
                var ring = Ring.Measure(before, after, (int)host.Width, box, Offset["FocusRing.Inside"]);
                Assert.True(ring.Problem is null, $"an open dropdown's item in {theme}: {ring.Problem}");
                combo.IsDropDownOpen = false;
                return 0;
            });
        }
    }

    /// <summary>The top of the popup's own tree that holds <paramref name="item"/>.</summary>
    static FrameworkElement PopupChild(FrameworkElement item)
    {
        DependencyObject node = item;
        while (System.Windows.Media.VisualTreeHelper.GetParent(node) is { } parent && System.Windows.Media.VisualTreeHelper.GetParent(parent) is not null) node = parent;
        return (FrameworkElement)node;
    }

    static uint[] Draw(FrameworkElement root, int width, int height)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new uint[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    [Fact]
    public void In_a_contrast_theme_the_ring_on_a_chosen_row_keeps_its_inner_line()
    {
        // FocusRing.Inside's 1 px line in the window's colour: where the selection fills with the highlight, the ring in
        // the same colour would vanish without it (D02 T03 §4).
        PageRenderer.InPage("HighContrast", () => new AdornerDecorator { Child = new Grid { Margin = new Thickness(24), Children = { Listed("NavList", selectFirst: true) } } }, page =>
        {
            var row = ShellSupport.Under<ListBoxItem>(page).First();
            var box = row.TransformToAncestor(page).TransformBounds(new Rect(row.RenderSize));
            var before = PageRenderer.Pixels(page);
            FocusShown.Show(row);
            PageRenderer.Settle(page);
            var after = PageRenderer.Pixels(page);
            // The inner line lies 2 px in, just inside the ring: it differs from the fill on every side.
            var inner = Ring.Measure(before, after, (int)page.Width, box, 2, thickness: 1, sole: false);
            Assert.True(inner.Problem is null, $"the inner line: {inner.Problem}");
            return 0;
        });
    }

    /// <summary>The pixels a focus visual changed, read as a ring around a box.</summary>
    internal static class Ring
    {
        static bool Changed(uint a, uint b) =>
            Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF)) + Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)) + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) > 60;

        /// <summary>
        /// Checks the changed pixels form a ring <paramref name="thickness"/> px thick starting <paramref name="offset"/> px
        /// inside <paramref name="box"/> (negative: outside): each side covered along its straight middle, the pixel
        /// just outside it unchanged, and, when <paramref name="sole"/>, nothing changed away from the ring.
        /// </summary>
        public static (string? Problem, int Changed) Measure(uint[] before, uint[] after, int width, Rect box, int offset, int thickness = 2, bool sole = true, bool clearOutside = true)
        {
            int height = before.Length / width;
            bool At(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && Changed(before[y * width + x], after[y * width + x]);
            var left = (int)Math.Round(box.Left) + offset;
            var top = (int)Math.Round(box.Top) + offset;
            var right = (int)Math.Round(box.Right) - offset - 1;
            var bottom = (int)Math.Round(box.Bottom) - offset - 1;
            int total = 0;
            for (var i = 0; i < before.Length; i++) if (Changed(before[i], after[i])) total++;
            if (total == 0) return ("nothing was drawn: no focus ring", 0);

            // Each side, along its middle half (the corners are rounded): covered across its band, clear either side of it.
            string? Side(string name, Func<int, int, bool> inBand, Func<int, bool> outsideBand, int from, int to)
            {
                int covered = 0, spilled = 0, n = 0;
                var span = to - from;
                for (var p = from + span / 4; p <= to - span / 4; p++, n++)
                {
                    if (Enumerable.Range(0, thickness).All(d => inBand(p, d))) covered++;
                    // Clear on both sides of the band: a ring thicker than specified spills one way or the other.
                    if ((clearOutside && outsideBand(p)) || inBand(p, thickness)) spilled++;
                }
                if (n == 0) return $"the {name} side is too short to measure";
                if (covered < n * 0.9) return $"the {name} side is missing ({covered} of {n} drawn)";
                if (spilled > n * 0.1) return $"the {name} side is thicker than {thickness} px or out of place ({spilled} of {n} beyond it)";
                return null;
            }
            var problem =
                Side("top", (x, d) => At(x, top + d), x => At(x, top - 1), left, right)
                ?? Side("bottom", (x, d) => At(x, bottom - d), x => At(x, bottom + 1), left, right)
                ?? Side("left", (y, d) => At(left + d, y), y => At(left - 1, y), top, bottom)
                ?? Side("right", (y, d) => At(right - d, y), y => At(right + 1, y), top, bottom);
            if (problem is not null)
            {
                // Where the change actually was, so a failure says what was drawn instead.
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        if (At(x, y)) (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                return ($"{problem}; the ring was looked for at {left},{top}..{right},{bottom} and {total} pixels changed within {minX},{minY}..{maxX},{maxY}", total);
            }
            if (!sole) return (null, total);

            // Nothing else: changed pixels away from the ring's band (with a pixel either side for its anti-aliasing).
            int away = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    if (!Changed(before[y * width + x], after[y * width + x])) continue;
                    // The rounded corners curve inward: their anti-aliased pixels are the ring's own.
                    var nearX = Math.Min(Math.Abs(x - left), Math.Abs(x - right)) <= 8;
                    var nearY = Math.Min(Math.Abs(y - top), Math.Abs(y - bottom)) <= 8;
                    if (nearX && nearY) continue;
                    var outer = x >= left - 1 && x <= right + 1 && y >= top - 1 && y <= bottom + 1;
                    var inner = x >= left + thickness + 1 && x <= right - thickness - 1 && y >= top + thickness + 1 && y <= bottom - thickness - 1;
                    if (!outer || inner) away++;
                }
            return away > total / 50 + 4 ? ($"{away} pixels changed away from the ring: a second outline or a fill", total) : (null, total);
        }
    }
}

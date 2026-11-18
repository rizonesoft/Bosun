using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Bosun.Panel.Tests.Render;

/// <summary>One row of the gallery's table: a name and two numbers.</summary>
public sealed record GalleryRow(string Name, string Side, int Megabytes, double Share);

/// <summary>
/// Every control style of Theme/Controls.xaml (D02 T01 §4) on one page, each in its states, for a review to look at:
/// the styles are the application's own, found through the page's resources as a window's content finds them.
/// </summary>
public sealed class ControlsGallery : UserControl
{
    public static readonly IReadOnlyList<GalleryRow> Rows =
    [
        new("devenv.exe", "Windows", 1840, 11.5),
        new("node (vite)", "WSL", 912, 5.7),
        new("clangd", "Windows", 388, 2.4),
        new("dotnet test", "Windows", 2210, 13.8),
        new("postgres", "WSL", 96, 0.6),
    ];

    /// <summary>The buttons, tabs, fields, check boxes, combo boxes and the two lists.</summary>
    public static ControlsGallery Gallery()
    {
        var stack = new StackPanel { Margin = new Thickness(32) };

        stack.Children.Add(Label("Buttons"));
        stack.Children.Add(Row(
            Button("Btn", "Refresh"), Button("Btn.Primary", "Save"), Button("Btn.Danger", "Stop job"),
            Button("Btn.Small", "Details"), Button("Btn", "Disabled", enabled: false), Button("Btn.Primary", "Disabled", enabled: false),
            Chrome()));

        stack.Children.Add(Label("Tabs"));
        var tabs = new TabControl { Height = 96, Tag = "3 waiting" }; // the strip draws it as its count pill (D02 T04 §6)
        tabs.SetResourceReference(StyleProperty, "TabStrip");
        tabs.Items.Add(new TabItem { Header = "Overview", Content = new TextBlock { Text = "The first tab's content" } });
        tabs.Items.Add(new TabItem { Header = "Logs", Content = new TextBlock { Text = "The second tab's content" } });
        tabs.Items.Add(new TabItem { Header = "Dry run", IsEnabled = false, ToolTip = "Needs the service" });
        stack.Children.Add(tabs);

        stack.Children.Add(Label("Fields"));
        stack.Children.Add(Row(
            Field("build Bosun"), Field("read only", readOnly: true), Field("disabled", enabled: false),
            Combo("Choose a side", -1), Combo("Choose a side", 1), Combo("Choose a side", 0, enabled: false)));

        stack.Children.Add(FormSection());
        stack.Children.Add(Label("Check boxes"));
        stack.Children.Add(Row(
            new CheckBox { Content = "Start with Windows", IsChecked = true }, new CheckBox { Content = "Notify on pressure" },
            new CheckBox { Content = "Disabled, on", IsChecked = true, IsEnabled = false }, new CheckBox { Content = "Disabled, off", IsEnabled = false }));

        var lists = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) });
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        lists.ColumnDefinitions.Add(new ColumnDefinition());

        var rail = new ListBox { ItemsSource = new[] { "Overview", "Memory", "Instances", "Queue" }, SelectedIndex = 1 };
        rail.SetResourceReference(StyleProperty, "NavList");
        lists.Children.Add(Column(0, Label("Rail"), rail));

        // A factory belongs to one template, so each log has its own line.
        static FrameworkElementFactory Mono()
        {
            var line = new FrameworkElementFactory(typeof(TextBlock));
            line.SetBinding(TextBlock.TextProperty, new Binding());
            line.SetResourceReference(StyleProperty, "T.Mono");
            return line;
        }
        // A log longer than its space scrolls with the styled bar; one with room to spare shows none (D02 T04 §9).
        var log = new ListBox
        {
            Height = 96,
            ItemTemplate = new DataTemplate { VisualTree = Mono() },
            ItemsSource = Enumerable.Range(1, 12).Select(n => $"12:0{n % 10}:00  INFO  queue: slot {n % 2 + 1} took job {n}").ToArray(),
        };
        log.SetResourceReference(StyleProperty, "LogBox");
        var fits = new ListBox
        {
            Height = 112,
            Margin = new Thickness(0, 8, 0, 0),
            ItemTemplate = new DataTemplate { VisualTree = Mono() },
            Name = "GalleryLog",
            ItemsSource = Enumerable.Range(1, 5).Select(n => $"12:0{n}:00  INFO  queue: slot {n % 2 + 1} took job {n}").ToArray(),
        };
        fits.SetResourceReference(StyleProperty, "LogBox");
        lists.Children.Add(Column(2, Label("Log"), log, Label("Log, with room to spare"), fits));
        stack.Children.Add(lists);

        return new ControlsGallery { Content = stack };
    }

    /// <summary>A table of names and numbers, the second row selected.</summary>
    public static ControlsGallery Table()
    {
        var grid = Grid(Rows);
        grid.SelectedIndex = 1;
        var stack = new StackPanel { Margin = new Thickness(32) };
        stack.Children.Add(Label("Table"));
        stack.Children.Add(grid);
        return new ControlsGallery { Content = stack };
    }

    /// <summary>The gallery's table: two text columns and two number columns, in the styles a page uses.</summary>
    public static DataGrid Grid(IReadOnlyList<GalleryRow> rows)
    {
        var controls = PageRenderer.Controls();
        var grid = new DataGrid { ItemsSource = rows };
        grid.Columns.Add(Column(controls, "Process", nameof(GalleryRow.Name), "Cell.Text"));
        grid.Columns.Add(Column(controls, "Side", nameof(GalleryRow.Side), "Cell.Text"));
        grid.Columns.Add(Column(controls, "Memory (MB)", nameof(GalleryRow.Megabytes), "Cell.Number", "N0"));
        grid.Columns.Add(Column(controls, "Share (%)", nameof(GalleryRow.Share), "Cell.Number", "N1"));
        return grid;
    }

    // A column is not in the tree, so it can't find a style by name as an element does: it is handed the style, as a
    // page's XAML hands it one with {StaticResource Cell.Number}.
    private static DataGridTextColumn Column(ResourceDictionary controls, string header, string path, string style, string? format = null) => new()
    {
        Header = header,
        Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        Binding = new Binding(path) { StringFormat = format },
        ElementStyle = (Style)controls[style],
    };

    /// <summary>A group's heading, as a page's section is headed (D02 T04 §3).</summary>
    private static TextBlock Label(string text) => Text(text, "Section.Heading");

    /// <summary>A form section as Settings draws one: its heading, then a field label the scale's S above its field.</summary>
    private static StackPanel FormSection()
    {
        var field = new TextBox { Text = "2048", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        field.SetResourceReference(StyleProperty, "Field");
        // The scale's XL above and below it, as one section stands apart from the next.
        var after = new Border();
        after.SetResourceReference(HeightProperty, "S.XL");
        var section = new StackPanel { Children = { Text("A section", "Section.Heading"), Text("A field label", "FieldLabel"), field, after } };
        section.SetResourceReference(MarginProperty, "Gap.Above.XL");
        return section;
    }

    private static TextBlock Text(string text, string style)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(StyleProperty, style);
        return block;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        foreach (var child in children)
        {
            if (child is FrameworkElement element)
            {
                // Each at its own height: a row would stretch a 32 px button to its tallest neighbour.
                element.Margin = new Thickness(0, 0, 12, 0);
                element.VerticalAlignment = VerticalAlignment.Center;
            }
            row.Children.Add(child);
        }
        return row;
    }

    private static StackPanel Column(int column, params UIElement[] children)
    {
        var stack = new StackPanel();
        System.Windows.Controls.Grid.SetColumn(stack, column);
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    /// <summary>A window's close button as the shell draws it: the map's caption glyph, named and with its tooltip (D02 T04 §6).</summary>
    private static Button Chrome()
    {
        var glyph = new System.Windows.Shapes.Path { Data = Bosun.Panel.Theme.Glyphs.CaptionClose, StrokeThickness = 1, SnapsToDevicePixels = true };
        glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var button = new Button { Content = glyph, Tag = "danger", ToolTip = "Close", Width = 46, Height = 32 };
        System.Windows.Automation.AutomationProperties.SetName(button, "Close");
        button.SetResourceReference(StyleProperty, "Btn.Chrome");
        return button;
    }

    private static Button Button(string style, string text, bool enabled = true, string? tag = null)
    {
        var button = new Button { Content = text, IsEnabled = enabled, Tag = tag };
        button.SetResourceReference(StyleProperty, style);
        return button;
    }

    private static TextBox Field(string text, bool readOnly = false, bool enabled = true)
    {
        var box = new TextBox { Text = text, Width = 150, IsReadOnly = readOnly, IsEnabled = enabled };
        box.SetResourceReference(StyleProperty, "Field");
        return box;
    }

    private static ComboBox Combo(string placeholder, int selected, bool enabled = true) =>
        new() { Tag = placeholder, Width = 150, ItemsSource = new[] { "Windows", "WSL" }, SelectedIndex = selected, IsEnabled = enabled };
}

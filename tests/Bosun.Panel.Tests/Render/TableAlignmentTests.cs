using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Render;

/// <summary>
/// Tables line up (D02 T04 §4): a table's header and first cell text start where its card's title does; a number
/// column's text, cells and header alike, ends at the column's right inset in tabular figures, sorted either way; and a
/// word in a number column is dim with its reason, or the highlight's text in a selected contrast row.
/// </summary>
public class TableAlignmentTests
{
    static Rect Box(FrameworkElement e, Visual root) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));

    static TextBlock TextOf(FrameworkElement container) => ShellSupport.Under<TextBlock>(container).First(t => t.IsVisible && t.Text.Length > 0);

    static void OnMemory(Action<Border, DataGrid> test, string theme = "Dark") => PageRenderer.InPage<int>(theme, () =>
    {
        var (view, _) = Memory.MemoryPageTests.Rendered(new Memory.MemoryPageTests.Source());
        return view;
    }, page =>
    {
        PageRenderer.Settle(page);
        test(page, ShellSupport.Under<DataGrid>(page).Single(g => g.Name == "ByRepo"));
        return 0;
    }, 1360, 1100);

    [Fact]
    public void A_table_s_header_and_first_cell_start_where_its_card_s_title_does()
    {
        OnMemory((page, grid) =>
        {
            var title = ShellSupport.Under<TextBlock>(page).Single(t => t.Text == "By repo");
            var header = ShellSupport.Under<DataGridColumnHeader>(grid).First(h => h.Column?.Header as string == "Repo");
            var templateCell = ShellSupport.Under<DataGridCell>(grid).First(c => c.Column.Header as string == "Repo");
            var textCell = ShellSupport.Under<DataGridCell>(grid).First(c => c.Column.Header as string == "Side");
            var left = Box(title, page).Left;
            Assert.Equal(left, Box(TextOf(header), page).Left, 1);
            Assert.Equal(left, Box(TextOf(templateCell), page).Left, 1);
            // The second column starts its text where its header's text starts.
            var sideHeader = ShellSupport.Under<DataGridColumnHeader>(grid).First(h => h.Column?.Header as string == "Side");
            Assert.Equal(Box(TextOf(sideHeader), page).Left, Box(TextOf(textCell), page).Left, 1);
        });
    }

    [Fact]
    public void A_number_column_s_cells_and_header_end_at_its_right_inset_in_tabular_figures_sorted_either_way()
    {
        OnMemory((page, grid) =>
        {
            var inset = (double)page.FindResource("S.M");
            foreach (var direction in new[] { ListSortDirection.Ascending, ListSortDirection.Descending, (ListSortDirection?)null })
            {
                var column = grid.Columns.Single(c => c.Header as string == "Private GB");
                column.SortDirection = direction;
                PageRenderer.Settle(page);
                var header = ShellSupport.Under<DataGridColumnHeader>(grid).First(h => ReferenceEquals(h.Column, column));
                var headerText = ShellSupport.Under<TextBlock>(header).First(t => t.Text == "Private GB");
                Assert.Equal(Box(header, page).Right - inset, Box(headerText, page).Right, 1);
                foreach (var cell in ShellSupport.Under<DataGridCell>(grid).Where(c => ReferenceEquals(c.Column, column)))
                {
                    var text = TextOf(cell);
                    Assert.Equal(Box(cell, page).Right - inset, Box(text, page).Right, 1);
                    Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(text));
                }
            }
        });
    }

    [Fact]
    public void The_history_table_and_the_Instances_template_columns_right_align_their_numbers_too()
    {
        // The Memory history as a table.
        Func<FrameworkElement, Task>? ready = null;
        PageRenderer.InPage<int>("Dark", () =>
        {
            var (view, isReady) = Memory.MemoryPageTests.Rendered(new Memory.MemoryPageTests.Source { Report = RenderCatalog.HistoryReport() }, page => page.ShowAsTable.Execute(null));
            ready = isReady;
            return view;
        }, page =>
        {
            // Its ready step shows the history as a table.
            var waiting = ready!(page);
            while (!waiting.IsCompleted) PageRenderer.Settle(page);
            PageRenderer.Settle(page);
            var inset = (double)page.FindResource("S.M");
            var grid = ShellSupport.Under<DataGrid>(page).Single(g => g.Name == "HistoryTable");
            var cell = ShellSupport.Under<DataGridCell>(grid).First(c => c.Column.Header as string == "Lowest available");
            Assert.Equal(Box(cell, page).Right - inset, Box(TextOf(cell), page).Right, 1);
            return 0;
        }, 1360, 1100);
        // The Instances page's GB and Uptime.
        PageRenderer.InPage<int>("Dark", () =>
        {
            var (view, _) = Instances.InstancesPageTests.Rendered(new Instances.FakeInstances());
            return view;
        }, page =>
        {
            PageRenderer.Settle(page);
            var inset = (double)page.FindResource("S.M");
            var grid = ShellSupport.Under<DataGrid>(page).Single(g => g.Name == "Table");
            // Grouped rows start under their headers, not inset by the group's container.
            var instanceHeader = ShellSupport.Under<DataGridColumnHeader>(grid).First(h => h.Column?.Header as string == "Instance");
            var firstInstance = ShellSupport.Under<DataGridCell>(grid).First(c => c.Column.Header as string == "Instance");
            Assert.Equal(Box(TextOf(instanceHeader), page).Left, Box(TextOf(firstInstance), page).Left, 1);
            foreach (var name in new[] { "GB", "Uptime" })
            {
                var header = ShellSupport.Under<DataGridColumnHeader>(grid).First(h => h.Column?.Header as string == name);
                Assert.Equal(Box(header, page).Right - inset, Box(ShellSupport.Under<TextBlock>(header).First(t => t.Text == name), page).Right, 1);
                foreach (var cell in ShellSupport.Under<DataGridCell>(grid).Where(c => c.Column.Header as string == name).Take(3))
                    Assert.Equal(Box(cell, page).Right - inset, Box(TextOf(cell), page).Right, 1);
            }
            return 0;
        }, 1360, 860);
    }

    [Fact]
    public void A_word_in_a_number_column_is_dim_with_its_reason_and_the_highlight_s_text_when_selected_in_contrast()
    {
        OnMemory((page, grid) =>
        {
            // Bosun's row has no peak in the history yet (the fixture).
            var cell = ShellSupport.Under<DataGridCell>(grid).First(c => c.Column.Header as string == "Peak GB" && TextOf(c).Text == Bosun.Panel.Memory.MemoryPage.NotInHistory);
            var word = TextOf(cell);
            Assert.Equal(((SolidColorBrush)page.FindResource("B.TextDim")).Color, ((SolidColorBrush)word.Foreground).Color);
            Assert.Equal("No sample of this repo is in the last 24 hours' history yet", word.ToolTip);
            Assert.Equal("No sample of this repo is in the last 24 hours' history yet", System.Windows.Automation.AutomationProperties.GetHelpText(word));
        });
        OnMemory((page, grid) =>
        {
            var row = ShellSupport.Under<DataGridRow>(grid).First(r => ((Bosun.Panel.Memory.RepoRow)r.Item).PeakIsWord);
            grid.SelectedItem = row.Item;
            PageRenderer.Settle(page);
            var word = TextOf(ShellSupport.Under<DataGridCell>(row).First(c => c.Column.Header as string == "Peak GB"));
            Assert.Equal(((SolidColorBrush)page.FindResource("B.HighlightText")).Color, ((SolidColorBrush)word.Foreground).Color);
        }, "HighContrast");
    }
}

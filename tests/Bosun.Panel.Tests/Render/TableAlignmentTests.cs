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

    /// <summary>Every number column of a grid: its header's text and each cell's text end at the column's right inset,
    /// in tabular figures, unsorted and sorted either way.</summary>
    static void NumbersEndAtTheirInset(Border page, DataGrid grid, params string[] columns)
    {
        var inset = (double)page.FindResource("S.M");
        foreach (var name in columns)
        {
            var column = grid.Columns.Single(c => c.Header as string == name);
            foreach (var direction in new ListSortDirection?[] { null, ListSortDirection.Ascending, ListSortDirection.Descending })
            {
                column.SortDirection = direction;
                PageRenderer.Settle(page);
                var header = ShellSupport.Under<DataGridColumnHeader>(grid).First(h => ReferenceEquals(h.Column, column));
                var headerText = ShellSupport.Under<TextBlock>(header).First(t => t.Text == name);
                Assert.True(Math.Abs(Box(header, page).Right - inset - Box(headerText, page).Right) <= 1, $"{name}'s header, sorted {direction}, ends at {Box(headerText, page).Right:0}, not {Box(header, page).Right - inset:0}");
                foreach (var cell in ShellSupport.Under<DataGridCell>(grid).Where(c => ReferenceEquals(c.Column, column)))
                {
                    var text = TextOf(cell);
                    Assert.True(Math.Abs(Box(cell, page).Right - inset - Box(text, page).Right) <= 1, $"{name}'s '{text.Text}' ends at {Box(text, page).Right:0}, not {Box(cell, page).Right - inset:0}");
                    Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(text));
                }
            }
            column.SortDirection = null;
        }
    }

    [Fact]
    public void A_number_column_s_cells_and_header_end_at_its_right_inset_in_tabular_figures_sorted_either_way()
    {
        OnMemory((page, grid) => NumbersEndAtTheirInset(page, grid, "Private GB", "Peak GB", "Processes"));
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
            NumbersEndAtTheirInset(page, ShellSupport.Under<DataGrid>(page).Single(g => g.Name == "HistoryTable"), "Lowest available");
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
            NumbersEndAtTheirInset(page, grid, "GB", "Uptime");
            return 0;
        }, 1360, 860);
    }

    [Fact]
    public void Every_word_says_why_and_every_number_its_exact_size_on_the_drawn_page()
    {
        // WSL's snapshot unread with the history read (its rows from their peaks, their memory now unknown), then the
        // history unread (the peaks unknown).
        var wslUnread = Bosun.Core.Memory.Reading<Bosun.Core.Memory.SnapshotDoc>.Fail(Bosun.Core.Memory.ReadingProblem.Failed, "The WSL logger's file could not be read.");
        var historyUnread = Bosun.Core.Memory.Reading<Bosun.Core.Memory.ReportDoc>.Fail(Bosun.Core.Memory.ReadingProblem.Failed, "bosun report ran past 60 s.");
        foreach (var (source, reason) in new[]
                 {
                     (new Memory.MemoryPageTests.Source { Wsl = wslUnread }, "Not available: the WSL snapshot could not be read"),
                     (new Memory.MemoryPageTests.Source { Report = historyUnread }, "Not available: the last 24 hours' history could not be read"),
                 })
        {
            PageRenderer.InPage<int>("Dark", () =>
            {
                var (view, _) = Memory.MemoryPageTests.Rendered(source);
                return view;
            }, page =>
            {
                PageRenderer.Settle(page);
                var grid = ShellSupport.Under<DataGrid>(page).Single(g => g.Name == "ByRepo");
                var said = ShellSupport.Under<DataGridCell>(grid).Select(TextOf).Select(t => t.ToolTip as string).OfType<string>().ToList();
                Assert.Contains(reason, said);
                foreach (var cell in ShellSupport.Under<DataGridCell>(grid).Where(c => c.Column is Bosun.Panel.Controls.NumberColumn))
                {
                    var text = TextOf(cell);
                    Assert.Equal(text.ToolTip, System.Windows.Automation.AutomationProperties.GetHelpText(text));
                    if (Bosun.Panel.Controls.TableColumn.GetIsWord(text))
                        Assert.StartsWith(text.Text == Bosun.Panel.Memory.MemoryPage.NotInHistory ? "No sample" : "Not available:", (string)text.ToolTip, StringComparison.Ordinal);
                    else if (cell.Column.Header as string == "Private GB") Assert.EndsWith("MB private now", (string)text.ToolTip, StringComparison.Ordinal);
                    else if (cell.Column.Header as string == "Peak GB") Assert.EndsWith("MB at its peak in the last 24 hours", (string)text.ToolTip, StringComparison.Ordinal);
                }
                return 0;
            }, 1360, 1100);
        }
        Assert.Equal("< 0.1", Bosun.Panel.Memory.MemoryPage.GbText(40 / 1024.0));
        Assert.Equal("0.0", Bosun.Panel.Memory.MemoryPage.GbText(0));
        Assert.Equal("6.2", Bosun.Panel.Memory.MemoryPage.GbText(6.2));
    }

    [Fact]
    public void Instances_words_take_the_highlight_s_text_when_selected_in_contrast()
    {
        PageRenderer.InPage<int>("HighContrast", () =>
        {
            var (view, _) = Instances.InstancesPageTests.Rendered(new Instances.FakeInstances());
            return view;
        }, page =>
        {
            PageRenderer.Settle(page);
            var grid = ShellSupport.Under<DataGrid>(page).Single(g => g.Name == "Table");
            var line = grid.Items.Cast<Bosun.Panel.Instances.InstanceLine>().First(l => l.MemoryIsWord);
            var row = ShellSupport.Under<DataGridRow>(grid).First(r => ReferenceEquals(r.Item, line));
            var word = TextOf(ShellSupport.Under<DataGridCell>(row).First(c => c.Column.Header as string == "GB"));
            Assert.Equal(((SolidColorBrush)page.FindResource("B.TextDim")).Color, ((SolidColorBrush)word.Foreground).Color);
            grid.SelectedItem = line;
            PageRenderer.Settle(page);
            Assert.Equal(((SolidColorBrush)page.FindResource("B.HighlightText")).Color, ((SolidColorBrush)word.Foreground).Color);
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

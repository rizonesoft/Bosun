using System.ComponentModel;
using System.Windows.Controls;

namespace Bosun.Panel.Instances;

/// <summary>The Instances page's content (D03 T02 §2): bound to its <see cref="InstancesPage"/>.</summary>
public partial class InstancesView : UserControl
{
    public InstancesView() => InitializeComponent();

    /// <summary>
    /// A column header sorts through the page, so GB and Uptime sort by their numbers with "Not available" last both
    /// ways, within the repo groups (the plan review): the grid's own sort would order the words.
    /// </summary>
    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not InstancesPage page) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in Table.Columns)
            if (!ReferenceEquals(column, e.Column)) column.SortDirection = null;
        page.SortBy(e.Column.SortMemberPath, direction);
        e.Column.SortDirection = direction;
    }
}

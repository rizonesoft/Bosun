using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bosun.Panel.Shell;

/// <summary>
/// Everything inside the panel's window (D02 T02 §4). The view keeps the rail and the shell's state in step, guards
/// the rail so a page with unsaved edits is asked about before it is left, passes the three window buttons on to the
/// window it sits in, and keeps the Activity pane showing its newest line.
/// </summary>
public partial class ShellView : UserControl
{
    private ShellViewModel? _shell;
    private bool _atNewest = true;

    public ShellView()
    {
        InitializeComponent();

        // The guard first, so it has refused or agreed before the selection is acted on below.
        SelectionGuard.Attach(Rail, MayGoTo);
        Rail.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Rail) && Rail.SelectedItem is Section section) _shell?.Show(section);
        };

        DataContextChanged += (_, _) => Bind(DataContext as ShellViewModel);
        PaneSplitter.DragCompleted += (_, _) => KeepPaneHeight();
        PaneSplitter.KeyUp += (_, _) => KeepPaneHeight();
        Activity.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnActivityScrolled));
        Loaded += (_, _) => AppIcon.Source ??= IconFrame(20 * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    /// <summary>The window this view sits in: what the three window buttons act on. Null off screen, where they do nothing.</summary>
    public IWindowSurface? Surface { get; set; }

    /// <summary>The maximise button, for the window's hit testing.</summary>
    public Button MaximiseButton => Maximise;

    /// <summary>The Activity pane's height now.</summary>
    public double PaneHeight => PaneRow.ActualHeight;

    private void Bind(ShellViewModel? shell)
    {
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellChanged;
            _shell.Activity.CollectionChanged -= OnActivityChanged;
        }
        _shell = shell;
        if (shell is null)
        {
            Rail.ItemsSource = null;
            return;
        }

        // The rail's rows under their group headings, in the shell's own order.
        var rows = new ListCollectionView(shell.Sections.ToList());
        rows.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Section.Group)));
        Rail.ItemsSource = rows;
        Rail.SetCurrentValue(Selector.SelectedItemProperty, shell.Selected);
        PaneRow.Height = new GridLength(shell.ActivityHeight);

        shell.PropertyChanged += OnShellChanged;
        shell.Activity.CollectionChanged += OnActivityChanged;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_shell is null) return;
        // The shell moved (a page's Open, the palette, a move the person agreed to): the rail follows.
        if (e.PropertyName == nameof(ShellViewModel.Selected) && !ReferenceEquals(Rail.SelectedItem, _shell.Selected))
            Rail.SetCurrentValue(Selector.SelectedItemProperty, _shell.Selected);
        if (e.PropertyName == nameof(ShellViewModel.ActivityHeight) && (!PaneRow.Height.IsAbsolute || Math.Abs(PaneRow.Height.Value - _shell.ActivityHeight) > 0.5))
            PaneRow.Height = new GridLength(_shell.ActivityHeight);
    }

    // ---- the rail's guard ----

    /// <summary>
    /// The guard's question: may the rail move to this row now? A page with no unsaved edits is simply left. One with
    /// edits is not: the move is refused here and now, the person is asked, and the shell moves when they agree.
    /// </summary>
    private bool MayGoTo(int index)
    {
        if (_shell is null) return true;
        // A move while an earlier one is still being asked about or saved is refused, with nothing more asked.
        if (_shell.IsLeaving) return false;
        if (!_shell.Page.HasUnsavedEdits) return true;
        if (index >= 0 && index < Rail.Items.Count && Rail.Items[index] is Section target) _ = LeaveThenGoAsync(_shell, target);
        return false;
    }

    private static async Task LeaveThenGoAsync(ShellViewModel shell, Section target)
    {
        try
        {
            await shell.GoToAsync(target);
        }
        // The question could not be asked, or the page's own code threw outside its save: the person stays where
        // they are with their edits, and the Activity pane says why.
        catch (Exception e)
        {
            shell.Log.Fail($"Open {target.Title}: {e.Message}");
        }
    }

    // ---- the window buttons ----

    private void OnMinimise(object sender, RoutedEventArgs e) => Surface?.Minimise();

    private void OnMaximise(object sender, RoutedEventArgs e) => Surface?.ToggleMaximise();

    private void OnClose(object sender, RoutedEventArgs e) => Surface?.Close();

    /// <summary>The window says whether it is maximised: the button's mark, name and tooltip say what it will do next.</summary>
    public void SetMaximised(bool maximised)
    {
        var does = maximised ? "Restore" : "Maximise";
        AutomationProperties.SetName(Maximise, does);
        Maximise.ToolTip = does;
        // Two overlapping squares for restore, one for maximise.
        MaximiseMark.Data = Geometry.Parse(maximised ? "M0,2 H8 V10 H0 Z M2,2 V0 H10 V8 H8" : "M0,0 H10 V10 H0 Z");
    }

    /// <summary>
    /// The window says the pointer is over the maximise button. Windows keeps the button's mouse input to itself
    /// once it is the snap layouts' button, so its hover is set from here.
    /// </summary>
    public void SetMaximiseHover(bool over)
    {
        if (over)
        {
            Maximise.SetResourceReference(BackgroundProperty, "B.Hover");
            Maximise.SetResourceReference(ForegroundProperty, "B.Text");
        }
        else
        {
            Maximise.ClearValue(BackgroundProperty);
            Maximise.ClearValue(ForegroundProperty);
        }
    }

    // ---- the Activity pane ----

    private void KeepPaneHeight()
    {
        // The height the splitter just gave the row, not the row's laid-out height: layout has not run yet when the
        // drag or the key ends, and the laid-out height is still the old one.
        if (_shell is not null && PaneRow.Height.IsAbsolute) _shell.ActivityHeight = PaneRow.Height.Value;
    }

    private void OnActivityScrolled(object sender, ScrollChangedEventArgs e)
    {
        // Only the person's own scrolling decides: a line being added changes the extent, not where they are looking.
        if (e.ExtentHeightChange == 0) _atNewest = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1;
    }

    private void OnActivityChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The newest line stays in view, unless the person has scrolled back to read an older one.
        if (e.Action == NotifyCollectionChangedAction.Add && _atNewest && Activity.Items.Count > 0)
            Activity.ScrollIntoView(Activity.Items[^1]);
    }

    // ---- the icon ----

    /// <summary>
    /// The app icon's frame for a size in pixels: the smallest frame that is at least that big, since the small ones
    /// are drawn for the pixel grid (design/SPEC.md §18) and a large one scaled down is a smudge. Null when the icon
    /// can't be read, which leaves the title bar with its words.
    /// </summary>
    public static ImageSource? IconFrame(double pixels)
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("/Bosun.Panel;component/Resources/bosun.ico", UriKind.Relative));
            if (resource is null) return null;
            using var stream = resource.Stream;
            var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
            var frame = frames.Where(f => f.PixelWidth >= pixels - 0.5).OrderBy(f => f.PixelWidth).FirstOrDefault() ?? frames.OrderByDescending(f => f.PixelWidth).First();
            frame.Freeze();
            return frame;
        }
        catch (Exception e) when (e is System.IO.IOException or NotSupportedException or InvalidOperationException or System.IO.FileFormatException)
        {
            return null;
        }
    }
}

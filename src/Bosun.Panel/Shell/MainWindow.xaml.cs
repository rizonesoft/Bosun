using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Bosun.Panel.Shell;

/// <summary>
/// The panel's window (D02 T02 §4): the chrome around <see cref="ShellView"/>, and the shell's
/// <see cref="IWindowSurface"/>. Closing it, by its button or Alt+F4, goes through the shell's question about
/// unsaved edits (<see cref="ExitGuard"/>).
/// </summary>
public partial class MainWindow : Window, IWindowSurface
{
    private readonly ExitGuard _exit;
    private readonly SnapLayoutHook _snap;

    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell;
        Shell.Surface = this;
        Icon = ShellView.IconFrame(32);
        _exit = new ExitGuard(shell, this);
        _snap = new SnapLayoutHook(OverMaximise, ToggleMaximise, Shell.SetMaximiseHover);

        // The hook lives as long as the window's handle does.
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source) _snap.Attach(source);
        };
        Closed += (_, _) => _snap.Detach();
        // By the person: a snap, a double click on the title bar, Win+Up.
        StateChanged += (_, _) => ShowState();
    }

    /// <summary>
    /// Asked first as the window closes: true when the tray took the close and hid the window (D02 T02 §7). Null,
    /// or false, and the close is the window's own, through the question about unsaved edits.
    /// </summary>
    public Func<bool>? TakeClose { get; set; }

    /// <summary>The view inside the window.</summary>
    public ShellView View => Shell;

    public bool IsMinimised => WindowState == WindowState.Minimized;

    public bool IsMaximised => WindowState == WindowState.Maximized;

    public void Restore() => Become(WindowState.Normal);

    void IWindowSurface.Activate() => Activate();

    public void Minimise() => Become(WindowState.Minimized);

    public void ToggleMaximise() => Become(IsMaximised ? WindowState.Normal : WindowState.Maximized);

    /// <summary>
    /// Changes the window's state and has the shell show it. Said here as well as on StateChanged: a window that has
    /// no handle yet changes its state without raising that event.
    /// </summary>
    private void Become(WindowState state)
    {
        WindowState = state;
        ShowState();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // Back from another window (Windows' own settings, say): the page on screen reads again what it shows of them.
        (DataContext as ShellViewModel)?.WindowActivated();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && TakeClose?.Invoke() == true)
        {
            e.Cancel = true; // hidden, not closed: the panel carries on in the tray
            return;
        }
        // Held back while the person is asked about unsaved edits; the guard closes the window again if they agree.
        if (!e.Cancel && !_exit.MayCloseNow()) e.Cancel = true;
    }

    private void ShowState()
    {
        Shell.SetMaximised(IsMaximised);
        // A maximised window with its own chrome hangs over the screen's edges by its resize border: the content is
        // brought back inside by that much, or the title bar's top and the buttons' right edge are cut off.
        var border = SystemParameters.WindowResizeBorderThickness;
        Shell.Margin = IsMaximised
            ? new Thickness(border.Left + SystemParameters.FixedFrameVerticalBorderWidth, border.Top + SystemParameters.FixedFrameHorizontalBorderHeight,
                            border.Right + SystemParameters.FixedFrameVerticalBorderWidth, border.Bottom + SystemParameters.FixedFrameHorizontalBorderHeight)
            : new Thickness(0);
    }

    /// <summary>Whether a point in screen pixels is on the maximise button, at this monitor's scale.</summary>
    private bool OverMaximise(int x, int y)
    {
        var button = Shell.MaximiseButton;
        if (!button.IsVisible || PresentationSource.FromVisual(button) is null) return false;
        var origin = button.PointToScreen(new Point(0, 0));
        return SnapLayoutHook.Over(x, y, (int)Math.Round(origin.X), (int)Math.Round(origin.Y), button.RenderSize, VisualTreeHelper.GetDpi(button).DpiScaleX);
    }
}

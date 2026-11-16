using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Bosun.Panel.Tests.Render;

/// <summary>
/// One picture to make: a view in one of its states. <see cref="Create"/> runs on the thread that draws, so the view
/// is made where it is used.
/// </summary>
public sealed record RenderCase(string View, string State, Func<FrameworkElement> Create)
{
    /// <summary>A text the picture must contain, looked for in the rendered tree: what makes this state this state.</summary>
    public string? Shows { get; init; }

    /// <summary>
    /// For a view that loads something after it appears: the task that completes when it has. The picture is taken
    /// once this is done; a view with nothing to wait for leaves it null.
    /// </summary>
    public Func<FrameworkElement, Task>? Ready { get; init; }

    /// <summary>
    /// Drawn on a clear host rather than the theme's <c>Base</c>, so what the view itself leaves clear (the chrome under
    /// Mica, D02 T03 §2) is clear in the picture, and what it paints is its own.
    /// </summary>
    public bool ClearHost { get; init; }

    /// <summary>How long <see cref="Ready"/> may take before the render fails.</summary>
    public TimeSpan ReadyLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long making, laying out and drawing the view may take on top of <see cref="ReadyLimit"/>. A view that
    /// blocks its own thread (a wait inside <c>Loaded</c>, a <see cref="Ready"/> that waits before it returns) stops
    /// the clock <see cref="ReadyLimit"/> is counted on, so the whole render is also timed from outside that thread.
    /// </summary>
    public TimeSpan DrawLimit { get; init; } = TimeSpan.FromSeconds(30);

    public string Name => $"{View}.{State}";
}

/// <summary>A view that couldn't be made into a picture, named, with why.</summary>
public sealed class RenderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A drawn view: its PNG and every text in its rendered tree.</summary>
public sealed record Rendered(byte[] Png, IReadOnlyList<string> Texts);

/// <summary>
/// Draws a WPF view to a PNG with no desktop (D02 T02 §9): the view is rooted in a presentation source that is never
/// shown, so <c>Loaded</c> fires and bindings run as they do in the app, and drawn at 96 DPI whatever the machine's
/// scaling, so two machines give pictures of the same size. A <see cref="Window"/> can't be drawn without being
/// shown: a window registers its content, and its chrome is proved by its own tests and an attended capture.
/// </summary>
public static class PageRenderer
{
    public const int Width = 1360;
    public const int Height = 860;
    /// <summary>The panel's two themes, and Windows' contrast colours over Dark (D02 T03 §10), in Night sky.</summary>
    public static readonly IReadOnlyList<string> Themes = ["Dark", "Light", "HighContrast"];

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public static Rendered Render(RenderCase view, string theme, int width = Width, int height = Height)
    {
        // Timed from here, outside the thread that draws: a view that blocks that thread stops every clock on it.
        var limit = view.ReadyLimit + view.DrawLimit;
        if (Sta.TryRun(() => RenderOnThisThread(view, theme, width, height), limit, out var drawn)) return drawn;
        throw new RenderException(FormattableString.Invariant($"{view.Name}: the view held its thread for more than {limit.TotalSeconds:0.###} s (making it, loading it or drawing it never came back), so no picture was taken"));
    }

    private static Rendered RenderOnThisThread(RenderCase view, string theme, int width, int height)
    {
        if (!Themes.Contains(theme)) throw new RenderException($"{view.Name}: no theme named '{theme}'; the themes are {string.Join(" and ", Themes)}");
        if (width <= 0 || height <= 0) throw new RenderException($"{view.Name}: a picture of {width} x {height} can't be drawn; both must be more than zero");

        // Awaits inside the view, and its Ready task, come back to this thread's dispatcher, as in the app.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        FrameworkElement element;
        try
        {
            element = view.Create();
        }
        catch (Exception e)
        {
            throw new RenderException($"{view.Name}: the view couldn't be made: {e.Message}", e);
        }
        if (element is Window) throw new RenderException($"{view.Name}: a Window can't be drawn without being shown; register its content");

        var page = Page(element, theme, width, height, clear: view.ClearHost);
        using var source = Hidden("Bosun render: " + view.Name, page);

        Idle();
        if (view.Ready is { } ready)
        {
            Task loading;
            try
            {
                loading = ready(element);
            }
            catch (Exception e)
            {
                throw new RenderException($"{view.Name}: the view's Ready failed: {e.Message}", e);
            }
            if (!PumpUntil(loading, view.ReadyLimit))
                throw new RenderException(FormattableString.Invariant($"{view.Name}: the view was not ready after {view.ReadyLimit.TotalSeconds:0.###} s, so no picture was taken"));
            if (loading.IsFaulted) throw new RenderException($"{view.Name}: the view's Ready failed: {loading.Exception!.GetBaseException().Message}", loading.Exception.GetBaseException());
            if (loading.IsCanceled) throw new RenderException($"{view.Name}: the view's Ready was cancelled, so no picture was taken");
            Idle();
        }

        // Laid out at exactly the size asked for, whatever the hidden window made of it.
        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        page.UpdateLayout();
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            throw new RenderException(FormattableString.Invariant($"{view.Name}: the view has no size ({element.ActualWidth:0} x {element.ActualHeight:0}), so there is nothing to draw"));

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var pixels = new uint[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        if (Array.TrueForAll(pixels, pixel => pixel == pixels[0]))
            throw new RenderException($"{view.Name}: the picture is one flat colour (#{pixels[0]:X8}): the view drew nothing");

        var texts = new List<string>();
        Texts(page, texts);
        if (view.Shows is { } shows && !texts.Any(text => text.Contains(shows, StringComparison.Ordinal)))
            throw new RenderException($"{view.Name}: the picture doesn't show '{shows}', which is what this state is; it shows: {string.Join(" | ", texts)}");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        return new Rendered(png.ToArray(), texts);
    }

    /// <summary>The panel's control styles, as the application merges them: the compiled Theme/Controls.xaml.</summary>
    public static ResourceDictionary Controls() =>
        (ResourceDictionary)Application.LoadComponent(new Uri("/Bosun.Panel;component/Theme/Controls.xaml", UriKind.Relative));

    /// <summary>
    /// The page a view sits in, as the application gives it one (D02 T01 §4): the named theme with the control styles
    /// merged after it, as App.xaml merges them, on the theme's base colour, with the font, size and text colour a
    /// window gives its content, at the size asked for.
    /// </summary>
    public static Border Page(FrameworkElement element, string theme, int width, int height, Bosun.Panel.Theme.SystemPalette? palette = null, bool clear = false)
    {
        // A contrast theme is Windows' colours over the generated Dark theme, as ThemeService merges them, in a fixed
        // palette so the picture is the same on every machine (Night sky unless a test names another).
        var contrast = theme == "HighContrast";
        ResourceDictionary colours;
        using (var stream = File.OpenRead(TestPaths.GeneratedTheme((contrast ? "Dark" : theme) + ".xaml"))) colours = (ResourceDictionary)XamlReader.Load(stream);
        var controls = Controls();

        var page = new Border { Width = width, Height = height, Child = element };
        page.Resources.MergedDictionaries.Add(colours);
        if (contrast)
        {
            page.Resources.MergedDictionaries.Add(Bosun.Panel.Theme.HighContrastTheme.Build(palette ?? Helpers.ContrastPalettes.NightSky, colours));
            Bosun.Panel.Theme.HighContrastMode.SetIsOn(page, true);
        }
        page.Resources.MergedDictionaries.Add(controls);
        if (clear) page.Background = Brushes.Transparent;
        else page.SetResourceReference(Border.BackgroundProperty, "B.Base");
        System.Windows.Documents.TextElement.SetFontFamily(page, (FontFamily)controls["F.Ui"]);
        page.SetResourceReference(System.Windows.Documents.TextElement.FontSizeProperty, "T.Body");
        page.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "B.Text");
        return page;
    }

    /// <summary>A presentation source that is never shown, holding <paramref name="root"/>: Loaded fires and bindings run, and nothing appears.</summary>
    private static HwndSource Hidden(string name, Visual root)
    {
        var parameters = new HwndSourceParameters(name)
        {
            WindowStyle = WS_POPUP, // and never WS_VISIBLE: this window is not shown
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            Width = 1,
            Height = 1,
        };
        return new HwndSource(parameters) { SizeToContent = SizeToContent.WidthAndHeight, RootVisual = root };
    }

    /// <summary>
    /// For a test of a control rather than a picture: puts <paramref name="create"/>'s element in a page of the
    /// theme, in a source that is never shown, lays it out and hands the page to <paramref name="read"/>, all on one
    /// STA thread. The control is live: its templates are applied and its triggers run.
    /// </summary>
    public static T InPage<T>(string theme, Func<FrameworkElement> create, Func<Border, T> read, int width = 480, int height = 320,
        Bosun.Panel.Theme.SystemPalette? palette = null) => Sta.Run(() =>
    {
        // Awaits inside the control come back to this thread's dispatcher, as in the app.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var page = Page(create(), theme, width, height, palette);
        using var source = Hidden("Bosun control test", page);
        Settle(page);
        return read(page);
    });

    /// <summary>Lets queued work run and lays the page out again: what a test calls after it changes a control.</summary>
    public static void Settle(Border page)
    {
        Idle();
        page.Measure(new Size(page.Width, page.Height));
        page.Arrange(new Rect(0, 0, page.Width, page.Height));
        page.UpdateLayout();
        Idle();
    }

    /// <summary>The page's pixels as 0xAARRGGBB, row by row, drawn at 96 DPI.</summary>
    public static uint[] Pixels(Border page)
    {
        var bitmap = new RenderTargetBitmap((int)page.Width, (int)page.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var pixels = new uint[(int)page.Width * (int)page.Height];
        bitmap.CopyPixels(pixels, (int)page.Width * 4, 0);
        return pixels;
    }

    /// <summary>Every text a reader of <paramref name="node"/> would see, in tree order.</summary>
    public static IReadOnlyList<string> TextsOf(DependencyObject node)
    {
        var found = new List<string>();
        Texts(node, found);
        return found;
    }

    /// <summary>Lets everything queued on this thread's dispatcher run: layout, bindings, templates, Loaded.</summary>
    private static void Idle() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Runs the dispatcher until <paramref name="task"/> ends or <paramref name="limit"/> passes; false when it passed.</summary>
    private static bool PumpUntil(Task task, TimeSpan limit)
    {
        if (task.IsCompleted) return true;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(limit, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return task.IsCompleted;
    }

    /// <summary>Every text a reader of the picture would see, in tree order.</summary>
    private static void Texts(DependencyObject node, List<string> found)
    {
        if (node is UIElement { Visibility: not Visibility.Visible }) return;
        if (node is TextBlock { Text.Length: > 0 } block) found.Add(block.Text);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Texts(VisualTreeHelper.GetChild(node, i), found);
    }

    /// <summary>The pixels of a PNG, as 0xAARRGGBB, row by row.</summary>
    public static (int Width, int Height, uint[] Pixels) Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new uint[converted.PixelWidth * converted.PixelHeight];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (converted.PixelWidth, converted.PixelHeight, pixels);
    }
}

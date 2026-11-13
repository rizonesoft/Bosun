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

    /// <summary>How long <see cref="Ready"/> may take before the render fails.</summary>
    public TimeSpan ReadyLimit { get; init; } = TimeSpan.FromSeconds(5);

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
    public static readonly IReadOnlyList<string> Themes = ["Dark", "Light"];

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public static Rendered Render(RenderCase view, string theme, int width = Width, int height = Height) =>
        Sta.Run(() => RenderOnThisThread(view, theme, width, height));

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

        ResourceDictionary colours;
        using (var stream = File.OpenRead(TestPaths.GeneratedTheme(theme + ".xaml"))) colours = (ResourceDictionary)XamlReader.Load(stream);

        // The page sits where it sits in the app: on the theme's base colour, at the size asked for.
        var page = new Border { Width = width, Height = height, Child = element };
        page.Resources.MergedDictionaries.Add(colours);
        page.SetResourceReference(Border.BackgroundProperty, "B.Base");

        var parameters = new HwndSourceParameters("Bosun render: " + view.Name)
        {
            WindowStyle = WS_POPUP, // and never WS_VISIBLE: this window is not shown
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            Width = 1,
            Height = 1,
        };
        using var source = new HwndSource(parameters) { SizeToContent = SizeToContent.WidthAndHeight, RootVisual = page };

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

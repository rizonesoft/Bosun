using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Xunit;

namespace Bosun.Panel.Tests.Render;

/// <summary>
/// Off-screen page renders (D02 T02 §9): a view becomes a PNG with no desktop, in both themes, showing what the app
/// would show, and the folder a reviewer reads holds one whole, current run or no index at all.
/// </summary>
public class PageRendererTests
{
    private static uint Token(string theme, string name)
    {
        using var tokens = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Repo(), "design", "tokens", "colors.json")));
        var digits = tokens.RootElement.GetProperty(theme.ToLowerInvariant()).GetProperty(name).GetString()!.TrimStart('#');
        var value = uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return digits.Length == 6 ? 0xFF000000 | value : value;
    }

    private static RenderCase Sample(string state = "normal") => RenderCatalog.Cases.Single(view => view.Name == "sample." + state);

    private static string Refused(RenderCase view, string theme = "Dark", int width = 400, int height = 300) =>
        Assert.Throws<RenderException>(() => PageRenderer.Render(view, theme, width, height)).Message;

    private static RenderCase Text(string name, string text) => new("probe", name, () => new TextBlock { Text = text, FontSize = 40 });

    // ---- one view, drawn ----

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void A_view_is_drawn_at_the_size_asked_in_the_themes_colours(string theme)
    {
        var drawn = PageRenderer.Render(Sample(), theme);
        var (width, height, pixels) = PageRenderer.Decode(drawn.Png);

        Assert.Equal((1360, 860), (width, height));
        Assert.Equal(Token(theme, "Surface"), pixels[SampleView.PanelY * width + SampleView.PanelX]); // the panel, painted with B.Surface
        Assert.Equal(Token(theme, "Base"), pixels[5 * width + 5]); // the page around it
        // Text is there to read: anti-aliased glyphs make many colours, where a panel alone makes two.
        Assert.True(pixels.Distinct().Count() > 20, "the picture holds text, not only two flat colours");
        Assert.Equal(["Queue", "build Bosun", "test Recto", "build Steward"], drawn.Texts);
    }

    [Fact]
    public void The_two_themes_give_two_pictures()
    {
        var dark = PageRenderer.Decode(PageRenderer.Render(Sample(), "Dark").Png).Pixels;
        var light = PageRenderer.Decode(PageRenderer.Render(Sample(), "Light").Png).Pixels;

        Assert.False(dark.AsSpan().SequenceEqual(light));
        Assert.NotEqual(Token("Dark", "Surface"), Token("Light", "Surface"));
    }

    [Fact]
    public void A_picture_is_the_same_size_whatever_size_is_asked()
    {
        var (width, height, _) = PageRenderer.Decode(PageRenderer.Render(Sample(), "Dark", 640, 480).Png);

        Assert.Equal((640, 480), (width, height));
    }

    [Fact]
    public void Each_state_shows_its_own_content_and_not_the_others()
    {
        var normal = PageRenderer.Render(Sample("normal"), "Dark");
        var empty = PageRenderer.Render(Sample("empty"), "Dark");

        Assert.Contains("test Recto", normal.Texts);
        Assert.DoesNotContain("Nothing here yet", normal.Texts);
        Assert.Contains("Nothing here yet", empty.Texts);
        Assert.DoesNotContain("test Recto", empty.Texts);
        Assert.False(PageRenderer.Decode(normal.Png).Pixels.AsSpan().SequenceEqual(PageRenderer.Decode(empty.Png).Pixels));
    }

    // ---- views that load ----

    /// <summary>A view whose text arrives the way a page's does: bound, with its data set once it is loaded.</summary>
    private sealed class LoadedView : UserControl
    {
        public LoadedView()
        {
            var text = new TextBlock { FontSize = 40 };
            text.SetBinding(TextBlock.TextProperty, new Binding("Message"));
            Content = text;
            Loaded += (_, _) => DataContext = new { Message = "set when loaded" };
        }
    }

    /// <summary>A view that fills itself a moment after it is loaded, and says when it has.</summary>
    private sealed class LateView : UserControl
    {
        private readonly TaskCompletionSource _filled = new();

        public Task Filled => _filled.Task;

        public LateView(int afterMs, Exception? failure = null)
        {
            var text = new TextBlock { FontSize = 40, Text = "loading" };
            Content = text;
            Loaded += async (_, _) =>
            {
                await Task.Delay(afterMs);
                if (failure is not null)
                {
                    _filled.SetException(failure);
                    return;
                }
                text.Text = "arrived late"; // on the view's own thread: the await came back to its dispatcher
                _filled.SetResult();
            };
        }
    }

    [Fact]
    public void Content_set_when_the_view_is_loaded_is_in_the_picture()
    {
        // A tree that hangs from nothing never gets Loaded: the picture would be blank and nothing would say so.
        var drawn = PageRenderer.Render(new("probe", "loaded", () => new LoadedView()) { Shows = "set when loaded" }, "Dark", 600, 200);

        Assert.Equal(["set when loaded"], drawn.Texts);
    }

    [Fact]
    public void A_view_that_fills_itself_later_is_drawn_once_it_says_it_is_ready()
    {
        var late = new RenderCase("probe", "late", () => new LateView(200)) { Ready = view => ((LateView)view).Filled, Shows = "arrived late" };

        Assert.Equal(["arrived late"], PageRenderer.Render(late, "Dark", 600, 200).Texts);
    }

    [Fact]
    public void A_view_whose_loading_fails_or_never_ends_is_refused_by_name()
    {
        var failing = new RenderCase("probe", "failing", () => new LateView(50, new InvalidOperationException("the queue can't be read"))) { Ready = view => ((LateView)view).Filled };
        var refusal = Assert.Throws<RenderException>(() => PageRenderer.Render(failing, "Dark", 600, 200));
        Assert.Contains("probe.failing", refusal.Message);
        Assert.Contains("the queue can't be read", refusal.Message);
        Assert.IsType<InvalidOperationException>(refusal.InnerException);

        var stuck = new RenderCase("probe", "stuck", () => new LateView(60_000)) { Ready = view => ((LateView)view).Filled, ReadyLimit = TimeSpan.FromMilliseconds(300) };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Contains("probe.stuck: the view was not ready after 0.3 s", Refused(stuck));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"given up at its limit, not after {clock.Elapsed}");

        var throws = new RenderCase("probe", "throws", () => new TextBlock { Text = "x" }) { Ready = _ => throw new InvalidOperationException("no such loader") };
        Assert.Contains("probe.throws: the view's Ready failed: no such loader", Refused(throws));
    }

    [Fact]
    public void A_view_that_blocks_its_own_thread_is_given_up_at_its_limit_and_named()
    {
        // A view that waits inside Loaded, or a Ready that waits before it returns its task, holds the very thread
        // the readiness limit is counted on, so that limit never comes (the completion review of D02 T02 §9: an
        // unattended run hung here). The render is given up from outside the thread. The gate bounds what the test
        // itself leaves behind: it opens when the test ends, or after 30 s at the latest.
        using var gate = new ManualResetEventSlim();
        try
        {
            var loading = new RenderCase("probe", "blocked", () =>
            {
                var view = new TextBlock { Text = "x" };
                view.Loaded += (_, _) => gate.Wait(TimeSpan.FromSeconds(30));
                return view;
            })
            { ReadyLimit = TimeSpan.FromMilliseconds(100), DrawLimit = TimeSpan.FromMilliseconds(400) };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.Contains("probe.blocked: the view held its thread for more than 0.5 s", Refused(loading));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"given up at its limit, not after {clock.Elapsed}");

            var ready = new RenderCase("probe", "blocked-ready", () => new TextBlock { Text = "x" })
            {
                Ready = _ => { gate.Wait(TimeSpan.FromSeconds(30)); return Task.CompletedTask; },
                ReadyLimit = TimeSpan.FromMilliseconds(100),
                DrawLimit = TimeSpan.FromMilliseconds(400),
            };
            Assert.Contains("probe.blocked-ready: the view held its thread for more than 0.5 s", Refused(ready));
        }
        finally
        {
            gate.Set();
        }

        // And a view that is merely slow, inside its limits, is drawn.
        Assert.Equal(["arrived late"], PageRenderer.Render(new RenderCase("probe", "late", () => new LateView(200)) { Ready = view => ((LateView)view).Filled, Shows = "arrived late" }, "Dark", 600, 200).Texts);
    }

    // ---- what can't be a picture ----

    [Fact]
    public void What_cant_be_a_picture_is_refused_by_name_with_the_reason()
    {
        Assert.Contains("probe.theme: no theme named 'Sepia'", Refused(Text("theme", "x"), theme: "Sepia"));
        Assert.Contains("probe.theme: no theme named 'dark'", Refused(Text("theme", "x"), theme: "dark"));
        Assert.Contains("probe.size: a picture of 0 x 300 can't be drawn", Refused(Text("size", "x"), width: 0));
        Assert.Contains("probe.size: a picture of 400 x -1 can't be drawn", Refused(Text("size", "x"), height: -1));
        Assert.Contains("probe.window: a Window can't be drawn without being shown; register its content", Refused(new("probe", "window", () => new Window())));
        Assert.Contains("probe.collapsed: the view has no size", Refused(new("probe", "collapsed", () => new TextBlock { Text = "x", Visibility = Visibility.Collapsed })));
        Assert.Contains("probe.blank: the picture is one flat colour", Refused(new("probe", "blank", () => new Border())));
        Assert.Contains("probe.wrong: the picture doesn't show 'rows', which is what this state is; it shows: nothing yet", Refused(Text("wrong", "nothing yet") with { Shows = "rows" }));

        var broken = Assert.Throws<RenderException>(() => PageRenderer.Render(new("probe", "broken", () => throw new InvalidOperationException("no data source")), "Dark"));
        Assert.Contains("probe.broken: the view couldn't be made: no data source", broken.Message);
        Assert.IsType<InvalidOperationException>(broken.InnerException);
    }

    [Fact]
    public void What_a_test_throws_on_the_STA_thread_reaches_it_as_itself()
    {
        var thrown = Assert.Throws<FormatException>(() => Sta.Run(() => throw new FormatException("its own message")));

        Assert.Equal("its own message", thrown.Message);
        Assert.Equal(ApartmentState.STA, Sta.Run(() => Thread.CurrentThread.GetApartmentState()));
    }

    // ---- the catalogue ----

    [Fact]
    public void The_catalogue_is_sound()
    {
        Assert.Empty(RenderCatalog.Problems(RenderCatalog.Cases));
        Assert.All(RenderCatalog.Cases, view => Assert.NotNull(view.Shows)); // every state says what makes it that state
    }

    [Fact]
    public void A_catalogue_with_a_repeated_row_or_a_name_that_cant_be_a_file_name_is_refused()
    {
        RenderCase Row(string view, string state) => new(view, state, () => new TextBlock { Text = "x" });

        Assert.Equal(["queue.normal is registered 2 times"], RenderCatalog.Problems([Row("queue", "normal"), Row("queue", "normal"), Row("queue", "empty")]));
        Assert.Equal(["view name 'Queue' must be lower-case letters, digits and hyphens"], RenderCatalog.Problems([Row("Queue", "normal")]));
        Assert.Equal(["state name 'no data' of queue must be lower-case letters, digits and hyphens"], RenderCatalog.Problems([Row("queue", "no data")]));
        Assert.Equal(["view name 'a.b' must be lower-case letters, digits and hyphens"], RenderCatalog.Problems([Row("a.b", "normal")]));
        Assert.Equal(["view name '..\\x' must be lower-case letters, digits and hyphens"], RenderCatalog.Problems([Row("..\\x", "normal")]));
        Assert.Empty(RenderCatalog.Problems([Row("queue-2", "no-data")]));

        using var folder = new Scratch();
        var refusal = Assert.Throws<RenderException>(() => RenderCatalog.WriteAll([Row("Queue", "normal")], folder.Path));
        Assert.Contains("view name 'Queue'", refusal.Message);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    // ---- the folder a reviewer reads ----

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("bosun-renders-").FullName;

        public string[] Names() => Directory.GetFiles(Path).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static readonly string[] SampleFiles =
        ["index.json", "sample.empty.dark.png", "sample.empty.highcontrast.png", "sample.empty.light.png",
         "sample.normal.dark.png", "sample.normal.highcontrast.png", "sample.normal.light.png"];

    /// <summary>
    /// The tests' own view, in its states: what the tests of the folder's rules draw, so they hold whatever views the
    /// panel has registered since (the run over the whole catalogue is the test below).
    /// </summary>
    private static readonly IReadOnlyList<RenderCase> Samples = [.. RenderCatalog.Cases.Where(view => view.View == "sample")];

    [Fact]
    public void Every_registered_view_is_written_in_each_state_and_theme_with_an_index()
    {
        // The real folder: this is the run that writes the renders a review reads (scripts/render-pages.ps1 runs it).
        // It puts nothing of its own there and removes nothing but renders: a reviewer's notes beside them are theirs
        // (round 1: this test planted a notes.txt there and deleted it, and with it one a person had written).
        var folder = RenderCatalog.Folder();
        {
            var written = RenderCatalog.WriteAll(RenderCatalog.Cases, folder);

            Assert.Equal(RenderCatalog.Cases.Count * PageRenderer.Themes.Count, written.Count);
            Assert.Equal(3, PageRenderer.Themes.Count); // Dark, Light and Windows' contrast colours (D02 T03 §10)
            var expected = RenderCatalog.Cases.SelectMany(view => new[] { $"{view.Name}.dark.png", $"{view.Name}.light.png", $"{view.Name}.highcontrast.png" }).Append("index.json").Order(StringComparer.Ordinal);
            // Of the catalogue's own files: a picture somebody else put there (a screenshot.png) is not a render, and
            // must not fail the run that Clear was careful to leave it through (the completion review).
            Assert.Equal(expected, Directory.GetFiles(folder).Select(Path.GetFileName).Where(name => RenderCatalog.Owns(name!)).Order(StringComparer.Ordinal));

            using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "index.json")));
            Assert.Matches("^([0-9a-f]{40}|unknown)$", index.RootElement.GetProperty("commit").GetString());
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", index.RootElement.GetProperty("written_at").GetString());
            var rows = index.RootElement.GetProperty("renders").EnumerateArray().ToArray();
            Assert.Equal(written.Order(StringComparer.Ordinal), rows.Select(row => row.GetProperty("file").GetString()!).Order(StringComparer.Ordinal));
            foreach (var row in rows)
            {
                var file = row.GetProperty("file").GetString()!;
                Assert.Equal(file, $"{row.GetProperty("view").GetString()}.{row.GetProperty("state").GetString()}.{row.GetProperty("theme").GetString()}.png");
                Assert.Equal((PageRenderer.Width, PageRenderer.Height), (row.GetProperty("width").GetInt32(), row.GetProperty("height").GetInt32()));
                var (width, height, pixels) = PageRenderer.Decode(File.ReadAllBytes(Path.Combine(folder, file)));
                Assert.Equal((PageRenderer.Width, PageRenderer.Height), (width, height));
                Assert.True(pixels.Distinct().Count() > 2, $"{file} is not blank");
            }
        }
    }

    [Fact]
    public void A_run_removes_the_pictures_an_earlier_run_left_and_nothing_else()
    {
        using var folder = new Scratch();
        File.WriteAllText(Path.Combine(folder.Path, "removed-view.normal.dark.png"), "a render of a view that is gone");
        File.WriteAllText(Path.Combine(folder.Path, "notes.txt"), "a reviewer's notes");

        RenderCatalog.WriteAll(Samples, folder.Path);

        Assert.Equal([.. SampleFiles.Append("notes.txt").Order(StringComparer.Ordinal)], folder.Names());
        Assert.Equal("a reviewer's notes", File.ReadAllText(Path.Combine(folder.Path, "notes.txt")));

        // And when the run fails, the notes are still there.
        Assert.Throws<RenderException>(() => RenderCatalog.WriteAll([Sample() with { Shows = "not on the page" }], folder.Path));
        Assert.Equal(["notes.txt"], folder.Names());
        Assert.Equal("a reviewer's notes", File.ReadAllText(Path.Combine(folder.Path, "notes.txt")));
    }

    [Fact]
    public void A_state_that_draws_another_states_picture_or_not_its_own_content_fails_the_run_and_leaves_no_index()
    {
        using var folder = new Scratch();
        File.WriteAllText(Path.Combine(folder.Path, "index.json"), "the index of an earlier, good run");
        var normal = Sample("normal");

        // `empty` given the `normal` factory: the state is ignored.
        var same = Assert.Throws<RenderException>(() => RenderCatalog.WriteAll([normal, normal with { State = "empty" }], folder.Path));
        Assert.Contains("sample.empty: draws the same picture as sample.normal", same.Message);
        Assert.Contains("no index was written", same.Message);
        Assert.DoesNotContain("index.json", folder.Names());

        // And with the content it names missing: the two factories were swapped.
        var swapped = Assert.Throws<RenderException>(() => RenderCatalog.WriteAll([Sample("normal") with { Create = Sample("empty").Create }, Sample("empty") with { Create = Sample("normal").Create }], folder.Path));
        Assert.Contains("sample.normal: the picture doesn't show 'test Recto'", swapped.Message);
        Assert.Contains("sample.empty: the picture doesn't show 'Nothing here yet'", swapped.Message);
        Assert.Empty(folder.Names());
    }

    [Fact]
    public void A_picture_that_cant_be_written_fails_the_run_naming_it_and_leaves_no_index()
    {
        using var folder = new Scratch();
        RenderCatalog.WriteAll(Samples, folder.Path);
        Assert.Equal(SampleFiles, folder.Names());

        var held = Path.Combine(folder.Path, "sample.empty.light.png");
        // Held open by something else, as a viewer holds a picture: it can be neither cleared nor replaced.
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var refusal = Assert.ThrowsAny<Exception>(() => RenderCatalog.WriteAll(Samples, folder.Path));
            Assert.Contains("sample.empty.light.png", refusal.Message);
            Assert.DoesNotContain("index.json", folder.Names());
            Assert.DoesNotContain(folder.Names(), name => name.EndsWith(".tmp", StringComparison.Ordinal));
        }

        RenderCatalog.WriteAll(Samples, folder.Path); // released: the next run is whole again
        Assert.Equal(SampleFiles, folder.Names());
    }

    [Fact]
    public void Clearing_takes_only_the_renders_and_their_index()
    {
        using var folder = new Scratch();
        foreach (var name in new[] { "index.json", "index.json.tmp", "queue.normal.dark.png", "queue.normal.light.png.tmp", "notes.txt", "Queue.normal.dark.png.bak", "screenshot.png", "queue.normal.sepia.png" })
            File.WriteAllText(Path.Combine(folder.Path, name), "x");

        RenderCatalog.Clear(folder.Path);

        Assert.Equal(["Queue.normal.dark.png.bak", "notes.txt", "queue.normal.sepia.png", "screenshot.png"], folder.Names());
    }
}

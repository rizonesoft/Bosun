using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Bosun.Panel.Tests.Render;

/// <summary>
/// Every view a reviewer can look at, one row per view and state (D02 T02 §9). A surface section adds its views here
/// as it lands; <see cref="WriteAll"/> draws each row in both themes into <c>build/renders/</c>.
/// </summary>
public static partial class RenderCatalog
{
    public static readonly IReadOnlyList<RenderCase> Cases =
    [
        new("sample", "normal", () => new SampleView(new SampleData("Queue", ["build Bosun", "test Recto", "build Steward"], null))) { Shows = "test Recto" },
        new("sample", "empty", () => new SampleView(new SampleData("Queue", [], "Nothing here yet"))) { Shows = "Nothing here yet" },
        // The control styles of Theme/Controls.xaml (D02 T01 §4), each in its states, and a table.
        new("controls", "gallery", ControlsGallery.Gallery) { Shows = "Stop job" },
        new("controls", "table", ControlsGallery.Table) { Shows = "Memory (MB)" },
        // The shell (D02 T02 §4): the whole frame as the panel starts, a page in each of its four states, and the
        // question asked when a page with unsaved changes is being left.
        new("shell", "overview", () => new Bosun.Panel.Shell.ShellView { DataContext = Shell.Shells.Panel() }) { Shows = "Panel ready" },
        // The backdrop (D02 T03 §2), on a clear host: Mica leaves the title bar and rail clear for Windows' backdrop,
        // the page's sheet still solid. Solid is the shell as it starts, drawn above as shell.overview.
        new("shell", "backdrop-mica", () => Backdropped(Bosun.Panel.Shell.BackdropKind.Mica)) { Shows = "Panel ready", ClearHost = true },
        // Count badges (D02 T03 §3): the selected row with a two-figure count, two others with one figure each.
        new("shell", "badges", Badged) { Shows = "12" },
        new("page", "loading", () => Loading(new Shell.TestPage())) { Shows = "Loading Queue" },
        // The loading state's three shapes (D02 T03 §7, design/SPEC.md §19), as each page declares its own.
        new("skeleton", "card", () => Loading(new Shell.TestPage("Overview", "Memory, the queue and dev instances at a glance."))) { Shows = "Loading Overview" },
        new("skeleton", "table", () => Loading(new Shell.TestPage("Instances", "Every dev instance on both sides.") { LoadingShape = Bosun.Panel.Controls.SkeletonShape.Table })) { Shows = "Loading Instances" },
        new("skeleton", "gauge", () => Loading(new Shell.TestPage("Memory", "Live headroom.") { LoadingShape = Bosun.Panel.Controls.SkeletonShape.Gauge })) { Shows = "Loading Memory" },
        // Data no longer current, with how old it is (a page reading WSL, with WSL stopped), and values that can't be read.
        new("page", "stale", Stale) { Shows = "WSL is stopped. Showing what was read 12 minutes ago." },
        new("value", "not-available", NotAvailableValues) { Shows = "It could not be read." },
        // What a contrast theme changes beyond colour (D02 T03 §10): the focus ring, a drop-down's highlighted and
        // selected items, and the Settings page saying why its theme choice waits.
        new("controls", "states", States) { Shows = "Highlighted" },
        new("settings", "contrast", SettingsInContrast) { Shows = "Windows' contrast theme is in use" },
        // The pressure alert threshold (D03 T01 §4): a value that can't be one, refused beside the field.
        new("settings", "threshold-error", () => Settings(_ => { }, f => f.Page.PressureFloorText = "lots")) { Shows = "Enter a whole number of MB" },
        new("page", "data", () => Framed(new Shell.TestPage().Data())) { Shows = "two slots, one waiting" },
        new("page", "empty", () => Framed(new Shell.TestPage().Empty())) { Shows = "Nothing is waiting." },
        new("page", "failed", () => Framed(new Shell.TestPage().Failed())) { Shows = "bosun: the queue file is damaged at line 3" },
        // A page that has a refresh: the Refresh button in its header (D02 T02 §8).
        new("page", "refresh", () => Framed(new Shell.TestPage().Data().Polling(new Bosun.Core.RefreshScheduler(), Shell.Shells.Log()))) { Shows = "Refresh" },
        new("dialog", "leave", LeaveQuestion) { Shows = "Save your changes to Queue?" },
        // The Settings page (D02 T02 §5): as it opens, after a damaged file was moved aside, when the file can't be
        // read at all, and after a save that only half took.
        new("settings", "general", () => Settings(_ => { })) { Shows = "Start with Windows, in the tray" },
        // With memory pressure notifications turned off (D02 T02 §7).
        new("settings", "notifications-off", () => Settings(f => f.Files.Put(Bosun.Panel.Tests.Settings.SettingsFixture.FilePath,
            "{\"schema_version\": 1, \"notify_memory_pressure\": false}"))) { Shows = "Notify me about memory pressure" },
        new("settings", "restored", () => Settings(f => f.Files.Put(Bosun.Panel.Tests.Settings.SettingsFixture.FilePath, "not json at all"))) { Shows = "panel.json.bad" },
        new("settings", "unavailable", () => Settings(f =>
        {
            f.Files.Put(Bosun.Panel.Tests.Settings.SettingsFixture.FilePath, "{}");
            f.Files.FailRead = new IOException("The process cannot access the file because it is being used by another process.");
        })) { Shows = "panel.json could not be read" },
        new("settings", "half-saved", () => Settings(_ => { }, f =>
        {
            f.Page.SelectedTheme = f.Option(Bosun.Core.ThemeChoice.Dark);
            f.Page.StartWithWindows = true;
            f.Key.FailWrite = new UnauthorizedAccessException("Access to the registry key is denied.");
            _ = f.Page.SaveAsync();
        })) { Shows = "Not saved: Start with Windows could not be turned on" },
    ];

    /// <summary>A page in the frame every page is drawn in, with the margin the shell gives it.</summary>
    private static FrameworkElement Framed(Shell.TestPage page)
    {
        var frame = new Bosun.Panel.Shell.PageFrame { DataContext = page, Margin = new Thickness(26, 22, 26, 12) };
        Shell.TestPage.AddTemplate(frame);
        return frame;
    }

    /// <summary>A page loading, on screen, in its frame, its skeleton drawn at once: a picture can't wait 150 ms.</summary>
    private static FrameworkElement Loading(Shell.TestPage page)
    {
        var frame = new Bosun.Panel.Shell.PageFrame { SkeletonDelay = TimeSpan.Zero, DataContext = page.Loading(), Margin = new Thickness(26, 22, 26, 12) };
        Shell.TestPage.AddTemplate(frame);
        page.Show();
        return frame;
    }

    /// <summary>The shell with counts waiting: Overview (selected) 12, Queue 3, Settings 1.</summary>
    private static FrameworkElement Badged()
    {
        var shell = Shell.Shells.Panel();
        foreach (var (key, count) in new[] { ("overview", 12), ("queue", 3), ("settings", 1) })
            shell.Sections.Single(s => s.Key == key).Page.Waiting = count;
        return new Bosun.Panel.Shell.ShellView { DataContext = shell };
    }

    /// <summary>The shell painted for a backdrop, as <see cref="Bosun.Panel.Shell.Backdrop.Paint"/> paints it in the window.</summary>
    private static FrameworkElement Backdropped(Bosun.Panel.Shell.BackdropKind kind)
    {
        var shell = new Bosun.Panel.Shell.ShellView { DataContext = Shell.Shells.Panel() };
        Bosun.Panel.Shell.Backdrop.Paint(null, shell, kind);
        return shell;
    }

    /// <summary>A page showing the data it read 12 minutes ago, before WSL stopped, by a clock fixed for the picture.</summary>
    private static FrameworkElement Stale()
    {
        var clock = new Helpers.ManualClock();
        var page = new Shell.TestPage("Instances", "Every dev instance on both sides.") { Clock = clock, Body = "Ubuntu: 3 instances, last read 09:48" };
        page.LastKnown("WSL is stopped", clock.GetUtcNow() - TimeSpan.FromMinutes(12));
        return Framed(page);
    }

    /// <summary>Two values that can't be read: one with its reason, and a null, which says only that.</summary>
    private static FrameworkElement NotAvailableValues()
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(26, 22, 26, 12) };
        foreach (var value in new object?[] { new Bosun.Panel.Shell.NotAvailable("WSL is stopped, so its memory can't be read."), null })
        {
            var shown = new System.Windows.Controls.ContentControl { Content = value, Margin = new Thickness(0, 0, 0, 16) };
            shown.SetResourceReference(FrameworkElement.StyleProperty, "Value");
            panel.Children.Add(shown);
        }
        return panel;
    }

    /// <summary>A control with the focus ring drawn round it, and a drop-down's list as it opens: one item highlighted, one selected.</summary>
    private static FrameworkElement States()
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(26, 22, 26, 12) };
        var focused = new System.Windows.Controls.Control { Width = 160, Height = 32, Margin = new Thickness(4, 4, 4, 24), HorizontalAlignment = HorizontalAlignment.Left };
        focused.SetResourceReference(FrameworkElement.StyleProperty, "FocusRing");
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Focus ring", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(focused);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "A drop-down's list", Margin = new Thickness(0, 0, 0, 6) });
        var list = new System.Windows.Controls.StackPanel { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        var drop = new System.Windows.Controls.Border { Child = list, BorderThickness = new Thickness(1), Padding = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        drop.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "B.Raised");
        drop.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "B.Line");
        drop.SetResourceReference(System.Windows.Controls.Border.CornerRadiusProperty, "R.Control");
        list.Children.Add(new DropItem("Follow Windows"));
        list.Children.Add(new DropItem("Highlighted", highlighted: true));
        list.Children.Add(new DropItem("Selected") { IsSelected = true });
        panel.Children.Add(drop);
        return panel;
    }

    /// <summary>A drop-down's item drawn outside a drop-down, so a picture can show the pointer's highlight on it.</summary>
    private sealed class DropItem : System.Windows.Controls.ComboBoxItem
    {
        public DropItem(string words, bool highlighted = false)
        {
            Content = words;
            IsHighlighted = highlighted;
            SetResourceReference(StyleProperty, typeof(System.Windows.Controls.ComboBoxItem));
        }
    }

    /// <summary>The Settings page while Windows draws in a contrast theme: the line under the theme box says why the choice waits.</summary>
    private static FrameworkElement SettingsInContrast()
    {
        var fixture = new Bosun.Panel.Tests.Settings.SettingsFixture().Open();
        var theme = new Bosun.Panel.Theme.ThemeService(new List<ResourceDictionary>(), new Bosun.Panel.Tests.Settings.FakeWindowsTheme(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher, _ => new ResourceDictionary(), new Helpers.ContrastOn());
        theme.Apply(Bosun.Core.ThemeChoice.Dark);
        var page = new Bosun.Panel.Settings.SettingsPage(fixture.Store, fixture.Startup, fixture.Log, () => { }, theme);
        return new Bosun.Panel.Shell.PageFrame { DataContext = page, Margin = new Thickness(26, 22, 26, 12) };
    }

    /// <summary>The Settings page in the frame, over settings that are only memory, arranged before and after it opens.</summary>
    private static FrameworkElement Settings(Action<Bosun.Panel.Tests.Settings.SettingsFixture> before, Action<Bosun.Panel.Tests.Settings.SettingsFixture>? after = null)
    {
        var fixture = new Bosun.Panel.Tests.Settings.SettingsFixture();
        before(fixture);
        fixture.Open();
        after?.Invoke(fixture);
        return new Bosun.Panel.Shell.PageFrame { DataContext = fixture.Page, Margin = new Thickness(26, 22, 26, 12) };
    }

    /// <summary>The shell with a page that has unsaved changes, asked about as it is being left.</summary>
    private static FrameworkElement LeaveQuestion()
    {
        var (shell, first, _, _) = Shell.Shells.WithTestPages();
        first.Edits = "slots 3";
        var view = new Bosun.Panel.Shell.ShellView { DataContext = shell };
        Shell.TestPage.AddTemplate(view);
        _ = shell.LeaveAsync(); // the question is up; nobody answers it in a picture
        return view;
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^[a-z0-9-]+\.[a-z0-9-]+\.(dark|light|highcontrast)\.png(\.tmp)?$")]
    private static partial Regex OwnedFile();

    private const string Index = "index.json";

    /// <summary>
    /// Whether a file in the renders folder is this catalogue's own: a render (or one half written) or the index.
    /// Anything else there, another picture included, is somebody else's: never removed, and never counted.
    /// </summary>
    public static bool Owns(string fileName) => fileName is Index or Index + ".tmp" || OwnedFile().IsMatch(fileName);

    /// <summary>What is wrong with a catalogue before anything is drawn: one line per problem, none when it is sound.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<RenderCase> cases)
    {
        var problems = new List<string>();
        foreach (var view in cases)
        {
            // The names become file names, and the dots between them are how a file is read back.
            if (!Name().IsMatch(view.View)) problems.Add($"view name '{view.View}' must be lower-case letters, digits and hyphens");
            if (!Name().IsMatch(view.State)) problems.Add($"state name '{view.State}' of {view.View} must be lower-case letters, digits and hyphens");
        }
        foreach (var twice in cases.GroupBy(view => view.Name).Where(group => group.Count() > 1)) problems.Add($"{twice.Key} is registered {twice.Count()} times");
        return problems;
    }

    /// <summary>Where the renders go: <c>build/renders/</c> in the checkout, unless <c>BOSUN_RENDER_DIR</c> names another folder.</summary>
    public static string Folder() =>
        Environment.GetEnvironmentVariable("BOSUN_RENDER_DIR") is { Length: > 0 } named ? Path.GetFullPath(named) : Path.Combine(TestPaths.Repo(), "build", "renders");

    /// <summary>The renders and index an earlier run left in <paramref name="folder"/>; nothing else in it is touched.</summary>
    public static void Clear(string folder)
    {
        if (!Directory.Exists(folder)) return;
        // The index goes first: if a picture can't be removed, what is left must not read as a whole run.
        File.Delete(Path.Combine(folder, Index));
        File.Delete(Path.Combine(folder, Index + ".tmp"));
        foreach (var file in Directory.GetFiles(folder))
            if (Owns(Path.GetFileName(file))) File.Delete(file);
    }

    /// <summary>
    /// Draws every case in both themes into <paramref name="folder"/> as <c>view.state.theme.png</c>, after clearing
    /// what an earlier run left. The index is written last and only when every picture was drawn and written, so a
    /// folder with an index is one whole, current run. Any failure throws, naming every view that failed.
    /// </summary>
    public static IReadOnlyList<string> WriteAll(IReadOnlyList<RenderCase> cases, string folder)
    {
        Directory.CreateDirectory(folder);
        Clear(folder);
        if (Problems(cases) is { Count: > 0 } problems) throw new RenderException("the catalogue can't be drawn: " + string.Join("; ", problems));

        var failures = new List<string>();
        var written = new List<object>();
        var names = new List<string>();
        foreach (var theme in PageRenderer.Themes)
        {
            var pictures = new Dictionary<string, (RenderCase View, uint[] Pixels)>();
            foreach (var view in cases)
            {
                var name = $"{view.Name}.{theme.ToLowerInvariant()}.png";
                try
                {
                    var drawn = PageRenderer.Render(view, theme);
                    var (width, height, pixels) = PageRenderer.Decode(drawn.Png);
                    // Two states of one view that draw the same picture are one state: the factory ignored its state.
                    var same = pictures.Values.FirstOrDefault(other => other.View.View == view.View && other.Pixels.AsSpan().SequenceEqual(pixels));
                    if (same.View is not null) throw new RenderException($"{view.Name}: draws the same picture as {same.View.Name} in {theme}, so the two states are not two states");
                    pictures[view.Name] = (view, pixels);
                    Replace(Path.Combine(folder, name), drawn.Png);
                    names.Add(name);
                    written.Add(new { file = name, view = view.View, state = view.State, theme = theme.ToLowerInvariant(), width, height });
                }
                catch (Exception e) when (e is RenderException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(e is RenderException ? e.Message : $"{view.Name}: {name} can't be written: {e.Message}");
                }
            }
        }
        if (failures.Count > 0) throw new RenderException($"{failures.Count} of {cases.Count * PageRenderer.Themes.Count} renders failed, so no index was written: " + string.Join("; ", failures));

        var index = new { commit = Commit(), written_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), renders = written };
        Replace(Path.Combine(folder, Index), JsonSerializer.SerializeToUtf8Bytes(index, new JsonSerializerOptions { WriteIndented = true }));
        return names;
    }

    /// <summary>Writes beside the destination, then moves into place: a reader never sees half a file.</summary>
    private static void Replace(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary); // gone already when the move worked
        }
    }

    /// <summary>The commit the checkout is at, for the index; "unknown" when git can't say.</summary>
    private static string Commit()
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = TestPaths.Repo(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("rev-parse");
            start.ArgumentList.Add("HEAD");
            using var git = Process.Start(start)!;
            var said = git.StandardOutput.ReadToEnd().Trim();
            if (!git.WaitForExit(10_000))
            {
                git.Kill(entireProcessTree: true);
                return "unknown";
            }
            return git.ExitCode == 0 && said.Length == 40 ? said : "unknown";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown"; // no git on this machine: the pictures are still good, their commit is not recorded
        }
    }
}

/// <summary>What the sample view shows: a title, rows, or the words that stand where the rows would be.</summary>
public sealed record SampleData(string Title, IReadOnlyList<string> Rows, string? Empty);

/// <summary>
/// The catalogue's first view, the tests' own: a panel in the theme's colours whose every text is bound, so a render
/// of it proves the theme, the bindings and the state all reached the picture.
/// </summary>
public sealed class SampleView : UserControl
{
    /// <summary>Where the panel's own colour can be read: inside its padding, where no text is.</summary>
    public const int PanelX = 60, PanelY = 60;

    public SampleView(SampleData data)
    {
        DataContext = data;

        var title = new TextBlock { FontSize = 24, Margin = new Thickness(0, 0, 0, 16) };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(SampleData.Title)));
        title.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");

        var row = new FrameworkElementFactory(typeof(TextBlock));
        row.SetBinding(TextBlock.TextProperty, new Binding());
        row.SetResourceReference(TextBlock.ForegroundProperty, "B.TextDim");
        row.SetValue(MarginProperty, new Thickness(0, 4, 0, 4));
        var rows = new ItemsControl { ItemTemplate = new DataTemplate { VisualTree = row } };
        rows.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(SampleData.Rows)));

        var empty = new TextBlock { FontSize = 16 };
        empty.SetBinding(TextBlock.TextProperty, new Binding(nameof(SampleData.Empty)));
        empty.SetResourceReference(TextBlock.ForegroundProperty, "B.TextMute");

        var stack = new StackPanel { Margin = new Thickness(40, 60, 40, 40) };
        stack.Children.Add(title);
        stack.Children.Add(rows);
        stack.Children.Add(empty);

        var panel = new Border { Margin = new Thickness(40), CornerRadius = new CornerRadius(8), Child = stack };
        panel.SetResourceReference(Border.BackgroundProperty, "B.Surface");
        Content = panel;
    }
}

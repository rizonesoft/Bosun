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
        new("page", "loading", () => Framed(new Shell.TestPage().Loading())) { Shows = "Loading" },
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

    [GeneratedRegex(@"^[a-z0-9-]+\.[a-z0-9-]+\.(dark|light)\.png(\.tmp)?$")]
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

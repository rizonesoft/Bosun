using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Xunit;

namespace Bosun.Panel.Tests;

/// <summary>
/// The generated theme (D02 T01 §1) loads in WPF, and holds every token of design/tokens/colors.json, with its type and
/// value, and nothing else: read back from the token file itself, so a key the generator dropped or got wrong fails.
/// </summary>
public class ThemeTests
{
    static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("the work on the STA thread failed", failure);
        return result;
    }

    // The checkout: the first folder up holding both the solution and the token file, since a copy of Bosun.slnx can sit
    // under build/ (one did, from a measurement run).
    static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Bosun.slnx")) && File.Exists(Path.Combine(dir.FullName, "design", "tokens", "colors.json")))
                return dir.FullName;
        throw new InvalidOperationException("no checkout (Bosun.slnx beside design/tokens/colors.json) above " + AppContext.BaseDirectory);
    }

    static Color Hex(string value)
    {
        var digits = value.TrimStart('#');
        uint n = uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return digits.Length == 6
            ? Color.FromArgb(0xFF, (byte)(n >> 16), (byte)(n >> 8), (byte)n)
            : Color.FromArgb((byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n);  // #AARRGGBB, alpha first
    }

    [Theory]
    [InlineData("dark", "Dark.xaml")]
    [InlineData("light", "Light.xaml")]
    public void Theme_holds_every_token_with_its_type_and_value_and_nothing_else(string theme, string file)
    {
        var repo = Repo();
        using var tokens = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "design", "tokens", "colors.json")));
        var path = Path.Combine(repo, "src", "Bosun.Panel", "Theme", "Generated", file);
        var failures = OnSta(() =>
        {
            ResourceDictionary dict;
            using (var stream = File.OpenRead(path)) dict = (ResourceDictionary)XamlReader.Load(stream);
            var problems = new List<string>();
            var expected = new HashSet<string>();
            void Want(string key, Func<object?, bool> ok, string what)
            {
                expected.Add(key);
                if (!dict.Contains(key)) problems.Add($"{key} is missing");
                else if (!ok(dict[key])) problems.Add($"{key} is {dict[key]}, not {what}");
            }
            foreach (var colour in tokens.RootElement.GetProperty(theme).EnumerateObject())
            {
                var want = Hex(colour.Value.GetString()!);
                Want("C." + colour.Name, v => v is Color c && c == want, want.ToString());
                Want("B." + colour.Name, v => v is SolidColorBrush b && b.Color == want && b.IsFrozen, $"a frozen brush of {want}");
            }
            foreach (var (group, prefix) in new[] { ("radius", "R."), ("space", "S."), ("type", "T.") })
                foreach (var token in tokens.RootElement.GetProperty(group).EnumerateObject())
                {
                    var want = token.Value.GetDouble();
                    if (group == "radius") Want(prefix + token.Name, v => v is CornerRadius r && r == new CornerRadius(want), $"CornerRadius {want}");
                    else Want(prefix + token.Name, v => v is double d && d == want, $"the double {want}");
                }
            foreach (var key in dict.Keys.Cast<object>().Select(k => k.ToString()!))
                if (!expected.Contains(key)) problems.Add($"{key} is not a token");
            return problems;
        });
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public void The_two_themes_hold_the_same_keys()
    {
        var keys = OnSta(() =>
            new[] { "Dark.xaml", "Light.xaml" }.Select(file =>
            {
                using var stream = File.OpenRead(Path.Combine(Repo(), "src", "Bosun.Panel", "Theme", "Generated", file));
                var dict = (ResourceDictionary)XamlReader.Load(stream);
                return dict.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            }).ToArray());
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEmpty(keys[0]);
    }
}

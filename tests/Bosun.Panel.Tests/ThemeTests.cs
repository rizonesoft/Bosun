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
        var repo = TestPaths.Repo();
        using var tokens = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "design", "tokens", "colors.json")));
        var path = TestPaths.GeneratedTheme(file);
        var failures = Sta.Run(() =>
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
            foreach (var (group, prefix) in new[] { ("radius", "R."), ("size", "Size."), ("space", "S."), ("type", "T.") })
                foreach (var token in tokens.RootElement.GetProperty(group).EnumerateObject())
                {
                    var want = token.Value.GetDouble();
                    if (group == "radius") Want(prefix + token.Name, v => v is CornerRadius r && r == new CornerRadius(want), $"CornerRadius {want}");
                    else Want(prefix + token.Name, v => v is double d && d == want, $"the double {want}");
                }
            // D02 T04 §2: each space token as a gap below and above, a Thickness of that size and nothing else.
            foreach (var token in tokens.RootElement.GetProperty("space").EnumerateObject())
            {
                var size = token.Value.GetDouble();
                Want("Gap.Below." + token.Name, v => v is Thickness t && t == new Thickness(0, 0, 0, size), $"Thickness 0,0,0,{size}");
                Want("Gap.Above." + token.Name, v => v is Thickness t && t == new Thickness(0, size, 0, 0), $"Thickness 0,{size},0,0");
                Want("Inset." + token.Name, v => v is Thickness t && t == new Thickness(size), $"Thickness {size}");
                Want("Gap.Before." + token.Name, v => v is Thickness t && t == new Thickness(size, 0, 0, 0), $"Thickness {size},0,0,0");
                Want("Gap.After." + token.Name, v => v is Thickness t && t == new Thickness(0, 0, size, 0), $"Thickness 0,0,{size},0");
                Want("Bleed.Before." + token.Name, v => v is Thickness t && t == new Thickness(-size, 0, 0, 0), $"Thickness -{size},0,0,0");
            }
            {
                var space = tokens.RootElement.GetProperty("space");
                var (xs, m) = (space.GetProperty("XS").GetDouble(), space.GetProperty("M").GetDouble());
                Want("Cell.Pad", v => v is Thickness t && t == new Thickness(xs, 0, m, 0), $"Thickness {xs},0,{m},0");
            }
            // D02 T03 §1: each elevation a frozen shadow straight down, black, at this theme's opacity, and the motion.
            var opacity = theme == "dark" ? "OpacityDark" : "OpacityLight";
            foreach (var elevation in tokens.RootElement.GetProperty("elevation").EnumerateObject())
            {
                double blur = elevation.Value.GetProperty("Blur").GetDouble(), depth = elevation.Value.GetProperty("Depth").GetDouble();
                double strength = elevation.Value.GetProperty(opacity).GetDouble();
                Want("Elevation." + elevation.Name, v => v is System.Windows.Media.Effects.DropShadowEffect e && e.IsFrozen && e.BlurRadius == blur
                    && e.ShadowDepth == depth && e.Opacity == strength && e.Direction == 270 && e.Color == Colors.Black,
                    $"a frozen black shadow, blur {blur}, depth {depth}, direction 270, opacity {strength}");
            }
            foreach (var motion in tokens.RootElement.GetProperty("motion").EnumerateObject())
            {
                if (motion.Name == "Ease")
                {
                    var p = motion.Value.EnumerateArray().Select(n => n.GetDouble()).ToArray();
                    Want("Motion.Ease", v => v is System.Windows.Media.Animation.KeySpline k && k.IsFrozen
                        && k.ControlPoint1 == new Point(p[0], p[1]) && k.ControlPoint2 == new Point(p[2], p[3]), $"a frozen spline {string.Join(",", p)}");
                    continue;
                }
                var ms = motion.Value.GetInt32();
                Want("Motion." + motion.Name[..^2], v => v is Duration d && d.HasTimeSpan && d.TimeSpan == TimeSpan.FromMilliseconds(ms), $"a duration of {ms} ms");
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
        var keys = Sta.Run(() =>
            new[] { "Dark.xaml", "Light.xaml" }.Select(file =>
            {
                using var stream = File.OpenRead(TestPaths.GeneratedTheme(file));
                var dict = (ResourceDictionary)XamlReader.Load(stream);
                return dict.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            }).ToArray());
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEmpty(keys[0]);
    }
}

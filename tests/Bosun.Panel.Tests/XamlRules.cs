using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Bosun.Panel.Tests;

/// <summary>One colour or font size written where the theme should have been used.</summary>
sealed record Violation(string File, int Line, string What)
{
    public override string ToString() => $"{File}:{Line}: {What}";
}

/// <summary>
/// The rule that only the generated theme holds colours and font sizes (D02 T01 §2, after the no-colour rule of
/// Steward's TabStripStyleTests): a colour or size typed into a view or its code-behind is one the next token change
/// misses. Views and code refer to the theme's resources (B.*, C.*, T.*) instead.
/// </summary>
static partial class XamlRules
{
    /// <summary>Properties that take a brush or a colour, by their last name ("TextElement.Foreground" is "Foreground").</summary>
    static readonly HashSet<string> ColourProperties = new(StringComparer.Ordinal)
    {
        "Foreground", "Background", "BorderBrush", "Fill", "Stroke", "Color", "CaretBrush", "SelectionBrush", "OpacityMask",
    };

    /// <summary>Attributes whose value is words for a person or a name, where "#123" is not a colour.</summary>
    static readonly HashSet<string> WordAttributes = new(StringComparer.Ordinal) { "Text", "Content", "Header", "ToolTip", "Title", "Key", "Name", "Uid" };

    [GeneratedRegex(@"^#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColour();

    [GeneratedRegex("\"#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\"")]
    private static partial Regex HexColourString();

    [GeneratedRegex(@"\bColor\s*\.\s*From(?:Argb|Rgb|ScRgb|Values)\b|\b(?:ColorConverter|BrushConverter)\b")]
    private static partial Regex ColourMade();

    [GeneratedRegex(@"\b(Colors|Brushes)\s*\.\s*(\w+)")]
    private static partial Regex NamedColour();

    // FontSize = 13, an initialiser's FontSize = 13.0, SetValue(X.FontSizeProperty, 13), new Setter(X.FontSizeProperty, 13.0)
    [GeneratedRegex(@"\bFontSize\s*=\s*[0-9.]|\bFontSizeProperty\s*,\s*[0-9.]")]
    private static partial Regex FontSizeNumber();

    /// <summary>Every violation in one file's text; the file's extension says how it is read.</summary>
    public static List<Violation> Violations(string file, string text) =>
        file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ? Xaml(file, text) : CSharp(file, text);

    /// <summary>
    /// Every violation in the .xaml and .cs files under a folder, at any depth, leaving out Theme/Generated (the one
    /// place colours live) and build output; and the files read, so a caller can see the scan saw what it should.
    /// </summary>
    public static (List<Violation> Violations, List<string> Read) Scan(string root)
    {
        var violations = new List<Violation>();
        var read = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && !relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var folders = relative.Split('/')[..^1];
            if (folders.Contains("bin") || folders.Contains("obj")) continue;
            if (("/" + relative).Contains("/Theme/Generated/", StringComparison.Ordinal)) continue;
            read.Add(relative);
            violations.AddRange(Violations(relative, File.ReadAllText(path)));
        }
        return (violations, read);
    }

    static bool IsNumber(string value) => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>A literal, not a markup extension such as {StaticResource B.Text} or {x:Null}.</summary>
    static bool IsLiteral(string value) => value.Trim() is { Length: > 0 } v && v[0] != '{';

    static string Last(string name) => name[(name.LastIndexOf('.') + 1)..];

    static List<Violation> Xaml(string file, string text)
    {
        var found = new List<Violation>();
        XDocument doc;
        try { doc = XDocument.Parse(text, LoadOptions.SetLineInfo); }
        catch (XmlException e) { return [new Violation(file, e.LineNumber, "not well-formed XML, so it can't be checked: " + e.Message)]; }
        int Line(XObject o) => ((IXmlLineInfo)o).LineNumber;
        void Value(XObject at, string property, string value, string where)
        {
            var v = value.Trim();
            if (Last(property) == "FontSize")
            {
                if (IsNumber(v)) found.Add(new Violation(file, Line(at), $"font size {v} written {where}: use a T.* resource"));
                return;
            }
            if (!ColourProperties.Contains(Last(property)) || !IsLiteral(v) || v == "Transparent") return;
            if (!HexColour().IsMatch(v)) found.Add(new Violation(file, Line(at), $"colour {v} written {where}: use a B.* or C.* resource"));
            // A hex colour is reported once, by the check below.
        }
        foreach (var element in doc.Descendants())
        {
            var name = element.Name.LocalName;
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration) continue;
                var attr = attribute.Name.LocalName;
                var v = attribute.Value.Trim();
                if (HexColour().IsMatch(v) && !WordAttributes.Contains(attr))
                    found.Add(new Violation(file, Line(attribute), $"colour {v} written in {attr}: use a B.* or C.* resource"));
                Value(attribute, attr, v, $"in {attr}");
            }
            if (name == "Setter" && (string?)element.Attribute("Property") is { } property && (string?)element.Attribute("Value") is { } setter)
                Value(element, property, setter, $"in a Setter for {property}");
            // The element's own words, not its children's: <Color>#fff</Color>, <TextBlock.FontSize>13</TextBlock.FontSize>.
            var own = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (own.Length == 0) continue;
            // Only where a value goes: a property element (<Setter.Value>, <Border.Background>) or a colour object. The
            // words of a <TextBlock> or a <Run> are for a person, and "#123" there is not a colour.
            bool takesValue = (name.Contains('.') && !WordAttributes.Contains(Last(name))) || name is "Color" or "SolidColorBrush";
            if (!takesValue) continue;
            if (HexColour().IsMatch(own))
                found.Add(new Violation(file, Line(element), $"colour {own} written in <{name}>: use a B.* or C.* resource"));
            // <Setter.Value> holds the value of its setter's property, so it is judged as that property (round 1).
            var judgedAs = name == "Setter.Value" && (string?)element.Parent?.Attribute("Property") is { } of ? of : name;
            Value(element, judgedAs, own, judgedAs == name ? $"in <{name}>" : $"in a Setter for {judgedAs}");
        }
        return found;
    }

    static List<Violation> CSharp(string file, string text)
    {
        var found = new List<Violation>();
        // Comments go line by line, then the code is matched as one text: a statement broken over lines
        // ("label.FontSize =\n13;", "Brushes.\nRed") is the same statement (round 1). Lines keep their numbers.
        var code = string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(WithoutComment));
        string Flat(Match m) => Regex.Replace(m.Value, @"\s+", " ").Trim();
        void Add(Match m, string what) => found.Add(new Violation(file, code.AsSpan(0, m.Index).Count('\n') + 1, what));
        foreach (Match m in HexColourString().Matches(code)) Add(m, $"colour {m.Value} written in a string: use a B.* or C.* resource");
        foreach (Match m in ColourMade().Matches(code)) Add(m, $"colour made in code ({Flat(m)}): use a B.* or C.* resource");
        foreach (Match m in NamedColour().Matches(code))
            if (m.Groups[2].Value != "Transparent") Add(m, $"colour {Regex.Replace(m.Value, @"\s+", "")} written in code: use a B.* or C.* resource");
        foreach (Match m in FontSizeNumber().Matches(code)) Add(m, $"font size set to a number ({Flat(m)}...): use a T.* resource");
        return found.OrderBy(v => v.Line).ToList();
    }

    /// <summary>The line up to a // comment that isn't inside a string, so prose about colours is not code.</summary>
    static string WithoutComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && quoted) { i++; continue; }
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i];
        }
        return line;
    }
}

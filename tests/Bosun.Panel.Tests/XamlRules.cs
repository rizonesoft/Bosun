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

    /// <summary>
    /// Properties whose value the theme owns as well (D02 T03 §1): a corner's rounding (R.*), a shadow's blur
    /// (Elevation.*) and an animation's length (Theme/Motion.cs, from Motion.*). Zero passes: no rounding and no motion
    /// are not values a token owns (a window's square corners on its WindowChrome).
    /// </summary>
    static readonly Dictionary<string, string> ThemedMeasures = new(StringComparer.Ordinal)
    {
        ["CornerRadius"] = "an R.* resource",
        ["BlurRadius"] = "an Elevation.* resource",
        ["Duration"] = "Theme/Motion.cs",
    };

    /// <summary>Whether a value holds a number other than zero: "8", "0:0:0.2", "4,4,0,0"; not "0", "0,0,0,0" or "Automatic".</summary>
    static bool HasNonZeroNumber(string value) =>
        Number().Matches(value).Any(m => double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n != 0);

    [GeneratedRegex(@"\d+(?:\.\d+)?|\.\d+")]
    private static partial Regex Number();

    // The same in code, each with its value as the group v, which is what is judged (so a zero passes): BlurRadius = 8;
    // CornerRadius = new(12), target-typed, or new CornerRadius(8) anywhere; a length given to an animation, written
    // as new Duration(TimeSpan.From...(220)), as Duration = TimeSpan.From...(220) (TimeSpan converts to Duration), or as
    // a TimeSpan in an animation's constructor (completion review); and through the property itself, as the FontSize
    // rule reads it (round 2): SetValue(X.BlurRadiusProperty, 20.0), new Setter(Border.CornerRadiusProperty, 8d).
    // A TimeSpan anywhere else is not an animation's (a refresh interval is not motion), so it is not read.
    [GeneratedRegex(@"\bBlurRadius\s*=\s*(?<v>[0-9.]+)"
        + @"|\bCornerRadius\s*=\s*new\s*(?:CornerRadius\s*)?\(\s*(?<v>[0-9.][^)]*)\)"
        + @"|\bnew\s+CornerRadius\s*\(\s*(?<v>[0-9.][^)]*)\)"
        + @"|\bDuration\s*=\s*(?:new\s+Duration\s*\(\s*)?TimeSpan\s*\.\s*From\w+\s*\(\s*(?<v>[0-9.]+)"
        + @"|\bnew\s+Duration\s*\(\s*TimeSpan\s*\.\s*From\w+\s*\(\s*(?<v>[0-9.]+)"
        + @"|\bnew\s+\w*Animation\w*\s*\([^;]*?TimeSpan\s*\.\s*From\w+\s*\(\s*(?<v>[0-9.]+)"
        + @"|\b(?:BlurRadius|CornerRadius)Property\s*,\s*(?<v>[0-9.]+)")]
    private static partial Regex ThemedMeasureInCode();

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

    /// <summary>A size as WPF reads one: a number, with or without a unit (13, 12.5, 12pt, 16px, 1in, 0.5cm).</summary>
    static bool IsSize(string value) => SizeLiteral().IsMatch(value.Trim());

    [GeneratedRegex(@"^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?\s*(?:px|pt|in|cm)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeLiteral();

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
            if (ThemedMeasures.TryGetValue(Last(property), out var owner))
            {
                if (IsLiteral(v) && HasNonZeroNumber(v)) found.Add(new Violation(file, Line(at), $"{Last(property)} {v} written {where}: use {owner}"));
                return;
            }
            if (Last(property) == "FontSize")
            {
                if (IsSize(v)) found.Add(new Violation(file, Line(at), $"font size {v} written {where}: use a T.* resource"));
                return;
            }
            if (!ColourProperties.Contains(Last(property)) || !IsLiteral(v) || v == "Transparent") return;
            if (!HexColour().IsMatch(v)) found.Add(new Violation(file, Line(at), $"colour {v} written {where}: use a B.* or C.* resource"));
            // A hex colour is reported once, by the check below.
        }
        foreach (var element in doc.Descendants())
        {
            var name = element.Name.LocalName;
            // A glyph names its kind only through the one icon map, Theme/Glyphs.cs (D02 T03 §3): a kind written in a view
            // is a glyph the map can't keep consistent or check is in the pack.
            if (name.StartsWith("PackIcon", StringComparison.Ordinal) && (string?)element.Attribute("Kind") is { } kind && IsLiteral(kind))
                found.Add(new Violation(file, Line(element), $"Lucide kind {kind.Trim()} written in a view: name it through Theme/Glyphs.cs"));
            // A status pill keeps its word (D02 T03 §4, design/SPEC.md §19): one written without it never reaches a page.
            if (name == "StatusPill" && element.Attribute("Word") is not { } said
                && !element.Elements().Any(e => e.Name.LocalName == "StatusPill.Word"))
                found.Add(new Violation(file, Line(element), "a StatusPill with no Word: status is never colour alone, so give it its word"));
            else if (name == "StatusPill" && element.Attribute("Word") is { } word && string.IsNullOrWhiteSpace(word.Value))
                found.Add(new Violation(file, Line(element), "a StatusPill with an empty Word: status is never colour alone, so give it its word"));
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
        // Comments go first, then the code is matched as one text: a statement broken over lines
        // ("label.FontSize =\n13;", "Brushes.\nRed") is the same statement (round 1). Lines keep their numbers.
        var code = WithoutComments(text.Replace("\r\n", "\n"));
        string Flat(Match m) => Regex.Replace(m.Value, @"\s+", " ").Trim();
        void Add(Match m, string what) => found.Add(new Violation(file, code.AsSpan(0, m.Index).Count('\n') + 1, what));
        foreach (Match m in HexColourString().Matches(code)) Add(m, $"colour {m.Value} written in a string: use a B.* or C.* resource");
        // The icon map is the one place a Lucide kind is named in code (D02 T03 §3).
        if (!file.EndsWith("Theme/Glyphs.cs", StringComparison.Ordinal))
            foreach (Match m in Regex.Matches(code, @"PackIconLucideKind\s*\.\s*\w+"))
                Add(m, $"Lucide kind {Flat(m)} named in code: name it through Theme/Glyphs.cs");
        // The reader of Windows' own .theme files (D02 T03 §11) makes the colours those files hold: Windows' colours, as
        // SystemColors gives them, never one the panel chooses. It is the one file that may.
        if (!file.EndsWith("Theme/ContrastPreview.cs", StringComparison.Ordinal))
            foreach (Match m in ColourMade().Matches(code)) Add(m, $"colour made in code ({Flat(m)}): use a B.* or C.* resource");
        foreach (Match m in NamedColour().Matches(code))
            if (m.Groups[2].Value != "Transparent") Add(m, $"colour {Regex.Replace(m.Value, @"\s+", "")} written in code: use a B.* or C.* resource");
        foreach (Match m in FontSizeNumber().Matches(code)) Add(m, $"font size set to a number ({Flat(m)}...): use a T.* resource");
        foreach (Match m in ThemedMeasureInCode().Matches(code))
            if (HasNonZeroNumber(m.Groups["v"].Value)) Add(m, $"{Flat(m)} written in code: use an R.* or Elevation.* resource, or Theme/Motion.cs");
        return found.OrderBy(v => v.Line).ToList();
    }

    /// <summary>
    /// The code with every comment blanked, line and block alike, and every newline kept, so lines keep their numbers:
    /// "FontSize = /* caption */ 13" is still a font size set to a number, and code inside a comment is not code
    /// (completion review). Strings are left as they are: "//" or "/*" inside one starts no comment.
    /// </summary>
    static string WithoutComments(string text)
    {
        var code = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') { code.Append(' '); i++; }
            }
            else if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int stop = end < 0 ? text.Length : end + 2;
                for (; i < stop; i++) code.Append(text[i] == '\n' ? '\n' : ' ');
            }
            else if (c == '"')
            {
                // A verbatim string (@"..." or $@"...") ends at a quote that isn't doubled; any other at one not escaped.
                bool verbatim = i > 0 && (text[i - 1] == '@' || (text[i - 1] == '$' && i > 1 && text[i - 2] == '@'));
                code.Append(c);
                i++;
                while (i < text.Length)
                {
                    char s = text[i];
                    code.Append(s);
                    i++;
                    if (verbatim && s == '"' && i < text.Length && text[i] == '"') { code.Append('"'); i++; continue; }
                    if (!verbatim && s == '\\' && i < text.Length) { code.Append(text[i]); i++; continue; }
                    if (s == '"' || (!verbatim && s == '\n')) break;
                }
            }
            else if (c == '\'')
            {
                // A character literal, so '"' opens no string.
                code.Append(c);
                i++;
                while (i < text.Length)
                {
                    char s = text[i];
                    code.Append(s);
                    i++;
                    if (s == '\\' && i < text.Length) { code.Append(text[i]); i++; continue; }
                    if (s == '\'' || s == '\n') break;
                }
            }
            else
            {
                code.Append(c);
                i++;
            }
        }
        return code.ToString();
    }
}

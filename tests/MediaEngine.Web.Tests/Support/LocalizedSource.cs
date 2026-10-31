using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Reads Razor source for tests that check what a screen says. Wording lives in <c>SharedStrings.resx</c> and the Razor
/// source holds only <c>@L["Key"]</c>, so this puts the English master text back where each key is used.
/// </summary>
internal static class LocalizedSource
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(LoadEnglish);
    private static readonly Regex Use = new(@"@?\bL\[""(?<key>[A-Za-z0-9_]+)""(?:\s*,(?<args>(?:[^\[\]]|\[[^\]]*\])*))?\](?:\.Value)?", RegexOptions.Compiled);
    private static readonly Regex Simple = new(@"^[A-Za-z_][\w]*(?:[!?]?\.[A-Za-z_]\w*(?:\([^()]*\))?)*$", RegexOptions.Compiled);

    public static string ReadAllText(string path)
    {
        var text = File.ReadAllText(path);
        return path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? Localize(text) : text;
    }

    public static string Localize(string source) =>
        Use.Replace(source, m =>
        {
            if (!English.Value.TryGetValue(m.Groups["key"].Value, out var value)) return m.Value;
            if (!m.Groups["args"].Success) return value;
            // Put each argument back as the Razor expression it was, so a test can look for "@Name: Wikidata" as before.
            var args = SplitArguments(m.Groups["args"].Value);
            for (var i = 0; i < args.Count; i++)
            {
                var expression = args[i].Trim();
                if (expression.EndsWith(" ?? string.Empty", StringComparison.Ordinal)) expression = expression[..^" ?? string.Empty".Length];
                value = value.Replace("{" + i + "}", Simple.IsMatch(expression) ? "@" + expression : "@(" + expression + ")");
            }
            return value;
        });

    private static List<string> SplitArguments(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') inString = !inString;
            else if (inString) continue;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) { parts.Add(text[start..i]); start = i + 1; }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MediaEngine.Web", "Resources", "SharedStrings.resx")))
        {
            dir = dir.Parent;
        }
        if (dir is null) throw new InvalidOperationException("Could not find the repository root from the test output folder.");
        var doc = XDocument.Load(Path.Combine(dir.FullName, "src", "MediaEngine.Web", "Resources", "SharedStrings.resx"));
        return doc.Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty);
    }
}

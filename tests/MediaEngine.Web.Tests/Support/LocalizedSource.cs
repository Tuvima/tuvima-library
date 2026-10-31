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
    private static readonly Regex Use = new(@"@?\bL\[""(?<key>[A-Za-z0-9_]+)""(?:\s*,[^\]]*)?\](?:\.Value)?", RegexOptions.Compiled);

    public static string ReadAllText(string path)
    {
        var text = File.ReadAllText(path);
        return path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ? Localize(text) : text;
    }

    public static string Localize(string source) =>
        Use.Replace(source, m => English.Value.TryGetValue(m.Groups["key"].Value, out var value) ? value : m.Value);

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

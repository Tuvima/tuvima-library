using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Every word a person reads in the Dashboard comes from the string table (<c>SharedStrings.resx</c>) so the whole app can follow
/// the household's language and regional spelling. These tests keep that true: typed-in wording can only go down, every key a screen
/// asks for exists, and every key in the table is used by a screen.
/// </summary>
public sealed partial class TypedWordingGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WebRoot = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
    private static readonly string BaselinePath = Path.Combine(RepoRoot, "tests", "MediaEngine.Web.Tests", "TypedWordingBaseline.txt");

    [GeneratedRegex(@"\bL\[""(?<key>[A-Za-z0-9_]+)""")] private static partial Regex KeyUse();

    private static IEnumerable<string> RazorFiles() =>
        Directory.EnumerateFiles(WebRoot, "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => !TypedWordingScanner.IsExempt(path))
            .Order(StringComparer.Ordinal);

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    private static Dictionary<string, int> ReadBaseline() =>
        File.ReadAllLines(BaselinePath)
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('|'))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1]), StringComparer.Ordinal);

    [Fact]
    public void TypedInWording_OnlyGoesDown()
    {
        var baseline = ReadBaseline();
        var current = new SortedDictionary<string, List<(int Line, string Text)>>(StringComparer.Ordinal);
        foreach (var path in RazorFiles())
        {
            var found = TypedWordingScanner.Find(File.ReadAllText(path));
            if (found.Count > 0) current[Relative(path)] = found.ToList();
        }

        var worse = current
            .Where(file => file.Value.Count > baseline.GetValueOrDefault(file.Key))
            .Select(file => $"{file.Key} has {file.Value.Count} typed-in words (allowed {baseline.GetValueOrDefault(file.Key)}): "
                + string.Join(" | ", file.Value.Take(4).Select(w => $"line {w.Line} \"{Trim(w.Text)}\"")))
            .ToList();
        Assert.True(worse.Count == 0,
            "Move this wording into SharedStrings.resx and show it with @L[\"Area_Name\"] (keep whole sentences, use {0} for names and numbers):\n"
            + string.Join('\n', worse));

        var better = baseline
            .Where(entry => current.GetValueOrDefault(entry.Key)?.Count is not int count || count < entry.Value)
            .Select(entry => $"{entry.Key}|{current.GetValueOrDefault(entry.Key)?.Count ?? 0}")
            .ToList();
        Assert.True(better.Count == 0,
            "Good news: these files now have fewer typed-in words. Lower their numbers in tests/MediaEngine.Web.Tests/TypedWordingBaseline.txt "
            + "(remove the line when it reaches 0) so the count cannot creep back up:\n" + string.Join('\n', better));
    }

    [Fact]
    public void EveryKeyAScreenAsksFor_ExistsInTheStringTable()
    {
        var keys = MasterKeys();
        var missing = new List<string>();
        foreach (var path in RazorFiles().Concat(Directory.EnumerateFiles(WebRoot, "*.cs", SearchOption.AllDirectories).Where(IsSource)))
        {
            foreach (Match use in KeyUse().Matches(File.ReadAllText(path)))
            {
                if (!keys.Contains(use.Groups["key"].Value)) missing.Add($"{Relative(path)}: {use.Groups["key"].Value}");
            }
        }

        Assert.True(missing.Count == 0, "These keys are not in SharedStrings.resx:\n" + string.Join('\n', missing.Distinct()));
    }

    [Fact]
    public void EveryKeyInTheStringTable_IsUsedBySomeScreen()
    {
        var text = string.Concat(Directory.EnumerateFiles(WebRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => IsSource(path) && (path.EndsWith(".razor") || path.EndsWith(".cs") || path.EndsWith(".js")))
            .Where(path => !path.EndsWith("SharedStrings.cs"))
            .Select(File.ReadAllText));

        var unused = MasterKeys().Where(key => !text.Contains($"\"{key}\"", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        Assert.True(unused.Count == 0, "Remove these unused keys from every SharedStrings*.resx file:\n" + string.Join('\n', unused));
    }

    [Fact]
    public void EveryPlaceholderInATranslation_MatchesTheEnglishOne()
    {
        var english = ResourceValues("SharedStrings.resx");
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(WebRoot, "Resources"), "SharedStrings.*.resx"))
        {
            foreach (var (key, value) in ResourceValues(Path.GetFileName(file)))
            {
                if (english.TryGetValue(key, out var master) && Placeholders(master) != Placeholders(value))
                {
                    problems.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.True(problems.Count == 0, "A translation must keep the same {0}, {1} placeholders as the English:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void EveryUsSpellingChangedByARegionalRule_HasARegionalOverride()
    {
        using var rules = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(WebRoot, "Resources", "regional-spellings.json")));
        var english = ResourceValues("SharedStrings.resx");
        var problems = new List<string>();
        foreach (var (file, rule) in new[] { ("en-GB", "uk"), ("en-AU", "uk"), ("en-CA", "canada") })
        {
            var overrides = ResourceValues($"SharedStrings.{file}.resx");
            var patterns = rules.RootElement.GetProperty(rule).EnumerateArray()
                .Select(item => new Regex(item[0].GetString()!)).ToList();
            foreach (var (key, value) in english)
            {
                if (patterns.Any(pattern => pattern.IsMatch(value)) && !overrides.ContainsKey(key))
                {
                    problems.Add($"{file}: {key} (\"{Trim(value)}\")");
                }
            }
        }

        Assert.True(problems.Count == 0,
            "These words are spelled differently in another English region. Add the regional spelling to the SharedStrings.<region>.resx file "
            + "(rules: Resources/regional-spellings.json):\n" + string.Join('\n', problems));
    }

    private static string Placeholders(string value) =>
        string.Join(',', Regex.Matches(value, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Distinct().Order());

    private static bool IsSource(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    private static string Trim(string text) => text.Length <= 60 ? text : text[..57] + "...";

    private static HashSet<string> MasterKeys() => ResourceValues("SharedStrings.resx").Keys.ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> ResourceValues(string fileName) =>
        XDocument.Load(Path.Combine(WebRoot, "Resources", fileName)).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}

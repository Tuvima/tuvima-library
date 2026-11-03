using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Plugins and Integrations screens v5 D46: every plugin page is drawn by one generator from the page model the Engine builds from the
/// plugin's own JSON. Nothing in the Dashboard may know one plugin from another, so a new plugin never needs Dashboard work. These tests
/// fail when Dashboard plugin code compares a plugin id or name with a text literal, names a shipped plugin, or when a Razor component
/// exists for one plugin. Fix the code, not the test.
/// </summary>
public sealed partial class PluginPageGeneratorGuardrailTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string WebRoot = Path.Combine(Root, "src", "MediaEngine.Web");

    // Plugins that ship with the app, read from the files that describe them, plus the ones whose pages the old Dashboard hand-wrote.
    private static readonly string[] ExtraPluginWords = ["stashdb", "stash_box", "stashbox", "trakt", "listenbrainz", "tmdb"];

    [GeneratedRegex(@"\b(?:[Pp]lugin\w*|[Pp]rovider\w*|[Ss]ource\w*)\s*\??\.\s*(?:Id|Name|PluginId)\s*(?:==|!=|\bis\b|\bis not\b)\s*""")]
    private static partial Regex CompareToLiteral();

    [GeneratedRegex(@"Equals\(\s*[\w.?]*(?:Id|Name)\s*,\s*""|Equals\(\s*""[^""]*""\s*,\s*[\w.?]*(?:Id|Name)\b")]
    private static partial Regex EqualsWithLiteral();

    [GeneratedRegex(@"\b(?:[Pp]luginId|pluginName|PluginName)\s*(?:==|!=|\bis\b)\s*""")]
    private static partial Regex PluginIdVariableCompare();

    [Fact]
    public void DashboardPluginCode_NeverComparesAPluginIdOrNameToALiteral()
    {
        var offenders = new List<string>();
        foreach (var path in PluginCode())
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (CompareToLiteral().IsMatch(line) || EqualsWithLiteral().IsMatch(line) || PluginIdVariableCompare().IsMatch(line))
                {
                    offenders.Add($"{Relative(path)}:{index + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "The Dashboard draws every plugin page from the Engine's page model. Do not branch on one plugin's id or name; "
            + "describe the difference in the plugin's JSON instead:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void DashboardPluginCode_NeverNamesAShippedPlugin()
    {
        var words = PluginWords();
        var offenders = new List<string>();
        foreach (var path in PluginCode())
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) || line.TrimStart().StartsWith("@*", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match literal in QuotedText().Matches(line))
                {
                    var text = literal.Groups[1].Value;
                    if (words.Any(word => string.Equals(text, word, StringComparison.OrdinalIgnoreCase)))
                    {
                        offenders.Add($"{Relative(path)}:{index + 1}: {line.Trim()}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "A text literal in Dashboard plugin code is the id of a shipped plugin:\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("@if (Plugin.Id == \"tmdb\")")]
    [InlineData("if (plugin.Name != \"Trakt\")")]
    [InlineData("string.Equals(plugin.Id, \"stash_box\", StringComparison.Ordinal)")]
    [InlineData("if (pluginId == \"x\")")]
    public void TheDetector_CatchesAComparisonWithALiteral(string line) =>
        Assert.True(CompareToLiteral().IsMatch(line) || EqualsWithLiteral().IsMatch(line) || PluginIdVariableCompare().IsMatch(line));

    [Fact]
    public void NoRazorComponentExistsForOnePlugin()
    {
        var words = PluginWords();
        var offenders = Directory.EnumerateFiles(Path.Combine(WebRoot, "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => Relative(path).Contains("/Plugins/", StringComparison.Ordinal) || Path.GetFileName(path).Contains("Plugin", StringComparison.Ordinal)
                || Path.GetFileName(path).Contains("Provider", StringComparison.Ordinal) || Path.GetFileName(path).Contains("Integration", StringComparison.Ordinal))
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path).Replace("_", string.Empty, StringComparison.Ordinal);
                return words.Any(word => name.Contains(word.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
            })
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A Razor component exists for one plugin. Plugin pages are drawn by PluginSetupPage from the Engine's page model:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void TheGenerator_IsTheOnlyPlaceThatDrawsAPluginsCards()
    {
        var folder = Path.Combine(WebRoot, "Components", "Settings", "Plugins");
        foreach (var required in new[] { "PluginSetupPage.razor", "PluginSettingsCard.razor", "PluginSettingControl.razor", "PluginGroupList.razor", "PluginActivityTimeline.razor" })
        {
            Assert.True(File.Exists(Path.Combine(folder, required)), $"{required} is part of the one generator and must exist.");
        }

        foreach (var retired in new[] { "ProviderDetail.razor", "StashBoxSettingsCard.razor", "PluginAppDetails.razor" })
        {
            Assert.False(File.Exists(Path.Combine(folder, retired)), $"{retired} was replaced by the generator and must not come back.");
        }

        var tab = File.ReadAllText(Path.Combine(WebRoot, "Components", "Settings", "PluginSettingsTab.razor"));
        Assert.Contains("<PluginSetupPage", tab, StringComparison.Ordinal);
        Assert.Contains("<PluginActivityTimeline", tab, StringComparison.Ordinal);
    }

    private static IEnumerable<string> PluginCode()
    {
        var folder = Path.Combine(WebRoot, "Components", "Settings", "Plugins");
        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal));
        var tab = Path.Combine(WebRoot, "Components", "Settings", "PluginSettingsTab.razor");
        var client = Directory.EnumerateFiles(Path.Combine(WebRoot, "Services", "Integration"), "*PluginSetup*.cs");
        return files.Append(tab).Concat(client).Where(File.Exists).Order(StringComparer.Ordinal);
    }

    private static HashSet<string> PluginWords()
    {
        var words = new HashSet<string>(ExtraPluginWords, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "config", "providers"), "*.json"))
        {
            words.Add(Path.GetFileNameWithoutExtension(file));
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 3 } text)
                {
                    words.Add(text);
                }
            }
            catch (JsonException)
            {
                // A provider file that is not valid JSON is another test's problem; its file name still counts above.
            }
        }

        // Words too common to mean one plugin.
        words.RemoveWhere(word => word.Length < 4 || word.Equals("local_filesystem", StringComparison.OrdinalIgnoreCase));
        return words;
    }

    [GeneratedRegex("\"([^\"\\\\]*)\"")]
    private static partial Regex QuotedText();

    private static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

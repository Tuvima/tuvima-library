using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Blazor adds a component's isolation attribute (<c>b-xxxx</c>) only to the HTML elements in that component's own markup. A class handed to a child component
/// (<c>&lt;AppNativeButton Class="stv__preset"&gt;</c>) lands on an element the child renders, so a rule such as <c>.stv__preset { }</c> in the parent's
/// <c>.razor.css</c> compiles to <c>.stv__preset[b-parent]</c> and never matches. The fix is <c>.own-root ::deep .stv__preset</c> (or a wrapper element of the
/// component's own). This guardrail finds the rules that can never match.
/// </summary>
public sealed class ScopedCssReachGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly Regex Comments = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex Functional = new(@":(not|is|where|has)\((?:[^()]|\([^()]*\))*\)");
    private static readonly Regex Attributes = new(@"\[[^\]]*\]");
    private static readonly Regex ClassName = new(@"\.(-?[A-Za-z_][\w-]*)");
    private static readonly Regex Rule = new(@"([^{}]+)\{");
    private static readonly Regex TagName = new(@"^<([A-Za-z][\w.:]*)");
    private static readonly Regex AttributeName = new(@"([A-Za-z][\w:.-]*)\s*=\s*""");

    [Fact]
    public void ScopedStyles_DoNotTargetClassesThatOnlyChildComponentsRender()
    {
        var offenders = Offenders();
        using var baseline = ReadBaseline();
        var known = baseline.RootElement.GetProperty("files").EnumerateObject()
            .SelectMany(file => file.Value.EnumerateArray().Select(name => $"{file.Name}: {name.GetString()}"))
            .ToHashSet(StringComparer.Ordinal);

        var added = offenders.Where(offender => !known.Contains(offender)).ToList();
        Assert.True(added.Count == 0,
            "These classes are only passed to child components (Class=...), so the isolation attribute never reaches them. Write the rule as '.own-root ::deep .class' or wrap the child in an element of this component:"
            + Environment.NewLine + string.Join(Environment.NewLine, added));
    }

    [Fact]
    public void ScopedCssReachBaseline_ListsOnlyRulesThatAreStillUnreachable()
    {
        var offenders = Offenders().ToHashSet(StringComparer.Ordinal);
        using var baseline = ReadBaseline();
        var stale = baseline.RootElement.GetProperty("files").EnumerateObject()
            .SelectMany(file => file.Value.EnumerateArray().Select(name => $"{file.Name}: {name.GetString()}"))
            .Where(entry => !offenders.Contains(entry))
            .ToList();
        Assert.True(stale.Count == 0, "Remove these fixed entries from scoped-css-reach-baseline.json:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Fact]
    public void ScopedCssReachBaseline_HoldsNoUnifiedPlayerFiles()
    {
        using var baseline = ReadBaseline();
        var player = baseline.RootElement.GetProperty("files").EnumerateObject()
            .Select(file => file.Name)
            .Where(name => Regex.IsMatch(Path.GetFileName(name), @"^(Playback|SleepTimer|Queue|Chapters|Episodes|Subtitle|AudioSubtitles)", RegexOptions.None))
            .ToList();
        Assert.Empty(player);
    }

    private static List<string> Offenders()
    {
        var offenders = new List<string>();
        var web = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
        foreach (var cssPath in Directory.EnumerateFiles(web, "*.razor.css", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Relative(cssPath);
            if (relative.Split('/').Any(segment => segment is "bin" or "obj" or "vendor"))
            {
                continue;
            }

            var razorPath = cssPath[..^4];
            if (!File.Exists(razorPath))
            {
                continue;
            }

            var razor = File.ReadAllText(razorPath);
            var css = File.ReadAllText(cssPath);
            foreach (var name in UnscopedClasses(css).OrderBy(name => name, StringComparer.Ordinal))
            {
                var reach = Reach(razor, name);
                // A class used on an HTML element of the component's own markup AND handed to a child component is reached on the first only; it is fine once the
                // rules are also written after '::deep' (the 'plain rule, .root ::deep plain rule' pair).
                if (reach.OnComponent && (!reach.OnHtml || !HasDeepRule(css, name)))
                {
                    offenders.Add($"{Relative(razorPath)}: {name}");
                }
            }
        }
        return offenders;
    }

    // Class names in the part of each selector before '::deep' (or the whole selector without one): the elements the isolation attribute is required on.
    private static HashSet<string> UnscopedClasses(string css)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match rule in Rule.Matches(Comments.Replace(css, "")))
        {
            var head = rule.Groups[1].Value.Trim();
            if (head.StartsWith('@') || head.Length == 0)
            {
                continue;
            }

            foreach (var selector in SplitTopLevel(head))
            {
                var deep = selector.IndexOf("::deep", StringComparison.Ordinal);
                var scoped = deep < 0 ? selector : selector[..deep];
                scoped = Attributes.Replace(Functional.Replace(scoped, ""), "");
                foreach (Match name in ClassName.Matches(scoped))
                {
                    names.Add(name.Groups[1].Value);
                }
            }
        }
        return names;
    }

    private static bool HasDeepRule(string css, string name) =>
        Regex.IsMatch(Comments.Replace(css, ""), @"::deep[^{},]*\." + Regex.Escape(name) + @"(?![\w-])");

    private static IEnumerable<string> SplitTopLevel(string list)
    {
        var depth = 0;
        var start = 0;
        for (var index = 0; index < list.Length; index++)
        {
            switch (list[index])
            {
                case '(' or '[': depth++; break;
                case ')' or ']': depth--; break;
                case ',' when depth == 0:
                    yield return list[start..index];
                    start = index + 1;
                    break;
            }
        }
        yield return list[start..];
    }

    // Where the class is used in the markup. OnComponent: handed to a child component's Class-like attribute (<AppNativeButton Class="...">, TriggerClass="..."); that
    // element belongs to the child, so the parent's isolation attribute never reaches it and a rule written before '::deep' cannot match it. OnHtml: also (or only)
    // on an HTML element of the component's own markup. Both together mean the rule reaches the HTML element only: the plain <div> row works, the <AppNativeButton>
    // row beside it silently does not, as '.psp__row:disabled' once did.
    private static (bool OnComponent, bool OnHtml) Reach(string razor, string name)
    {
        var onComponent = false;
        var onHtml = false;
        foreach (Match use in Regex.Matches(razor, @"(?<![\w-])" + Regex.Escape(name) + @"(?![\w-])"))
        {
            var open = razor.LastIndexOf('<', use.Index);
            var close = razor.LastIndexOf('>', use.Index);
            if (open <= close)
            {
                onHtml = true;
                continue;
            }

            var tag = TagName.Match(razor[open..]);
            if (!tag.Success || !char.IsUpper(tag.Groups[1].Value[0]))
            {
                onHtml = true;
                continue;
            }

            var attribute = AttributeName.Matches(razor[open..use.Index]).LastOrDefault();
            if (attribute is not null && attribute.Groups[1].Value.EndsWith("Class", StringComparison.Ordinal))
            {
                onComponent = true;
            }
            else
            {
                onHtml = true;
            }
        }
        return (onComponent, onHtml);
    }

    private static JsonDocument ReadBaseline() => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "tests", "MediaEngine.Web.Tests", "Fixtures", "scoped-css-reach-baseline.json")));
    private static string Relative(string path) => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
}

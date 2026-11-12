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
    private static readonly Regex CodeBlock = new(@"(?m)^@code\b");
    private static readonly Regex MemberClass = new(@"\b[A-Za-z]*Class\s*=\s*""@\(?([A-Za-z_]\w*)\)?""");

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

    // A browser drops a whole rule when one selector in its list is unknown to it, so '::-moz-range-track' beside a plain selector removes the rule in Chrome and
    // Safari, and '::-webkit-slider-thumb' beside one removes it in Firefox (issue 941: the player slider's height rule never applied outside Firefox).
    [Fact]
    public void SelectorLists_DoNotMixOneBrowsersPseudoElementsWithOtherSelectors()
    {
        var offenders = new List<string>();
        var web = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
        foreach (var cssPath in Directory.EnumerateFiles(web, "*.css", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Relative(cssPath);
            if (relative.Split('/').Any(segment => segment is "bin" or "obj" or "vendor" or "lib") || relative.EndsWith(".min.css", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match rule in Rule.Matches(Comments.Replace(File.ReadAllText(cssPath), "")))
            {
                var head = rule.Groups[1].Value.Trim();
                if (head.StartsWith('@') || head.Length == 0)
                {
                    continue;
                }

                var engines = SplitTopLevel(head)
                    .Select(selector => selector.Contains("::-moz-", StringComparison.Ordinal) ? "moz" : selector.Contains("::-webkit-", StringComparison.Ordinal) ? "webkit" : "any")
                    .Distinct()
                    .ToList();
                if (engines.Count > 1)
                {
                    offenders.Add($"{relative}: {Regex.Replace(head, @"\s+", " ")}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Give each browser's pseudo-elements a rule of their own:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // Issue 941, pinned: the bar's Play button, the clickable cover and the phone sheet are drawn by child parts, so their rules sit after '::deep'.
    [Theory]
    [InlineData("Components/Player/PlaybackPrimaryButton.razor.css", ".playback-primary-button-shell ::deep .playback-bar-play {", ".playback-bar-play")]
    [InlineData("Components/Player/PlaybackPrimaryButton.razor.css", ".playback-primary-button-shell ::deep .playback-phone-play {", ".playback-phone-play")]
    [InlineData("Components/Player/PlaybackIdentity.razor.css", ".playback-identity ::deep .playback-identity__art-image", ".playback-identity__art-image")]
    [InlineData("Components/Player/PlaybackPanel.razor.css", ".playback-panel--sheet ::deep .playback-tool-sheet {", ".playback-tool-sheet")]
    public void PlayerRulesForChildDrawnElements_AreWrittenAfterDeep(string file, string expected, string neverAtLineStart)
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", file)).Replace("\r\n", "\n");

        Assert.Contains(expected, css, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*" + Regex.Escape(neverAtLineStart) + @"(?![\w-])", css);
        Assert.DoesNotMatch(@"(?m)^\s*\.playback-tool-sheet\s*[{:\[]", File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components/Player/PlaybackToolSheet.razor.css")));
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
            // Classes handed to a child component through a member (Class="@ButtonClass"): the name is only in the code block, so the markup scan below
            // cannot see where it lands (issue 941: '.playback-bar-play' was written as a plain scoped rule and never applied).
            var fromCode = ClassesHandedToChildrenFromCode(razor);
            var subjects = UnscopedClasses(css, subjectOnly: true);
            var codeStart = CodeBlock.Match(razor) is { Success: true } block ? block.Index : razor.Length;
            foreach (var name in UnscopedClasses(css).OrderBy(name => name, StringComparer.Ordinal))
            {
                // For those, only a rule whose own target is the class counts ('.root-from-code .own-child' reaches the child, which is the component's own element).
                var reach = fromCode.TryGetValue(name, out var alsoOnHtml)
                    ? (OnComponent: subjects.Contains(name), OnHtml: alsoOnHtml || Reach(razor[..codeStart], name).OnHtml)
                    : Reach(razor, name);
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
    // With subjectOnly, just the names in the last compound of that part: the one element the rule (or its '::deep' anchor) has to match.
    private static HashSet<string> UnscopedClasses(string css, bool subjectOnly = false)
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
                if (subjectOnly)
                {
                    scoped = Regex.Split(scoped.Trim(), @"[\s>+~]+").LastOrDefault() ?? "";
                }

                foreach (Match name in ClassName.Matches(scoped))
                {
                    names.Add(name.Groups[1].Value);
                }
            }
        }
        return names;
    }

    // The class names a component hands to a child through a member of its code block: for each Class-like attribute of a child component whose value is
    // one identifier (Class="@ButtonClass", TriggerClass="@(RowClass)"), the words of every string literal in that member's expression. The value says whether
    // the same member is also the class of an HTML element of the component's own markup (class="@ArtworkClass"), which the plain rule does reach.
    private static Dictionary<string, bool> ClassesHandedToChildrenFromCode(string razor)
    {
        var names = new Dictionary<string, bool>(StringComparer.Ordinal);
        var code = CodeBlock.Match(razor);
        if (!code.Success)
        {
            return names;
        }

        var markup = razor[..code.Index];
        var body = razor[code.Index..];
        foreach (Match use in MemberClass.Matches(markup))
        {
            var open = markup.LastIndexOf('<', use.Index);
            var tag = open < 0 ? Match.Empty : TagName.Match(markup[open..]);
            if (!tag.Success || !char.IsUpper(tag.Groups[1].Value[0]))
            {
                continue;
            }

            var memberName = Regex.Escape(use.Groups[1].Value);
            var onHtml = Regex.IsMatch(markup, @"<[a-z][^<>]*\sclass\s*=\s*""@\(?" + memberName + @"\)?""");
            var member = Regex.Match(body, @"\b" + memberName + @"\s*=>?\s*(?<value>[^;]*);");
            if (!member.Success)
            {
                continue;
            }

            foreach (Match literal in Regex.Matches(member.Groups["value"].Value, "\"([^\"]*)\""))
            {
                foreach (var word in literal.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Regex.IsMatch(word, @"^-?[A-Za-z_][\w-]*$"))
                    {
                        names[word] = onHtml || names.GetValueOrDefault(word);
                    }
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

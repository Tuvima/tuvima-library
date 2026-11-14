using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Unified player v2 acceptance A14 (decision D6 and D8): the player keeps one look and one pair of media elements. Player files are found by rule, not by a
/// hand list, so a new panel (for example the Bookmarks panel) is covered the day it is added: everything under <c>Components/Player</c> and
/// <c>Components/Listen</c>, the shared <c>Components/Shared/Playback*</c> parts (the shell, panels, menus, buttons, glyphs) and the Now playing page.
/// Older violations that existed before this guardrail are listed in <c>Fixtures/player-guardrail-baseline.json</c>: fix them when the file is next edited and
/// delete the entry; nothing new may be added.
/// </summary>
public sealed class PlayerGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string Web = Path.Combine(RepoRoot, "src", "MediaEngine.Web");

    private static readonly Regex Comments = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex HtmlTitle = new(@"(?<![\w:@.-])title\s*=\s*[""'@]");
    private static readonly Regex MaterialIcon = new(@"AppMaterialIcons\.|AppMaterialIcon\b|material-symbols|material-icons", RegexOptions.IgnoreCase);
    private static readonly Regex ColorDeclaration = new(@"(?<![\w-])(background|background-color|color|border-color)\s*:\s*([^;}]*)", RegexOptions.IgnoreCase);
    private static readonly Regex LiteralColor = new(@"#[0-9a-fA-F]{3,8}\b|\b(?:rgba?|hsla?)\([^)]*\)", RegexOptions.IgnoreCase);
    // A --playback-* that only points at another token, var(--tl-x) or var(--playback-y) with an optional token fallback, adds no color of its own (a surface remap).
    private static readonly Regex TokenAlias = new(@"^var\(\s*--[\w-]+\s*(,\s*var\(\s*--[\w-]+\s*\)\s*)?\)$");
    private static readonly Regex PlaybackDeclaration = new(@"(?<![\w-])(--playback-[\w-]+)\s*:\s*([^;}]*)");
    private static readonly HashSet<string> ColorWords = new(StringComparer.Ordinal)
    {
        "surface", "background", "bg", "color", "colour", "text", "border", "accent", "hairline", "divider", "track", "rail", "handle", "marker", "danger", "blue", "glow", "hover", "pressed", "selected",
    };

    // ---- Rule 1: no title= hints ----

    [Fact]
    public void PlayerFiles_NeverUseTheBrowsersTitleHint()
    {
        var offenders = new List<string>();
        foreach (var path in PlayerFiles(".razor"))
        {
            var text = File.ReadAllText(path);
            foreach (Match match in HtmlTitle.Matches(text))
            {
                offenders.Add($"{Relative(path)}:{text[..match.Index].Count(c => c == '\n') + 1}");
            }

            // A native-button wrapper must not take a Title either (it has no such parameter).
            foreach (Match match in Regex.Matches(text, @"<(AppNativeButton|AppIconButton|PlaybackIconButton)\b[^>]*\sTitle\s*="))
            {
                offenders.Add($"{Relative(path)}:{text[..match.Index].Count(c => c == '\n') + 1} <{match.Groups[1].Value}>");
            }
        }

        Assert.True(offenders.Count == 0,
            "The player never uses the browser's title=\"...\" hint (spec D8, A14). Use the Tooltip parameter on an icon-only control, AppTooltip for a disabled reason, or data-overflow-tooltip for cut-off text, or delete a hint that repeats the visible label:" + Lines(offenders));
    }

    // ---- Rule 2: no Material icons ----

    [Fact]
    public void PlayerFiles_UseOnlyTheStrokeGlyphsAndNoMaterialIcons()
    {
        var found = PlayerFiles(".razor", ".razor.css", ".cs")
            .Select(path => (Path: Relative(path), Hits: MaterialIcon.Matches(File.ReadAllText(path)).Select(m => m.Value.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToList()))
            .Where(file => file.Hits.Count > 0)
            .ToDictionary(file => file.Path, file => file.Hits, StringComparer.Ordinal);

        AssertAgainstBaseline("materialIcons", found,
            "The player draws its icons with PlaybackUtilityGlyph stroke glyphs (spec D6, Look v2 A4); add a glyph kind if one is missing and do not use AppMaterialIcon, AppMaterialIcons, material-symbols or material-icons");
    }

    // ---- Rule 3: no player surface colors outside the shared tokens ----

    [Fact]
    public void PlayerStyles_TakeSurfaceColorsFromSharedTokensAndNotFromLiterals()
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in PlayerFiles(".razor.css"))
        {
            var css = Comments.Replace(File.ReadAllText(path), string.Empty);
            var hits = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match declaration in ColorDeclaration.Matches(css))
            {
                foreach (Match color in LiteralColor.Matches(declaration.Groups[2].Value))
                {
                    if (!IsPlainBlackOrWhiteTransparency(color.Value))
                    {
                        hits.Add($"{declaration.Groups[1].Value.ToLowerInvariant()}: {Regex.Replace(color.Value, @"\s+", " ").ToLowerInvariant()}");
                    }
                }
            }

            if (hits.Count > 0)
            {
                found[Relative(path)] = hits.ToList();
            }
        }

        AssertAgainstBaseline("literalColors", found,
            "Player CSS sets background, color and border-color from var(--tl-*) or var(--playback-*) tokens, never a literal hex, rgb or hsl (spec D6, A14). Allowed without a token: transparent, currentColor, inherit, and rgba(0,0,0,x) / rgba(255,255,255,x) scrims, which the player already uses for video shades and hover washes. Use an existing token, or add one to wwwroot/tuvima.tokens.css");
    }

    [Fact]
    public void PlayerStyles_DefineNoSurfaceColorTokensOutsideTheSharedTokenFile()
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in PlayerFiles(".razor.css"))
        {
            var css = Comments.Replace(File.ReadAllText(path), string.Empty);
            var hits = PlaybackDeclaration.Matches(css)
                .Where(m => !TokenAlias.IsMatch(m.Groups[2].Value.Trim()))
                .Where(m => IsColorTokenName(m.Groups[1].Value) || LiteralColor.Matches(m.Groups[2].Value).Any(color => !IsPlainBlackOrWhiteTransparency(color.Value)))
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (hits.Count > 0)
            {
                found[Relative(path)] = hits;
            }
        }

        AssertAgainstBaseline("playbackColorDefinitions", found,
            "A --playback-* surface, text, border or state color value is defined once, in src/MediaEngine.Web/wwwroot/tuvima.tokens.css (spec D6, A14); a player stylesheet uses it with var(...). A player stylesheet may remap one token to another (--playback-x: var(--tl-y)) and may set component-local sizes such as --playback-glyph-size, but never a literal color");
    }

    [Fact]
    public void TheSharedTokenFile_DefinesThePlaybackColorTokensThePlayerReads()
    {
        var tokens = File.ReadAllText(Path.Combine(Web, "wwwroot", "tuvima.tokens.css"));

        foreach (var name in new[] { "--playback-surface:", "--playback-text:", "--playback-state-hover:", "--playback-state-pressed:", "--playback-state-selected:", "--playback-hairline:", "--playback-accent:" })
        {
            Assert.True(tokens.Contains(name, StringComparison.Ordinal), $"tuvima.tokens.css must define {name} for the player; the guardrail relies on it being the one home of player colors.");
        }
    }

    // ---- Rule 4: one audio and one video element per session ----

    // Sites that render a media element but are not the session player. Each has a reason; a new site needs one here or must reuse the session player.
    private static readonly Dictionary<string, string> NonSessionAudioSites = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Web/Components/Details/ExtraViewerContent.razor"] = "plays one extra (a trailer or a bonus clip) in its own viewer with the browser controls; it never joins a playback session",
        ["src/MediaEngine.Web/Components/Shared/MediaViewerShell.razor"] = "the artwork and file preview viewer shows a short clip with browser controls; it never joins a playback session",
    };

    private static readonly Dictionary<string, string> NonSessionVideoSites = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Web/wwwroot/js/playback-capabilities.js"] = "creates one video element that is never put on the page and only asks it canPlayType, to measure what the browser can open (playback-path spec D12); it plays nothing and joins no playback session",
        ["src/MediaEngine.Web/Components/Details/ExtraViewerContent.razor"] = "plays one extra (a trailer or a bonus clip) in its own viewer with the browser controls; it never joins a playback session",
        ["src/MediaEngine.Web/Components/Shared/MediaViewerShell.razor"] = "the artwork and file preview viewer shows a short clip with browser controls; it never joins a playback session",
    };

    [Fact]
    public void TheDashboard_RendersOneSessionAudioElementAndNoOtherAudioSurface()
    {
        var sites = MediaElementSites(@"<audio\b|createElement\(\s*['""`]audio['""`]|new\s+Audio\(");

        var session = sites.Where(site => !NonSessionAudioSites.ContainsKey(site)).ToList();
        Assert.True(session.SequenceEqual(["src/MediaEngine.Web/Components/Player/ListenNowPlayingBar.razor"]),
            "Exactly one place renders the session's <audio>: ListenNowPlayingBar. The Now playing page, the bar and every panel share it (spec D24, A14). Reuse it instead of adding another; a genuinely separate viewer needs a reasoned entry in NonSessionAudioSites. Found:" + Lines(session));
        Assert.True(NonSessionAudioSites.Keys.All(sites.Contains),
            "These non-session audio entries no longer render <audio>; delete them from NonSessionAudioSites:" + Lines(NonSessionAudioSites.Keys.Where(k => !sites.Contains(k))));
    }

    [Fact]
    public void TheDashboard_RendersOneSessionVideoElementAndNoOtherVideoSurface()
    {
        var sites = MediaElementSites(@"<video\b|createElement\(\s*['""`]video['""`]");

        var session = sites.Where(site => !NonSessionVideoSites.ContainsKey(site)).ToList();
        Assert.True(session.SequenceEqual(["src/MediaEngine.Web/Components/Player/VideoPlaybackHost.razor"]),
            "Exactly one place renders the session's <video>: VideoPlaybackHost (spec A14). Reuse it instead of adding another; a genuinely separate viewer needs a reasoned entry in NonSessionVideoSites. Found:" + Lines(session));
        Assert.True(NonSessionVideoSites.Keys.All(sites.Contains),
            "These non-session video entries no longer render <video>; delete them from NonSessionVideoSites:" + Lines(NonSessionVideoSites.Keys.Where(k => !sites.Contains(k))));
    }

    [Fact]
    public void EachSessionMediaElementIsRenderedOnceInItsOwnFile()
    {
        var bar = File.ReadAllText(Path.Combine(Web, "Components", "Player", "ListenNowPlayingBar.razor"));
        var host = File.ReadAllText(Path.Combine(Web, "Components", "Player", "VideoPlaybackHost.razor"));

        Assert.True(Regex.Matches(bar, @"<audio\b").Count == 1 && !Regex.IsMatch(bar, @"<video\b"), "ListenNowPlayingBar renders exactly one <audio> and no <video>; do not add a second media element to the bar.");
        Assert.True(Regex.Matches(host, @"<video\b").Count == 1 && !Regex.IsMatch(host, @"<audio\b"), "VideoPlaybackHost renders exactly one <video> and no <audio>; do not add a second media element to the host.");
    }

    // ---- File sets and helpers ----

    [Fact]
    public void ThePlayerFileSet_IsFoundByRuleAndCoversTheShellThePanelsAndTheNowPlayingPage()
    {
        var files = PlayerFiles(".razor", ".razor.css", ".cs").Select(Relative).ToHashSet(StringComparer.Ordinal);

        Assert.True(files.Count > 100, "The player file rules matched too few files; check the folder rules in PlayerFiles.");
        foreach (var expected in new[]
        {
            "src/MediaEngine.Web/Components/Player/PlaybackShell.razor",
            "src/MediaEngine.Web/Components/Player/PlaybackPanel.razor.css",
            "src/MediaEngine.Web/Components/Player/ListenNowPlayingBar.razor",
            "src/MediaEngine.Web/Components/Player/VideoPlaybackHost.razor",
            "src/MediaEngine.Web/Components/Pages/NowPlayingPage.razor",
        })
        {
            Assert.Contains(expected, files);
        }

        Assert.DoesNotContain(files, f => f.Contains("/obj/", StringComparison.Ordinal) || f.Contains("/bin/", StringComparison.Ordinal));
    }

    // Files that sit in a player folder but are library browsing, not the player. Each has a reason; the player rules do not apply to them.
    private static readonly Dictionary<string, string> NotPlayerFiles = new(StringComparer.Ordinal)
    {
        ["SongTable"] = "the one song table for the Songs, Albums, album and playlist pages (spec music-table-and-reader-frame), not part of the player",
        ["SongList"] = "the Songs and Albums lists: feeds the one song table and keeps its tick boxes, bulk bar and delete flow, not part of the player",
        ["SongSelectionBar"] = "the phone's bottom bar for ticked songs (spec music-player-and-listen-fixes D12): library browsing, not part of the player",
        ["SongsPhoneHeader"] = "the phone Songs page header (search, Play, Shuffle, sort and filter sheet), not part of the player",
        ["ListenNavigationSection"] = "the Listen page's side navigation (Library, Formats, Your library), not part of the player",
    };

    private static List<string> PlayerFiles(params string[] extensions)
    {
        var components = Path.Combine(Web, "Components");
        var files = new List<string>();
        files.AddRange(Directory.EnumerateFiles(Path.Combine(components, "Listen"), "*", SearchOption.AllDirectories));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(components, "Player"), "*", SearchOption.AllDirectories));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(components, "Shared"), "Playback*", SearchOption.TopDirectoryOnly));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(components, "Pages"), "NowPlayingPage.*", SearchOption.TopDirectoryOnly));
        return files
            .Where(path => extensions.Any(extension => path.EndsWith(extension, StringComparison.Ordinal)))
            .Where(path => !IsBuildOutput(path))
            .Where(path => !NotPlayerFiles.ContainsKey(Path.GetFileName(path).Split('.')[0]))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<string> MediaElementSites(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        var sources = Directory.EnumerateFiles(Web, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".html", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal))
            .Where(path => !IsBuildOutput(path))
            // Third-party reader and viewer libraries are vendored, not the app's own player.
            .Where(path => !Relative(path).StartsWith("src/MediaEngine.Web/wwwroot/lib/", StringComparison.Ordinal));
        return sources
            .Where(path => regex.IsMatch(File.ReadAllText(path)))
            .Select(Relative)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertAgainstBaseline(string section, Dictionary<string, List<string>> found, string rule)
    {
        var known = ReadBaseline(section);
        var added = found.SelectMany(file => file.Value.Where(hit => !known.Contains($"{file.Key}: {hit}")).Select(hit => $"{file.Key}: {hit}")).ToList();
        Assert.True(added.Count == 0, rule + ". New violations (nothing new may be added to the baseline):" + Lines(added));

        var current = found.SelectMany(file => file.Value.Select(hit => $"{file.Key}: {hit}")).ToHashSet(StringComparer.Ordinal);
        var stale = known.Where(entry => !current.Contains(entry)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, $"These are fixed or gone; delete them from the \"{section}\" section of Fixtures/player-guardrail-baseline.json:" + Lines(stale));
    }

    private static HashSet<string> ReadBaseline(string section)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "tests", "MediaEngine.Web.Tests", "Fixtures", "player-guardrail-baseline.json")));
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in document.RootElement.GetProperty(section).EnumerateObject())
        {
            Assert.False(string.IsNullOrWhiteSpace(file.Value.GetProperty("reason").GetString()), $"{file.Name} in the \"{section}\" baseline needs a reason.");
            foreach (var hit in file.Value.GetProperty("hits").EnumerateArray())
            {
                entries.Add($"{file.Name}: {hit.GetString()}");
            }
        }

        return entries;
    }

    private static bool IsColorTokenName(string name) =>
        name["--playback-".Length..].Split('-').Any(ColorWords.Contains) || name.EndsWith("-focus-ring", StringComparison.Ordinal);

    // rgba(0,0,0,x) and rgba(255,255,255,x): the video shades, scrims and hover washes the player already uses.
    private static bool IsPlainBlackOrWhiteTransparency(string color)
    {
        var match = Regex.Match(color, @"^rgba?\(([^)]*)\)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        var numbers = Regex.Matches(match.Groups[1].Value, @"-?\d*\.?\d+")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToList();
        if (numbers.Count < 4)
        {
            return false;
        }

        return (numbers[0] == 0 && numbers[1] == 0 && numbers[2] == 0) || (numbers[0] == 255 && numbers[1] == 255 && numbers[2] == 255);
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Lines(IEnumerable<string> items) => Environment.NewLine + string.Join(Environment.NewLine, items);

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');
}

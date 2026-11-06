using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Source-level checks that the Activity page keeps the look of the approved mockup (spec library-activity, issue 690): a left-aligned
/// grid row per History entry, collapsing headings, area-coloured chips, compact controls on the right of the heading, and no inline
/// styles or forced overrides.
/// </summary>
public sealed class LibraryActivityLookCssTests
{
    private const string Folder = "src/MediaEngine.Web/Components/Settings/LibraryActivity/";

    [Fact]
    public void AHistoryEntryRow_IsAGridRead_LeftToRight_AndNeverACentredStack()
    {
        var app = Read("src/MediaEngine.Web/wwwroot/app.css");

        var toggle = Rule(app, ".lact-box__toggle");
        Assert.Contains("display: grid;", toggle, StringComparison.Ordinal);
        Assert.Contains("text-align: left;", toggle, StringComparison.Ordinal);
        Assert.Contains("grid-template-areas: \"what covers count status chev\";", toggle, StringComparison.Ordinal);
        Assert.Contains("align-items: center;", toggle, StringComparison.Ordinal);
        Assert.DoesNotContain("justify-content: center", toggle, StringComparison.Ordinal);
        Assert.DoesNotContain("flex-direction: column", toggle, StringComparison.Ordinal);

        // Narrower windows move the counts under the row, then the covers, rather than squeezing every column.
        Assert.Contains("grid-template-areas: \"what covers status chev\" \"count count count count\";", app, StringComparison.Ordinal);
        Assert.Contains("grid-template-areas: \"what status chev\" \"covers covers covers\" \"count count count\";", app, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEntryParts_PlaceThemselvesInTheNamedGridAreas()
    {
        var css = Read(Folder + "LibraryActivityRunEntry.razor.css");

        foreach (var area in new[] { "what", "covers", "count", "status", "chev" })
        {
            Assert.Contains($"grid-area: {area};", css, StringComparison.Ordinal);
        }

        Assert.Contains("width: 30px; height: 44px;", Rule(css, ".lact-cover"), StringComparison.Ordinal);
        Assert.Contains("width: 44px;", Rule(css, ".lact-cover.is-square"), StringComparison.Ordinal);
        Assert.Contains("margin: var(--tl-space-1) 0 var(--tl-space-2) calc(62px + var(--tl-space-4));", Rule(css, ".lact-open"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEntryAndHeadingButtons_AreNativeButtonsWhoseRootsAreStyledGlobally()
    {
        var entry = Read(Folder + "LibraryActivityRunEntry.razor");
        var history = Read(Folder + "LibraryActivityHistory.razor");

        // The shared AppButton centres its content; the row and the headings need left-aligned layouts of their own.
        Assert.DoesNotContain("<AppButton ButtonStyle=\"AppButtonStyle.Text\"", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppButton ButtonStyle=\"AppButtonStyle.Text\"", history, StringComparison.Ordinal);
        Assert.Contains("Class=\"lact-box__toggle\"", entry, StringComparison.Ordinal);
        Assert.Contains("Class=\"lact-group__toggle\"", history, StringComparison.Ordinal);

        var group = Rule(Read("src/MediaEngine.Web/wwwroot/app.css"), ".lact-group__toggle");
        Assert.Contains("display: flex;", group, StringComparison.Ordinal);
        Assert.Contains("text-align: left;", group, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMonthHeading_IsBoldWithAMutedBatchCountAfterIt()
    {
        var css = Read(Folder + "LibraryActivityHistory.razor.css");
        var razor = Read(Folder + "LibraryActivityHistory.razor");

        Assert.Contains("font-weight: var(--tl-font-weight-bold);", Rule(css, ".lact-group__toggle strong"), StringComparison.Ordinal);
        Assert.Contains("color: var(--tl-text-muted);", Rule(css, ".lact-group__count"), StringComparison.Ordinal);
        Assert.Contains("<span class=\"lact-group__count\">· @LibraryActivityText.BatchCount(month.Count)</span>", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHistoryControls_SitRightOfTheHeading_AtTheMockupsWidths()
    {
        var css = Read(Folder + "LibraryActivityHistory.razor.css");

        var head = Rule(css, ".lact-history__head");
        Assert.Contains("display: flex;", head, StringComparison.Ordinal);
        Assert.Contains("justify-content: space-between;", head, StringComparison.Ordinal);
        Assert.Contains("width: 210px;", Rule(css, ".lact-controls__search"), StringComparison.Ordinal);
        Assert.Contains("width: 130px;", Rule(css, ".lact-controls__select--type"), StringComparison.Ordinal);
        Assert.Contains("width: 150px;", Rule(css, ".lact-controls__select--range"), StringComparison.Ordinal);
        Assert.Contains("width: 140px;", Rule(css, ".lact-controls__select--sort"), StringComparison.Ordinal);
        Assert.Contains("justify-content: flex-end;", Rule(Read("src/MediaEngine.Web/wwwroot/app.css"), ".lact-controls"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAreaChips_TakeTheirOutlineFromTheAreaColour_AndAllLibrariesFromTheAccent()
    {
        var app = Read("src/MediaEngine.Web/wwwroot/app.css");

        Assert.Contains("var(--lib-area-colour, var(--tl-accent-primary))", Rule(app, "button.app-toggle-chip.lact-chip"), StringComparison.Ordinal);
        Assert.Contains("--lib-area-colour: var(--tl-accent-primary);", Rule(app, "button.app-toggle-chip.lact-chip--all"), StringComparison.Ordinal);
        Assert.Contains("color-mix(in srgb, var(--lib-area-colour, var(--tl-accent-primary)) 16%, transparent)", Rule(app, "button.app-toggle-chip.lact-chip.is-on"), StringComparison.Ordinal);
        Assert.Contains("lact-chip--all", Read(Folder + "LibraryActivityPage.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLiveTag_IsGreenWhileLive_AndTheTitleIsLargeAndBold()
    {
        var css = Read(Folder + "LibraryActivityPage.razor.css");

        Assert.Contains("color: var(--tl-status-success);", Rule(css, ".lact-live.is-live"), StringComparison.Ordinal);
        var title = Rule(css, ".lact-title-main");
        Assert.Contains("font-size: var(--tl-font-size-hero);", title, StringComparison.Ordinal);
        Assert.Contains("font-weight: var(--tl-font-weight-bold);", title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheActivityPieces_UseNoInlineStyleAndNoForcedOverrides()
    {
        var root = FindRepoRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, Folder.Replace('/', Path.DirectorySeparatorChar))))
        {
            var text = File.ReadAllText(file);
            if (file.EndsWith(".razor", StringComparison.Ordinal))
            {
                Assert.DoesNotMatch(new Regex(@"\sstyle\s*=", RegexOptions.IgnoreCase), text);
                Assert.DoesNotContain("InlineStyle=", text, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("!important", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheActivityRulesInAppCss_SitBeforeTheTvMarker_WithNoForcedOverrides()
    {
        var app = Read("src/MediaEngine.Web/wwwroot/app.css");
        var activity = app.IndexOf("Activity page (LibraryActivity*", StringComparison.Ordinal);
        var tv = app.IndexOf("/* tv form factor:", StringComparison.Ordinal);

        Assert.True(activity > 0 && tv > activity, "The Activity rules must sit in the shared block, before the TV rules.");
        Assert.DoesNotContain("!important", app[tv..], StringComparison.Ordinal);
        var block = app[activity..tv];
        var end = block.IndexOf("a.tl-link.lact-summary__review", StringComparison.Ordinal);
        Assert.DoesNotContain("!important", block[..(end + 1)], StringComparison.Ordinal);
    }

    [Fact]
    public void ThePicturesAreAlwaysDrawnByTheOnePictureComponent_WithAStandIn()
    {
        var face = Read(Folder + "LibraryActivityTileFace.razor");
        var entry = Read(Folder + "LibraryActivityRunEntry.razor");
        var picture = Read(Folder + "LibraryActivityPicture.razor");

        Assert.Contains("<LibraryActivityPicture", face, StringComparison.Ordinal);
        Assert.Contains("<LibraryActivityPicture", entry, StringComparison.Ordinal);
        Assert.Contains("@onerror=\"OnError\"", picture, StringComparison.Ordinal);
        Assert.Contains("lact-picture--empty", picture, StringComparison.Ordinal);
    }

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, "(?m)^\\s*" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"No rule for {selector}.");
        return match.Groups["body"].Value;
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(
        FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("MediaEngine.slnx not found above the test output folder.");
    }
}

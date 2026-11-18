using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Settings.LibraryActivity;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// An opened batch as a list (review mock-ups of 2026-11-18, D3, #1107, #1108, #1118): a summary row with its pills, media-type chips with counts,
/// and a table of a small cover, Title, Type, Size and Status whose headings sort, with a video collection as one collapsed row.
/// </summary>
public sealed class LibraryActivityEntryListTests : AsyncBunitContext
{
    private static readonly Guid ShowWork = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid FirstLesson = Guid.Parse("22222222-2222-4222-8222-222222222221");
    private static readonly Guid SecondLesson = Guid.Parse("22222222-2222-4222-8222-222222222222");

    public LibraryActivityEntryListTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void TheSummaryRow_ReadsTheBatchDateAndTime_WithPillsForItemsDoneAndWorking()
    {
        var started = new DateTimeOffset(new DateTime(2026, 11, 18, 15, 42, 0, DateTimeKind.Local));

        var cut = RenderList(Mixed(), counts: [new("Movies", 12), new("TV", 34)], itemCount: 46, started: started, working: [ShowWork, FirstLesson, SecondLesson]);

        Assert.Equal("Batch · Nov 18, 3:42 PM", cut.Find("[data-testid='lact-batch-stamp']").TextContent.Trim());
        var pills = cut.FindAll("[data-testid='lact-batch-summary'] .lact-batch__pill").Select(pill => pill.TextContent.Trim()).ToList();
        // The show (3 episodes) and two files of the collection are still being worked on: 5 of 46.
        Assert.Equal(["46 items", "41 done", "5 working"], pills);
    }

    [Fact]
    public void ABatchNothingIsWorkingOn_ShowsNoWorkingPill_AndEveryRowReadsDone()
    {
        var cut = RenderList(Mixed(), counts: [new("Movies", 2)], itemCount: 46, working: null);

        Assert.Empty(cut.FindAll(".lact-batch__pill--working"));
        Assert.Equal("46 done", cut.Find(".lact-batch__pill--done").TextContent.Trim());
        Assert.All(cut.FindAll("[data-testid='lact-batch-row'] .lact-batch__status"), status => Assert.Equal("Done", status.TextContent.Trim()));
    }

    [Fact]
    public void TheChips_ReadAllThenEachMediaTypeWithItsCount_AndPickingOneNarrowsTheRows()
    {
        var cut = RenderList(Mixed(), counts: [new("TV", 34), new("Movies", 12)], itemCount: 46, working: null);

        Assert.Equal(["All 46", "TV 34", "Movies 12"], Chips(cut));
        Assert.Equal(4, cut.FindAll("[data-testid='lact-batch-row']").Count);

        cut.FindAll("[data-testid='lact-batch-chips'] button")[2].Click();

        Assert.Equal(["Dune", "Arrival"], Titles(cut));
        Assert.All(cut.FindAll("[data-testid='lact-batch-row']"), row => Assert.Equal("Movies", row.GetAttribute("data-media-type")));
        Assert.Equal("true", cut.FindAll("[data-testid='lact-batch-chips'] button")[2].GetAttribute("aria-pressed"));

        cut.FindAll("[data-testid='lact-batch-chips'] button")[0].Click();

        Assert.Equal(4, cut.FindAll("[data-testid='lact-batch-row']").Count);
    }

    [Fact]
    public void ClickingAHeading_SortsAscendingWithAnUpArrow_AndAgainDescendingWithADownArrow()
    {
        var cut = RenderList(Mixed(), counts: [new("TV", 3)], itemCount: 6, working: null);
        Assert.Empty(cut.FindAll("[data-testid='lact-sort-arrow']"));
        Assert.All(cut.FindAll("thead th"), heading => Assert.NotEqual("ascending", heading.GetAttribute("aria-sort")));

        cut.Find("[data-testid='lact-sort-title']").Click();

        Assert.Equal(["Arrival", "Dune", "Lessons", "The Expanse"], Titles(cut));
        Assert.Equal("ascending", cut.Find("[data-testid='lact-sort-title']").Closest("th")!.GetAttribute("aria-sort"));
        Assert.Contains("M4 12l1.41 1.41", cut.Find("[data-testid='lact-sort-arrow']").InnerHtml, StringComparison.Ordinal);

        cut.Find("[data-testid='lact-sort-title']").Click();

        Assert.Equal(["The Expanse", "Lessons", "Dune", "Arrival"], Titles(cut));
        Assert.Equal("descending", cut.Find("[data-testid='lact-sort-title']").Closest("th")!.GetAttribute("aria-sort"));
        Assert.Contains("M20 12l-1.41-1.41", cut.Find("[data-testid='lact-sort-arrow']").InnerHtml, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("[data-testid='lact-sort-arrow']"));
    }

    [Fact]
    public void ClickingAnotherHeading_MovesTheArrowThere_AndSortsBySizeThenTypeThenStatus()
    {
        var cut = RenderList(Mixed(), counts: [new("TV", 3)], itemCount: 6, working: [ShowWork]);

        cut.Find("[data-testid='lact-sort-size']").Click();
        Assert.Equal(["The Expanse", "Arrival", "Lessons", "Dune"], Titles(cut));
        Assert.Equal("ascending", cut.Find("[data-testid='lact-sort-size']").Closest("th")!.GetAttribute("aria-sort"));
        Assert.Equal("none", cut.Find("[data-testid='lact-sort-title']").Closest("th")!.GetAttribute("aria-sort"));

        cut.Find("[data-testid='lact-sort-type']").Click();
        Assert.Equal(["Arrival", "Dune", "The Expanse", "Lessons"], Titles(cut));
        Assert.Equal("none", cut.Find("[data-testid='lact-sort-size']").Closest("th")!.GetAttribute("aria-sort"));
        Assert.Single(cut.FindAll("[data-testid='lact-sort-arrow']"));

        // Done rows first, then the show that is still being worked on; pressing again puts it first.
        cut.Find("[data-testid='lact-sort-status']").Click();
        Assert.Equal(["Arrival", "Dune", "Lessons", "The Expanse"], Titles(cut));
        cut.Find("[data-testid='lact-sort-status']").Click();
        Assert.Equal("The Expanse", Titles(cut)[0]);
    }

    [Fact]
    public void AVideoCollection_IsOneRowTaggedVideoCollection_ClosedAtFirst_WithItsOwnWorkingCount()
    {
        var cut = RenderList(Mixed(), counts: [new("Videos", 14)], itemCount: 14, working: [FirstLesson, SecondLesson]);

        var row = cut.FindAll("[data-testid='lact-batch-row']").Single(candidate => candidate.GetAttribute("data-collection") == "true");
        Assert.Equal("Video collection", row.QuerySelector("[data-testid='lact-batch-tag']")!.TextContent.Trim());
        Assert.Equal("2 of 14 working", row.QuerySelector(".lact-batch__status")!.TextContent.Trim());
        Assert.Equal("false", row.QuerySelector("[data-testid='lact-batch-toggle']")!.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[data-testid='lact-batch-file']"));
    }

    [Fact]
    public void TheArrowOnACollection_OpensItsFilesAsMutedRowsUnderIt_AndClosesThemAgain()
    {
        var cut = RenderList(Mixed(), counts: [new("Videos", 14)], itemCount: 14, working: [FirstLesson]);

        cut.Find("[data-testid='lact-batch-toggle']").Click();

        Assert.Equal("true", cut.Find("[data-testid='lact-batch-toggle']").GetAttribute("aria-expanded"));
        var files = cut.FindAll("[data-testid='lact-batch-file']");
        Assert.Equal(14, files.Count);
        Assert.All(files, file => Assert.Contains("is-file", file.ClassName, StringComparison.Ordinal));
        Assert.Equal("Lesson 01", files[0].QuerySelector(".lact-batch__text")!.TextContent.Trim());
        Assert.Equal("Working", files[0].QuerySelector(".lact-batch__status")!.TextContent.Trim());
        Assert.Equal("Done", files[1].QuerySelector(".lact-batch__status")!.TextContent.Trim());
        Assert.Equal("100 MB", files[0].QuerySelector(".lact-batch__size")!.TextContent.Trim());
        // The files sit directly under their collection, before the next row.
        var rows = cut.FindAll("tbody tr").ToList();
        var collection = rows.FindIndex(row => row.GetAttribute("data-collection") == "true");
        Assert.All(rows.Skip(collection + 1).Take(14), row => Assert.Equal("lact-batch-file", row.GetAttribute("data-testid")));
        Assert.Equal(collection + 15, rows.Count);

        cut.Find("[data-testid='lact-batch-toggle']").Click();

        Assert.Empty(cut.FindAll("[data-testid='lact-batch-file']"));
        Assert.Equal("false", cut.Find("[data-testid='lact-batch-toggle']").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void ARow_ReadsASmallCover_ThenTitleTypeSizeAndStatus_WithTheSizeAsPlainUnits()
    {
        var cut = RenderList(Mixed(), counts: [new("Movies", 2)], itemCount: 6, working: null);

        Assert.Equal(["Cover", "Title", "Type", "Size", "Status"], cut.FindAll("thead th").Select(heading => heading.TextContent.Trim()).ToList());
        var dune = cut.FindAll("[data-testid='lact-batch-row']").Single(row => row.QuerySelector(".lact-batch__text, .lact-batch__link")!.TextContent.Trim() == "Dune");
        Assert.Equal("1.4 GB", dune.QuerySelector(".lact-batch__size")!.TextContent.Trim());
        Assert.Equal("—", cut.FindAll("[data-testid='lact-batch-row']").Single(row => row.GetAttribute("data-media-type") == "TV").QuerySelector(".lact-batch__size")!.TextContent.Trim());
        Assert.NotNull(dune.QuerySelector(".lact-batch__cover .lact-batch__art"));
    }

    [Fact]
    public void TheTable_SitsInsideItsOwnScrollingCard_AsTallAsAWorkingNowTile()
    {
        var cut = RenderList(Mixed(), counts: [new("Movies", 2)], itemCount: 6, working: null);
        Assert.NotNull(cut.Find("[data-testid='lact-batch-scroll'] [data-testid='lact-batch-table']"));

        var folder = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Settings", "LibraryActivity");
        var css = File.ReadAllText(Path.Combine(folder, "LibraryActivityEntryList.razor.css"));
        var working = File.ReadAllText(Path.Combine(folder, "LibraryActivityWorkingNow.razor.css"));

        Assert.Contains("overflow-x: auto;", Rule(css, ".lact-batch__scroll"), StringComparison.Ordinal);
        // The row is one and a half times the cover's width, and the cover is the Working now tile's width, on a computer and on a phone.
        Assert.Contains("height: calc(var(--lact-batch-tile-w) * 1.5);", Rule(css, ".lact-batch__row"), StringComparison.Ordinal);
        Assert.Contains("--lact-batch-tile-w: 84px;", css, StringComparison.Ordinal);
        Assert.Contains("--lact-working-tile-w: 84px;", working, StringComparison.Ordinal);
        Assert.Contains("--lact-batch-tile-w: 64px;", css, StringComparison.Ordinal);
        Assert.Contains("--lact-working-tile-w: 64px;", working, StringComparison.Ordinal);
        Assert.Contains("height: calc(var(--lact-working-tile-w) * 1.5);", working, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryWordOnTheList_IsASharedStringsKey()
    {
        var root = FindRepoRoot();
        var razor = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Settings/LibraryActivity/LibraryActivityEntryList.razor"));
        var english = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src/MediaEngine.Web/Resources/SharedStrings.resx"))
            .Descendants("data").Select(data => (string)data.Attribute("name")!).ToHashSet(StringComparer.Ordinal);

        var used = System.Text.RegularExpressions.Regex.Matches(razor, "\"(LibraryActivity_[A-Za-z]+)\"").Select(match => match.Groups[1].Value).Distinct().ToList();

        Assert.NotEmpty(used);
        Assert.All(used, key => Assert.True(english.Contains(key), $"{key} is not in SharedStrings.resx."));
    }

    private IRenderedComponent<LibraryActivityEntryList> RenderList(
        IReadOnlyList<ActivityGroupDto> groups, IReadOnlyList<ActivityRunCountDto> counts, int itemCount, IEnumerable<Guid>? working, DateTimeOffset? started = null)
        => Render<LibraryActivityEntryList>(parameters => parameters
            .Add(list => list.Groups, groups)
            .Add(list => list.Counts, counts)
            .Add(list => list.ItemCount, itemCount)
            .Add(list => list.StartedAt, started ?? DateTimeOffset.Now.AddHours(-1))
            .Add(list => list.WorkingIds, working?.ToHashSet()));

    /// <summary>A show of 3 episodes (no size known), two movies, and a folder of 14 videos.</summary>
    private static IReadOnlyList<ActivityGroupDto> Mixed()
    {
        var files = Enumerable.Range(1, 14)
            .Select(index => new ActivityFileDto($"Lesson {index:00}", 100L * 1024 * 1024, index switch { 1 => FirstLesson, 2 => SecondLesson, _ => Guid.NewGuid() }))
            .ToList();
        return
        [
            new("TV|the expanse", "The Expanse", "TV", 3, "/engine-image/a.jpg", "/details/show/1", 0, ShowWork),
            new("movie:dune", "Dune", "Movies", 1, "/engine-image/b.jpg", "/details/work/2", 1_500_000_000L, Guid.NewGuid()),
            new("movie:arrival", "Arrival", "Movies", 1, null, null, 900_000_000L, Guid.NewGuid()),
            new("videos|/lessons", "Lessons", "Videos", 14, null, null, 14L * 100 * 1024 * 1024, null, files),
        ];
    }

    private static List<string> Chips(IRenderedComponent<LibraryActivityEntryList> cut)
        => cut.FindAll("[data-testid='lact-batch-chips'] button").Select(chip => string.Join(' ', chip.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToList();

    private static List<string> Titles(IRenderedComponent<LibraryActivityEntryList> cut)
        => cut.FindAll("[data-testid='lact-batch-row']").Select(row => row.QuerySelector(".lact-batch__text, .lact-batch__link")!.TextContent.Trim()).ToList();

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector} has no rule.");
        return css[start..css.IndexOf('}', start)];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}

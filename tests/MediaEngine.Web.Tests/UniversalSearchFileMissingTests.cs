using Bunit;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Search;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Global search lists the signed-in profile's restored items that are still waiting for their file as plain "File missing" rows
/// (backup and restore spec D18): a name, where it belongs, its year and a note; no link, no artwork, no play.
/// </summary>
public sealed class UniversalSearchFileMissingTests : AsyncBunitContext
{
    public UniversalSearchFileMissingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
    }

    [Fact]
    public void FileMissingRow_ShowsNameParentYearAndNote_WithNoLink()
    {
        var cut = Render<UniversalSearchResults>(parameters => parameters.Add(p => p.Response, Response(topResult: Movie(), FileMissingSection())));

        var section = cut.Find("section[aria-labelledby='search-section-file-missing']");
        var row = section.QuerySelector("li.universal-results__missing-row")!;
        Assert.Contains("Pilot", row.TextContent);
        Assert.Contains("Severance", row.TextContent);
        Assert.Contains("2022", row.TextContent);
        Assert.Contains("File missing", row.TextContent);
        Assert.Empty(section.QuerySelectorAll("a"));
        Assert.Empty(section.QuerySelectorAll("img"));
        Assert.Empty(section.QuerySelectorAll("button"));
        Assert.Equal("File missing", section.QuerySelector(".app-status-badge")?.TextContent.Trim());
    }

    [Fact]
    public void FileMissingSection_IsDrawnAfterTheOtherSections_AndOffersNoSeeAll()
    {
        var cut = Render<UniversalSearchResults>(parameters => parameters.Add(p => p.Response, Response(topResult: Movie(), WatchSection(), FileMissingSection())));

        var headings = cut.FindAll("section.universal-results__section h2").Select(heading => heading.TextContent.Trim()).ToList();
        Assert.Equal(["Movies & TV", "File missing"], headings);
        var section = cut.Find("section[aria-labelledby='search-section-file-missing']");
        Assert.DoesNotContain("See all", section.TextContent);
    }

    [Fact]
    public void FileMissingSection_IsNotDrawn_WhenThereAreNoRows()
    {
        var cut = Render<UniversalSearchResults>(parameters => parameters.Add(p => p.Response, Response(topResult: Movie(), WatchSection())));

        Assert.Empty(cut.FindAll("section[aria-labelledby='search-section-file-missing']"));
        Assert.DoesNotContain("File missing", cut.Markup);
    }

    [Fact]
    public void ResultsAreDrawn_WhenOnlyFileMissingRowsMatch_WithNoTopResult()
    {
        var cut = Render<UniversalSearchResults>(parameters => parameters.Add(p => p.Response, Response(topResult: null, FileMissingSection())));

        Assert.Empty(cut.FindAll(".universal-results__top"));
        Assert.Single(cut.FindAll("li.universal-results__missing-row"));
    }

    private static UniversalSearchResponseDto Response(UniversalSearchResultDto? topResult, params UniversalSearchSectionDto[] sections) =>
        new("q", topResult, sections, topResult is null ? 0 : 1);

    private static UniversalSearchResultDto Movie() =>
        new(Guid.NewGuid(), "movie", "Movie", "Dune", null, null, "2021", null, null, "/details/work/1?context=watch", "Watch", "Matched title", 1.0);

    private static UniversalSearchSectionDto WatchSection() =>
        new("watch", "Movies & TV", [Movie()], 1, "/watch/movies?q=q");

    private static UniversalSearchSectionDto FileMissingSection() =>
        new(
            "file-missing",
            "File missing",
            [new UniversalSearchResultDto(Guid.NewGuid(), "file-missing", "TV", "Pilot", "Severance", null, "2022", null, null, string.Empty, string.Empty, "File missing", 0)],
            1,
            null);
}

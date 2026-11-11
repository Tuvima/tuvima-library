using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The tab set each detail page type offers (item-detail spec, section 4). No page offers a Universe,
/// Related, Details, Characters, Portrayals or Relationships tab.
/// </summary>
public sealed class DetailTabCatalogTests
{
    public static TheoryData<DetailEntityType, string[]> ExpectedSets => new()
    {
        { DetailEntityType.Movie, ["overview", "cast"] },
        { DetailEntityType.TvShow, ["overview", "cast"] },
        { DetailEntityType.TvSeason, ["overview", "cast"] },
        { DetailEntityType.TvEpisode, ["overview", "cast"] },
        { DetailEntityType.MusicAlbum, ["overview"] },
        { DetailEntityType.Audiobook, ["overview", "tracks"] },
        { DetailEntityType.Book, ["overview", "credits"] },
        { DetailEntityType.Work, ["overview", "credits"] },
        { DetailEntityType.ComicIssue, ["overview", "credits"] },
        { DetailEntityType.MovieSeries, ["overview"] },
        { DetailEntityType.BookSeries, ["overview"] },
        { DetailEntityType.ComicSeries, ["overview"] },
        { DetailEntityType.Collection, ["overview"] },
        { DetailEntityType.Universe, ["overview"] },
        { DetailEntityType.Person, ["overview"] },
    };

    [Theory]
    [MemberData(nameof(ExpectedSets))]
    public void EveryTypeGetsExactlyItsTabs(DetailEntityType entityType, string[] expected)
    {
        var tabs = BuildTabs(entityType, DetailPresentationContext.Default);

        Assert.Equal(expected, tabs.Select(tab => tab.Key));
    }

    [Theory]
    [MemberData(nameof(ExpectedSets))]
    public void AdminAddressFlagDoesNotAddARegistryTab(DetailEntityType entityType, string[] expected)
    {
        var context = DetailComposerService.ParseContext("admin");

        var tabs = BuildTabs(entityType, context);

        Assert.Equal(DetailPresentationContext.Default, context);
        Assert.Equal(expected, tabs.Select(tab => tab.Key));
        Assert.DoesNotContain(tabs, tab => tab.Key == "registry");
    }

    [Fact]
    public void EveryKindOfPageHasItsTabSetListedHere()
    {
        var listed = ExpectedSets.Select(row => (DetailEntityType)row[0]).ToHashSet();

        Assert.Equal(Enum.GetValues<DetailEntityType>().OrderBy(type => type), listed.OrderBy(type => type));
    }

    [Fact]
    public void NoKindOfPageEverListsCharacterTabs()
    {
        string[] removed = ["characters", "portrayals", "relationships", "people"];

        foreach (var entityType in Enum.GetValues<DetailEntityType>())
        {
            var keys = DetailTabCatalog.KeysFor(entityType);

            Assert.Empty(keys.Intersect(removed));
        }
    }

    [Fact]
    public void NoKindOfPageEverListsUniverseOrRelated()
    {
        foreach (var entityType in Enum.GetValues<DetailEntityType>())
        {
            var keys = DetailTabCatalog.KeysFor(entityType);

            Assert.DoesNotContain("universe", keys);
            Assert.DoesNotContain("related", keys);
            Assert.DoesNotContain("details", keys);
        }
    }

    private static List<DetailTab> BuildTabs(DetailEntityType entityType, DetailPresentationContext context)
    {
        var orchestrator = typeof(MediaEngine.Api.Services.Details.DetailComposerService).Assembly.GetType(
            "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator", throwOnError: true)!;
        var method = orchestrator.GetMethod(
            "BuildTabs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<List<DetailTab>>(
            method!.Invoke(null, [entityType, context]));
    }
}

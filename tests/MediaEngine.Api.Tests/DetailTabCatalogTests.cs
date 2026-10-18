using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The tab set each detail page type offers. No page offers a Universe or Related tab.
/// </summary>
public sealed class DetailTabCatalogTests
{
    public static TheoryData<DetailEntityType, string[]> ExpectedSets => new()
    {
        { DetailEntityType.Movie, ["overview", "cast", "details"] },
        { DetailEntityType.TvShow, ["overview", "cast", "details"] },
        { DetailEntityType.TvSeason, ["overview", "cast", "details"] },
        { DetailEntityType.TvEpisode, ["overview", "cast", "characters", "details"] },
        { DetailEntityType.MusicAlbum, ["overview", "details"] },
        { DetailEntityType.Audiobook, ["overview", "tracks", "details"] },
        { DetailEntityType.Book, ["overview", "credits", "details"] },
        { DetailEntityType.Work, ["overview", "credits", "details"] },
        { DetailEntityType.ComicIssue, ["overview", "credits", "details"] },
        { DetailEntityType.MovieSeries, ["overview", "details"] },
        { DetailEntityType.BookSeries, ["overview", "details"] },
        { DetailEntityType.ComicSeries, ["overview", "details"] },
        { DetailEntityType.Collection, ["overview", "details"] },
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
    public void NoKindOfPageEverListsUniverseOrRelated()
    {
        foreach (var entityType in Enum.GetValues<DetailEntityType>())
        {
            var keys = DetailTabCatalog.KeysFor(entityType);

            Assert.DoesNotContain("universe", keys);
            Assert.DoesNotContain("related", keys);
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

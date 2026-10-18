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
        var tabs = BuildTabs(entityType, isAdminView: false);

        Assert.Equal(expected, tabs.Select(tab => tab.Key));
    }

    [Theory]
    [MemberData(nameof(ExpectedSets))]
    public void AdminRegistryTabStaysLast(DetailEntityType entityType, string[] expected)
    {
        var tabs = BuildTabs(entityType, isAdminView: true);

        Assert.Equal([.. expected, "registry"], tabs.Select(tab => tab.Key));
        Assert.True(tabs[^1].IsAdminOnly);
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

    private static List<DetailTab> BuildTabs(DetailEntityType entityType, bool isAdminView)
    {
        var orchestrator = typeof(MediaEngine.Api.Services.Details.DetailComposerService).Assembly.GetType(
            "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator", throwOnError: true)!;
        var method = orchestrator.GetMethod(
            "BuildTabs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<List<DetailTab>>(
            method!.Invoke(null, [entityType, DetailPresentationContext.Default, isAdminView]));
    }
}

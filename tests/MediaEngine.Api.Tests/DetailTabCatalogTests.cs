using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The tab set each detail page type offers, and the single switch that hides (or restores)
/// the Universe and Related tabs on every page.
/// </summary>
public sealed class DetailTabCatalogTests
{
    public static TheoryData<DetailEntityType, string[]> HiddenSets => new()
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

    [Fact]
    public void TheSwitchShipsOff()
    {
        Assert.False(DetailTabVisibility.ShowUniverseAndRelatedTabs);
    }

    [Theory]
    [MemberData(nameof(HiddenSets))]
    public void EveryTypeGetsExactlyItsTabsWithUniverseAndRelatedHidden(DetailEntityType entityType, string[] expected)
    {
        foreach (var hasUniverse in new[] { false, true })
        {
            var tabs = BuildTabs(entityType, isAdminView: false, hasUniverse);

            Assert.Equal(expected, tabs.Select(tab => tab.Key));
        }
    }

    [Theory]
    [MemberData(nameof(HiddenSets))]
    public void AdminRegistryTabIsUnchangedAndStaysLast(DetailEntityType entityType, string[] expected)
    {
        var tabs = BuildTabs(entityType, isAdminView: true, hasUniverse: true);

        Assert.Equal([.. expected, "registry"], tabs.Select(tab => tab.Key));
        Assert.True(tabs[^1].IsAdminOnly);
    }

    [Theory]
    [InlineData(DetailEntityType.Movie, false, new[] { "overview", "cast", "related", "details" })]
    [InlineData(DetailEntityType.Movie, true, new[] { "overview", "cast", "universe", "related", "details" })]
    [InlineData(DetailEntityType.TvShow, true, new[] { "overview", "cast", "universe", "related", "details" })]
    [InlineData(DetailEntityType.TvEpisode, true, new[] { "overview", "cast", "characters", "universe", "related", "details" })]
    [InlineData(DetailEntityType.Book, true, new[] { "overview", "credits", "universe", "related", "details" })]
    [InlineData(DetailEntityType.BookSeries, false, new[] { "overview", "related", "details" })]
    [InlineData(DetailEntityType.ComicSeries, true, new[] { "overview", "universe", "related", "details" })]
    [InlineData(DetailEntityType.Audiobook, true, new[] { "overview", "tracks", "details" })]
    [InlineData(DetailEntityType.MusicAlbum, true, new[] { "overview", "details" })]
    [InlineData(DetailEntityType.Collection, true, new[] { "overview", "details" })]
    public void FlippingTheSwitchRestoresUniverseAndRelated(DetailEntityType entityType, bool hasUniverse, string[] expected)
    {
        Assert.Equal(expected, DetailTabCatalog.KeysFor(entityType, hasUniverse, includeUniverseAndRelated: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothSwitchStatesDifferOnlyByTheTwoSwitchedKeys(bool hasUniverse)
    {
        foreach (var entityType in Enum.GetValues<DetailEntityType>())
        {
            var shown = DetailTabCatalog.KeysFor(entityType, hasUniverse, includeUniverseAndRelated: true);
            var hidden = DetailTabCatalog.KeysFor(entityType, hasUniverse, includeUniverseAndRelated: false);

            Assert.Equal(shown.Where(key => key is not ("universe" or "related")), hidden);
            Assert.DoesNotContain(hidden, key => DetailTabVisibility.IsSwitchedTab(key));
        }
    }

    private static List<DetailTab> BuildTabs(DetailEntityType entityType, bool isAdminView, bool hasUniverse)
    {
        var orchestrator = typeof(MediaEngine.Api.Services.Details.DetailComposerService).Assembly.GetType(
            "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator", throwOnError: true)!;
        var method = orchestrator.GetMethod(
            "BuildTabs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<List<DetailTab>>(
            method!.Invoke(null, [entityType, DetailPresentationContext.Default, isAdminView, false, hasUniverse, true]));
    }
}

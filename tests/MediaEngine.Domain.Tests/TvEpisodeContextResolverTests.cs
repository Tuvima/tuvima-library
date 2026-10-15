using MediaEngine.Domain.Services;
namespace MediaEngine.Domain.Tests;

public sealed class TvEpisodeContextResolverTests
{
    [Fact]
    public void UnstartedAndResetUseSeriesArt_AndDoNotStartWithSpecials()
    {
        var result = TvEpisodeContextResolver.Resolve([new("special", 0, 1, 0, null), new("one", 1, 1, 0, null)]);
        Assert.Equal("one", result.Target!.Id); Assert.False(result.UsesEpisodeArtwork);
    }
    [Fact]
    public void MostRecentResumeWinsOverHighestPercentage()
    {
        var result = TvEpisodeContextResolver.Resolve([new("one", 1, 1, 80, "2026-01-01"), new("two", 1, 2, 10, "2026-01-02")]);
        Assert.Equal("two", result.Target!.Id); Assert.True(result.UsesEpisodeArtwork);
    }
    [Fact]
    public void CompletedEpisodeAdvancesToUnstartedOwnedEpisode_WithGap()
    {
        var result = TvEpisodeContextResolver.Resolve([new("one", 1, 1, 100, "2026-01-01"), new("three", 1, 3, 0, null)]);
        Assert.Equal("three", result.Target!.Id); Assert.True(result.UsesEpisodeArtwork); Assert.True(result.HasGap);
    }
    [Fact]
    public void SeasonBoundaryUsesNextOwnedSeason()
    {
        var result = TvEpisodeContextResolver.Resolve([new("last", 1, 7, 100, "2026-01-01"), new("next", 2, 1, 0, null)]);
        Assert.Equal("next", result.Target!.Id); Assert.True(result.UsesEpisodeArtwork);
    }
    [Fact]
    public void AllOwnedCompletedReturnsSeriesArtAndExplicitRewatchState()
    {
        var result = TvEpisodeContextResolver.Resolve([new("one", 1, 1, 100, "2026-01-01"), new("two", 1, 2, 100, "2026-01-02")]);
        Assert.Equal(TvEpisodeSelectionReason.AllOwnedCompleted, result.Reason); Assert.False(result.UsesEpisodeArtwork);
    }
    [Fact]
    public void OrderPutsSpecialsLast_ThenUnnumberedAfterNumbered_AndBreaksTiesById()
    {
        var ordered = TvEpisodeContextResolver.Order(
        [
            new("b-tie", 1, 2, 0, null), new("special", 0, 1, 0, null), new("two-one", 2, 1, 0, null),
            new("no-episode", 1, null, 0, null), new("a-tie", 1, 2, 0, null), new("no-season", null, 1, 0, null), new("one-one", 1, 1, 0, null),
        ]);

        Assert.Equal(["one-one", "a-tie", "b-tie", "no-episode", "two-one", "no-season", "special"], ordered.Select(e => e.Id));
    }
}

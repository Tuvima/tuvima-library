using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class TvEpisodeRangeCoverageTests
{
    private static readonly IReadOnlySet<int> SeasonOfTen = new HashSet<int>(Enumerable.Range(1, 10));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoEpisodeEnd_IsNotARange(string? episodeEnd)
    {
        var decision = TvEpisodeRangeCoverage.Evaluate(1, episodeEnd, SeasonOfTen);

        Assert.Equal(EpisodeRangeKind.NotARange, decision.Kind);
    }

    [Theory]
    [InlineData(1, "2", new[] { 1, 2 })]
    [InlineData(3, "5", new[] { 3, 4, 5 })]
    [InlineData(1, "6", new[] { 1, 2, 3, 4, 5, 6 })]
    public void UnbrokenRangeInProviderList_IsClean(int first, string end, int[] expected)
    {
        var decision = TvEpisodeRangeCoverage.Evaluate(first, end, SeasonOfTen);

        Assert.Equal(EpisodeRangeKind.Clean, decision.Kind);
        Assert.Equal(expected, decision.Episodes);
        Assert.Null(decision.Reason);
    }

    [Theory]
    [InlineData(1, "7")]
    [InlineData(1, "200")]
    public void RangeLongerThanTheLimit_NeedsReview(int first, string end)
    {
        var decision = TvEpisodeRangeCoverage.Evaluate(first, end, SeasonOfTen);

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
    }

    [Theory]
    [InlineData(2, "2")]
    [InlineData(3, "2")]
    [InlineData(1, "abc")]
    public void NonAscendingOrUnreadableRange_NeedsReview(int first, string end)
    {
        var decision = TvEpisodeRangeCoverage.Evaluate(first, end, SeasonOfTen);

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
    }

    [Fact]
    public void MissingFirstEpisode_NeedsReview()
    {
        var decision = TvEpisodeRangeCoverage.Evaluate(null, "2", SeasonOfTen);

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
    }

    [Fact]
    public void EpisodeMissingFromProviderList_NeedsReview()
    {
        var decision = TvEpisodeRangeCoverage.Evaluate(1, "3", new HashSet<int> { 1, 3 });

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
        Assert.Contains("2", decision.Reason);
    }

    [Fact]
    public void UnavailableProviderList_NeedsReview()
    {
        var decision = TvEpisodeRangeCoverage.Evaluate(1, "2", null);

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
    }

    [Fact]
    public void WholeShow_OfThreePartsInOneSeason_IsCleanForEpisodesOneToThree()
    {
        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(1, new HashSet<int> { 1, 2, 3 });

        Assert.Equal(EpisodeRangeKind.Clean, decision.Kind);
        Assert.Equal([1, 2, 3], decision.Episodes);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void WholeShow_OfASinglePart_IsCleanForEpisodeOne()
    {
        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(1, new HashSet<int> { 1 });

        Assert.Equal(EpisodeRangeKind.Clean, decision.Kind);
        Assert.Equal([1], decision.Episodes);
    }

    [Fact]
    public void WholeShow_OfSixParts_IsCleanAtTheLimit()
    {
        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(1, new HashSet<int>(Enumerable.Range(1, 6)));

        Assert.Equal(EpisodeRangeKind.Clean, decision.Kind);
        Assert.Equal(6, decision.Episodes.Count);
    }

    [Fact]
    public void WholeShow_OfSevenParts_FallsBackWithAReasonNamingTheLimit()
    {
        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(1, new HashSet<int>(Enumerable.Range(1, 7)));

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
        Assert.Empty(decision.Episodes);
        Assert.Contains("7", decision.Reason);
        Assert.Contains("6", decision.Reason);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(2, 4)]
    public void WholeShow_WithABrokenRunOrNotStartingAtOne_FallsBack(int first, int second)
    {
        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(1, new HashSet<int> { first, second });

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
        Assert.Contains("unbroken", decision.Reason);
    }

    [Fact]
    public void WholeShow_WithoutAnEpisodeList_FallsBack()
    {
        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(1, null);

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
        Assert.Contains("couldn't be fetched", decision.Reason);
    }

    [Fact]
    public void WholeShow_WithAnEmptyEpisodeList_FallsBack()
    {
        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(1, new HashSet<int>());

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
        Assert.Contains("no episodes", decision.Reason);
    }

    [Fact]
    public void WholeShow_InTwoSeasons_FallsBackEvenWithAnEpisodeList()
    {
        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(2, new HashSet<int> { 1, 2 });

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
        Assert.Contains("2 seasons", decision.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WholeShow_WithNoSeasons_FallsBackWithASensibleReason(int seasons)
    {
        var decision = TvEpisodeRangeCoverage.EvaluateWholeShow(seasons, new HashSet<int> { 1 });

        Assert.Equal(EpisodeRangeKind.NeedsReview, decision.Kind);
        Assert.Equal("TMDB doesn't list any seasons for this show.", decision.Reason);
    }
}

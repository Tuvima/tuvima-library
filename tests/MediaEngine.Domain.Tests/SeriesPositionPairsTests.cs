using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class SeriesPositionPairsTests
{
    private static readonly CanonicalArrayEntry[] TwoSeries =
    [
        new() { Ordinal = 0, Value = "3", ValueQid = "Q111" },
        new() { Ordinal = 1, Value = "7.5", ValueQid = "Q222" },
    ];

    [Fact]
    public void PositionFor_ReturnsThePairOfTheAskedSeries_NotTheOtherSeriesNumber()
    {
        Assert.Equal("3", SeriesPositionPairs.PositionFor("Q111", TwoSeries, "9"));
        Assert.Equal("7.5", SeriesPositionPairs.PositionFor("Q222", TwoSeries, "9"));
    }

    [Fact]
    public void PositionFor_KeepsTheTextAsStored_SoADecimalIsNeverRounded()
    {
        var position = SeriesPositionPairs.PositionFor("q222", TwoSeries, null);

        Assert.Equal("7.5", position);
        Assert.Equal(7.5d, SeriesPositionPairs.SortValueOf(position));
    }

    [Fact]
    public void PositionFor_FallsBackToTodaysSingleNumber_WhenTheSeriesHasNoPair()
    {
        Assert.Equal("9", SeriesPositionPairs.PositionFor("Q333", TwoSeries, " 9 "));
        Assert.Equal("9", SeriesPositionPairs.PositionFor(null, TwoSeries, "9"));
        Assert.Equal("9", SeriesPositionPairs.PositionFor("Q111", null, "9"));
        Assert.Null(SeriesPositionPairs.PositionFor("Q333", TwoSeries, " "));
    }

    [Fact]
    public void PositionFor_UsesTheFirstPairInStatementOrder_WhenASeriesIsListedTwice()
    {
        CanonicalArrayEntry[] twice =
        [
            new() { Ordinal = 1, Value = "4", ValueQid = "Q111" },
            new() { Ordinal = 0, Value = "2", ValueQid = "Q111" },
        ];

        Assert.Equal("2", SeriesPositionPairs.PositionFor("Q111", twice, null));
    }

    [Fact]
    public void PositionFor_IgnoresValueOnlyEntries_BecauseTheyBelongToNoSeries()
    {
        CanonicalArrayEntry[] valueOnly = [new() { Ordinal = 0, Value = "5" }];

        Assert.Equal("1", SeriesPositionPairs.PositionFor("Q111", valueOnly, "1"));
    }

    [Theory]
    [InlineData("1", 1d)]
    [InlineData("1.5", 1.5d)]
    [InlineData("#12", 12d)]
    [InlineData("0.2", 0.2d)]
    public void SortValueOf_KeepsDecimals(string position, double expected)
        => Assert.Equal(expected, SeriesPositionPairs.SortValueOf(position));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("omnibus")]
    public void SortValueOf_IsNull_WhenThePositionCarriesNoNumber(string? position)
        => Assert.Null(SeriesPositionPairs.SortValueOf(position));
}

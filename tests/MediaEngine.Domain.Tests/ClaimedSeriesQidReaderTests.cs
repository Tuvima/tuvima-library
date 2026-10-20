using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

/// <summary>The one reader of the Series a Work's own claims name.</summary>
public sealed class ClaimedSeriesQidReaderTests
{
    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
        => ClaimedSeriesQidReader.Merge([pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))]);

    [Fact]
    public void SeriesQid_IsReadWithItsUriPrefixAndLabelStripped()
    {
        var qid = ClaimedSeriesQidReader.Resolve("Books", Values(("series_qid", "http://www.wikidata.org/entity/Q19610143::The Expanse")));

        Assert.Equal("Q19610143", qid);
    }

    [Fact]
    public void RootValues_WinOverAssetValues_ForTheSameKey()
    {
        var merged = ClaimedSeriesQidReader.Merge(
        [
            [new KeyValuePair<string, string>("series_qid", "Q1")],
            [new KeyValuePair<string, string>("series_qid", "Q2")],
        ]);

        Assert.Equal("Q1", ClaimedSeriesQidReader.Resolve("Books", merged));
    }

    [Fact]
    public void ASeriesQidOnlyOnTheAsset_IsStillFound()
    {
        var merged = ClaimedSeriesQidReader.Merge(
        [
            [new KeyValuePair<string, string>("title", "Leviathan Wakes")],
            [new KeyValuePair<string, string>("series_qid", "Q7")],
        ]);

        Assert.Equal("Q7", ClaimedSeriesQidReader.Resolve("Books", merged));
    }

    [Fact]
    public void SeriesQid_BeatsAQidShapedSeriesValue()
    {
        Assert.Equal("Q1", ClaimedSeriesQidReader.Resolve("Books", Values(("series", "Q2"), ("series_qid", "Q1"))));
    }

    [Fact]
    public void AQidShapedSeriesValue_IsUsed_WhenThereIsNoSeriesQid()
    {
        Assert.Equal("Q2", ClaimedSeriesQidReader.Resolve("Books", Values(("series", "Q2"))));
    }

    [Fact]
    public void ASeriesNameThatIsNotAQid_IsNotAClaim()
    {
        Assert.Null(ClaimedSeriesQidReader.Resolve("Books", Values(("series", "The Expanse"))));
    }

    [Fact]
    public void WikidataQid_IsAFallbackForComicsOnly()
    {
        var values = Values(("wikidata_qid", "Q55"));

        Assert.Equal("Q55", ClaimedSeriesQidReader.Resolve("Comics", values));
        Assert.Null(ClaimedSeriesQidReader.Resolve("Books", values));
    }
}

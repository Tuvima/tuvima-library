using MediaEngine.Api.DevSupport;

namespace MediaEngine.Api.Tests;

public sealed class BaselineNormalizerTests
{
    private const string Samples = @"C:\runs\one\samples";
    private const string Data = @"C:\runs\one\data";

    private static BaselineNormalizer Create(params Guid[] localIds)
    {
        var normalizer = new BaselineNormalizer(Samples, (Data, "<data>"), (@"C:\runs\one", "<run>"));
        normalizer.AddLocalIds(localIds);
        return normalizer;
    }

    [Fact]
    public void RelativePath_StripsTheSampleRootInEitherSlashStyleAndAnyCase()
    {
        var normalizer = Create();

        Assert.Equal("books/Dune.epub", normalizer.RelativePath(@"C:\runs\one\samples\books\Dune.epub"));
        Assert.Equal("books/Dune.epub", normalizer.RelativePath("c:/RUNS/one/samples/books/Dune.epub"));
    }

    [Fact]
    public void Text_ReplacesRunFoldersWithTokensSoTwoRunFoldersLookTheSame()
    {
        var first = Create().Text(@"C:\runs\one\data\library\.data\art\cover.jpg");
        var second = new BaselineNormalizer(@"C:\runs\two\samples", (@"C:\runs\two\data", "<data>"), (@"C:\runs\two", "<run>"))
            .Text(@"C:\runs\two\data\library\.data\art\cover.jpg");

        Assert.Equal(@"<data>\library\.data\art\cover.jpg", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Text_ReplacesFoldersWrittenWithEscapedBackslashesInsideJson()
    {
        var text = Create().Text(@"{""path"":""C:\\runs\\one\\samples\\books\\Dune.epub""}");

        Assert.Equal(@"{""path"":""<samples>\\books\\Dune.epub""}", text);
    }

    [Fact]
    public void Text_ReplacesTheRunsOwnRowIdsButKeepsAProvidersGuid()
    {
        var local = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var provider = "2a1afcc9-6179-5fdf-3bea-aec376486895";

        var text = Create(local).Text($"https://cdn.example/{provider}/cover.jpg?item={local:D}");

        Assert.Equal($"https://cdn.example/{provider}/cover.jpg?item={BaselineNormalizer.GuidToken}", text);
    }

    [Fact]
    public void Text_ReplacesAnyIdInAnEngineStreamAddress()
    {
        var text = Create().Text("/stream/artwork/9d3f2a10-7b6c-4d5e-8f90-a1b2c3d4e5f6?size=m");

        Assert.Equal($"/stream/artwork/{BaselineNormalizer.GuidToken}?size=m", text);
    }

    [Fact]
    public void Text_ReplacesTheFolderPathDigestOfAnAudiobookRecordingKey()
    {
        var first = Create().Text("source-folder:" + new string('A', 64));
        var second = Create().Text("source-folder:" + new string('B', 64));

        Assert.Equal(BaselineNormalizer.FolderHashToken, first);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("artwork_settled_at", "2026-10-19T05:54:22Z", "<timestamp>")]
    [InlineData("tmdb_artwork_last_checked_at", "2026-10-19T05:54:22Z", "<timestamp>")]
    [InlineData("air_date", "2008-01-20", "2008-01-20")]
    [InlineData("title", "Dune", "Dune")]
    public void Value_OnlyStopsCarryingTheClockForKeysThatEndInAt(string key, string value, string expected)
    {
        Assert.Equal(expected, Create().Value(key, value));
    }

    [Fact]
    public void Value_LeavesAnEmptyStampEmptySoMissingStaysDistinctFromPresent()
    {
        Assert.Equal(string.Empty, Create().Value("artwork_settled_at", string.Empty));
    }
}

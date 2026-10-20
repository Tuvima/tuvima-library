using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A cover that downloads after an earlier attempt failed (a sibling pass or a sweep that ran first) records where it
/// came from, not the earlier failure.
/// </summary>
public sealed class CoverSourceAfterRetryTests
{
    private const string AppleCover = "https://is1-ssl.mzstatic.com/image/thumb/Music114/v4/cover.jpg/600x600bb.jpg";

    [Theory]
    [InlineData("provider_unavailable")]
    [InlineData("provider_empty")]
    [InlineData("provider_download_failed")]
    [InlineData("none")]
    public void SuccessfulDownload_AfterAFailedAttempt_RecordsTheProvider(string failure)
    {
        Assert.Equal("provider", CoverArtWorker.InferCoverSource([CoverSource(failure)], AppleCover));
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("folder")]
    public void SuccessfulDownload_KeepsARealEarlierSource(string source)
    {
        Assert.Equal(source, CoverArtWorker.InferCoverSource([CoverSource(source)], AppleCover));
    }

    private static CanonicalValue CoverSource(string value) => new()
    {
        EntityId = Guid.NewGuid(),
        Key = MetadataFieldConstants.CoverSource,
        Value = value,
        LastScoredAt = DateTimeOffset.UtcNow,
    };
}

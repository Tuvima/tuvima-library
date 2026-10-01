using MediaEngine.Api.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class MediaFileWriteIntentDispatcherTests
{
    [Theory]
    [InlineData(WriteBackOutcomeKind.Verified, 1, "verified")]
    [InlineData(WriteBackOutcomeKind.Blocked, 1, "blocked")]
    [InlineData(WriteBackOutcomeKind.Unsupported, 1, "unsupported")]
    [InlineData(WriteBackOutcomeKind.Unverified, 1, "pending")]
    [InlineData(WriteBackOutcomeKind.Failed, 1, "pending")]
    [InlineData(WriteBackOutcomeKind.Unverified, 5, "failed")]
    [InlineData(WriteBackOutcomeKind.Failed, 5, "failed")]
    public void MapCompletion_PreservesTruthfulOutcomeAndBoundsRetries(
        WriteBackOutcomeKind kind, int attempts, string expected)
    {
        var intent = new MediaFileWriteIntent { Attempts = attempts };
        var result = MediaFileWriteIntentDispatcher.MapCompletion(
            intent, new WriteBackOutcome(kind, "evidence"));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == "verified" ? null : "evidence", result.Error);
    }
}

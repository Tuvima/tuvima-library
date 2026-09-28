using MediaEngine.Api.Endpoints;

namespace MediaEngine.Api.Tests;

public sealed class TvSeasonReviewPolicyTests
{
    [Theory]
    [InlineData(null, 0, 1, "401", null, false, "Missing local episode number")]
    [InlineData(2, 2, 1, "401", null, false, "Duplicate owned episode number")]
    [InlineData(2, 1, 0, null, null, false, "No unique TMDB episode")]
    [InlineData(2, 1, 2, "401", null, false, "No unique TMDB episode")]
    [InlineData(2, 1, 1, "401", "402", false, "Existing episode match differs; review individually")]
    [InlineData(2, 1, 1, "401", "401", true, "Already matched; artwork can be refreshed")]
    [InlineData(2, 1, 1, "401", null, true, "Ready to match")]
    public void ClassifyTvSeasonReviewRow_OnlyOffersUniqueUnconflictedOwnedEpisodes(
        int? number, int localCount, int providerCount, string? providerId, string? existingId,
        bool expectedCanApply, string expectedStatus)
    {
        var result = ItemCanonicalEndpoints.ClassifyTvSeasonReviewRow(
            number, localCount, providerCount, providerId, existingId);

        Assert.Equal(expectedCanApply, result.CanApply);
        Assert.Equal(expectedStatus, result.Status);
    }
}

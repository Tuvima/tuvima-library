using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class RetailCandidateQualityGuardTests
{
    [Theory]
    [InlineData("The Hunger Games", "The Hunger Games Trilogy (Box Set)", "box_set_candidate")]
    [InlineData("The Hobbit", "The Hobbit and The Lord of the Rings Boxed Set", "box_set_candidate")]
    [InlineData("The Hunger Games", "The Hunger Games", null)]
    [InlineData("The Cairo Trilogy", "The Cairo Trilogy", null)]
    [InlineData("The Hunger Games", "The Hunger Games (The Hunger Games Trilogy, Book 1)", null)]
    public void GetRejectionReasons_RefusesStoreBundlesForASingleBook(string fileTitle, string candidateTitle, string? expected)
    {
        var hints = new Dictionary<string, string> { [MetadataFieldConstants.Title] = fileTitle };

        var reasons = RetailCandidateQualityGuard.GetRejectionReasons(MediaType.Books, hints, candidateTitle, null);

        Assert.Equal(expected is null ? [] : [expected], reasons);
    }
}

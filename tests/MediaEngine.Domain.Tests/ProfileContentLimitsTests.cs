using MediaEngine.Domain.Aggregates;

namespace MediaEngine.Domain.Tests;

public sealed class ProfileContentLimitsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("Everything", null)]
    [InlineData("g", "G")]
    [InlineData(" pg ", "PG")]
    [InlineData("pg-13", "PG-13")]
    [InlineData("PG 13", "PG-13")]
    [InlineData("r", "R")]
    public void Normalize_AcceptsTheFiveChoices(string? requested, string? expected) =>
        Assert.Equal(expected, ProfileContentLimits.Normalize(requested));

    [Theory]
    [InlineData("NC-17")]
    [InlineData("TV-MA")]
    [InlineData("<script>")]
    public void Normalize_RejectsAnythingElse(string requested) =>
        Assert.Throws<ArgumentException>(() => ProfileContentLimits.Normalize(requested));

    [Theory]
    [InlineData("G", 1)]
    [InlineData("TV-Y7", 1)]
    [InlineData("TV-G", 1)]
    [InlineData("All Ages", 1)]
    [InlineData("PG", 2)]
    [InlineData("tv-pg", 2)]
    [InlineData("E10+", 2)]
    [InlineData("PG-13", 3)]
    [InlineData("TV-14", 3)]
    [InlineData("Rated PG-13", 3)]
    [InlineData("Teen", 3)]
    [InlineData("R", 4)]
    [InlineData("TV-MA", 4)]
    [InlineData("Mature", 4)]
    [InlineData("NC-17", 5)]
    [InlineData("X", 5)]
    [InlineData("12", 3)]
    [InlineData("12A", 3)]
    [InlineData("15", 4)]
    [InlineData("18", 5)]
    [InlineData("7+", 2)]
    [InlineData("0", 1)]
    [InlineData("FSK-12", 3)]
    [InlineData("FSK 16", 4)]
    [InlineData("DE:16", 4)]
    [InlineData("GB:15", 4)]
    [InlineData("GB:PG", 2)]
    [InlineData("DE/6", 1)]
    [InlineData("PEGI-18", 5)]
    [InlineData("PG|R", 4)]
    [InlineData("R|PG", 4)]
    [InlineData("|PG", 2)]
    public void RankOfRating_PlacesEveryVocabularyOnOneLadder(string rating, int expected) =>
        Assert.Equal(expected, ProfileContentLimits.RankOfRating(rating));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not Rated")]
    [InlineData("Unrated")]
    [InlineData("NR")]
    [InlineData("something odd")]
    public void RankOfRating_LeavesUnratedAndUnknownValuesUnranked(string? rating) =>
        Assert.Null(ProfileContentLimits.RankOfRating(rating));

    [Theory]
    [InlineData(null, false, "NC-17", true)]
    [InlineData(null, false, null, true)]
    [InlineData("PG", false, "G", true)]
    [InlineData("PG", false, "PG", true)]
    [InlineData("PG", false, "PG-13", false)]
    [InlineData("PG", false, "TV-14", false)]
    [InlineData("PG", false, null, false)]
    [InlineData("PG", true, null, true)]
    [InlineData("PG", true, "R", false)]
    [InlineData("R", false, "TV-MA", true)]
    [InlineData("R", false, "NC-17", false)]
    [InlineData("G", false, "something odd", false)]
    public void Allows_AppliesTheLimitAndTheUnratedChoice(string? limit, bool allowUnrated, string? rating, bool expected) =>
        Assert.Equal(expected, ProfileContentLimits.Allows(limit, allowUnrated, rating));

    [Fact]
    public void NewKidsProfilesStartOnPg() => Assert.Equal("PG", ProfileContentLimits.KidsDefault);
}

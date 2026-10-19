using MediaEngine.Api.Services.Taste;
using MediaEngine.Contracts.Taste;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>How typed taste values are cleaned, and how removals and additions sit on top of the learned numbers.</summary>
public sealed class TasteOverridesTests
{
    private static readonly Guid Person = Guid.NewGuid();

    [Theory]
    [InlineData("  Sci-Fi  ", "sci-fi")]
    [InlineData("Slow   Burn", "slow burn")]
    [InlineData("R&B/Soul", "r&b/soul")]
    public void AGenreOrMood_IsTrimmedSpacedAndLowerCased(string typed, string expected) =>
        Assert.Equal(expected, TasteOverrides.Normalize(TasteGroups.Genre, typed));

    [Theory]
    [InlineData("1990s", "1990s")]
    [InlineData(" 2020S ", "2020s")]
    [InlineData("1900s", "1900s")]
    public void AnEra_MustBeADecade(string typed, string expected) =>
        Assert.Equal(expected, TasteOverrides.Normalize(TasteGroups.Era, typed));

    [Theory]
    [InlineData("1995")]
    [InlineData("90s")]
    [InlineData("the 90s")]
    [InlineData("")]
    [InlineData(null)]
    public void ANotADecade_IsRejectedForAnEra(string? typed) =>
        Assert.Null(TasteOverrides.Normalize(TasteGroups.Era, typed));

    [Fact]
    public void AnEmptyOrOverlongValue_IsRejected()
    {
        Assert.Null(TasteOverrides.Normalize(TasteGroups.Mood, "   "));
        Assert.Null(TasteOverrides.Normalize(TasteGroups.Mood, new string('a', TasteOverrides.MaxValueLength + 1)));
        Assert.NotNull(TasteOverrides.Normalize(TasteGroups.Mood, new string('a', TasteOverrides.MaxValueLength)));
    }

    [Fact]
    public void Groups_MapFromTheirWireNames()
    {
        Assert.Equal(TasteGroups.Genre, TasteOverrides.ToStoredGroup("Genres"));
        Assert.Equal(TasteGroups.Mood, TasteOverrides.ToStoredGroup("moods"));
        Assert.Equal(TasteGroups.Era, TasteOverrides.ToStoredGroup(" eras "));
        Assert.Null(TasteOverrides.ToStoredGroup("genre"));
        Assert.Null(TasteOverrides.ToStoredGroup(null));
    }

    [Fact]
    public void Items_HideRemovedValues_ListAddedOnesLast_AndKeepLearnedOrder()
    {
        var learned = new Dictionary<string, double> { ["drama"] = 0.2, ["sci-fi"] = 0.5, ["comedy"] = 0.3 };
        var changes = new[]
        {
            new TasteOverride(Person, TasteGroups.Genre, "comedy", TasteOverrideActions.Removed),
            new TasteOverride(Person, TasteGroups.Genre, "western", TasteOverrideActions.Added),
            new TasteOverride(Person, TasteGroups.Mood, "cozy", TasteOverrideActions.Removed),
        };

        var items = TasteOverrides.Items(learned, changes, TasteGroups.Genre);

        Assert.Equal(["sci-fi", "drama", "western"], items.Select(item => item.Value));
        Assert.Equal([TasteValues.SourceLearned, TasteValues.SourceLearned, TasteValues.SourceAdded], items.Select(item => item.Source));
        Assert.Equal(0.5, items[0].Weight);
        Assert.Null(items[2].Weight);
    }

    [Fact]
    public void AnAddedValueTheLibraryAlreadyLearned_IsShownOnceAsLearned()
    {
        var learned = new Dictionary<string, double> { ["drama"] = 0.4 };
        var changes = new[] { new TasteOverride(Person, TasteGroups.Genre, "drama", TasteOverrideActions.Added) };

        var item = Assert.Single(TasteOverrides.Items(learned, changes, TasteGroups.Genre));

        Assert.Equal(TasteValues.SourceLearned, item.Source);
    }

    [Fact]
    public void Items_ForNothingLearned_ShowOnlyWhatWasAdded()
    {
        var changes = new[] { new TasteOverride(Person, TasteGroups.Era, "1980s", TasteOverrideActions.Added) };

        var item = Assert.Single(TasteOverrides.Items(null, changes, TasteGroups.Era));

        Assert.Equal("1980s", item.Value);
    }

    [Fact]
    public void Apply_GivesATypedValueTheStrongestWeight_AndLeavesTheOriginalAlone()
    {
        var original = new TasteProfile
        {
            UserId = Person,
            GenreDistribution = new Dictionary<string, double> { ["drama"] = 0.7, ["comedy"] = 0.3 },
            Summary = "kept",
        };
        var changes = new[]
        {
            new TasteOverride(Person, TasteGroups.Genre, "drama", TasteOverrideActions.Removed),
            new TasteOverride(Person, TasteGroups.Genre, "western", TasteOverrideActions.Added),
        };

        var applied = TasteOverrides.Apply(original, changes);

        Assert.Equal(0.7, applied.GenreDistribution["western"]);
        Assert.False(applied.GenreDistribution.ContainsKey("drama"));
        Assert.Equal("kept", applied.Summary);
        Assert.True(original.GenreDistribution.ContainsKey("drama"));
        Assert.False(original.GenreDistribution.ContainsKey("western"));
    }

    [Fact]
    public void DecadeSuggestions_RunFromTheCurrentDecadeBackTo1900s()
    {
        var decades = TasteOverrides.DecadeSuggestions(2026);

        Assert.Equal("2020s", decades[0]);
        Assert.Equal("1900s", decades[^1]);
        Assert.Equal(13, decades.Count);
    }
}

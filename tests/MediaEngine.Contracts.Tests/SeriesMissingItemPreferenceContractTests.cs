using System.Text.Json;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Contracts.Tests;

public sealed class SeriesMissingItemPreferenceContractTests
{
    [Fact]
    public void Response_CarriesOnlyTheProfilesTick_WithNoInheritState()
    {
        var profileId = Guid.NewGuid();
        var value = new SeriesMissingItemPreferenceDto
        {
            ProfileId = profileId,
            MediaType = "movies",
            ContainerKey = "q22092344",
            ShowMissing = true,
        };

        var json = JsonSerializer.Serialize(value);
        var roundTrip = JsonSerializer.Deserialize<SeriesMissingItemPreferenceDto>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(profileId, roundTrip!.ProfileId);
        Assert.Equal("movies", roundTrip.MediaType);
        Assert.Equal("q22092344", roundTrip.ContainerKey);
        Assert.True(roundTrip.ShowMissing);
        Assert.Contains("\"show_missing\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_ForASeriesWithNoStoredTick_ReadsAsUnticked()
    {
        var value = new SeriesMissingItemPreferenceDto { ProfileId = Guid.NewGuid(), MediaType = "books", ContainerKey = "q1" };

        var json = JsonSerializer.Serialize(value);

        Assert.False(value.ShowMissing);
        Assert.Contains("\"show_missing\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveRequest_UsesExplicitSnakeCaseOverrideFields()
    {
        var value = new SaveSeriesMissingItemPreferenceRequest
        {
            MediaType = "movies",
            ContainerKey = "q22092344",
            ShowMissing = true,
        };

        var json = JsonSerializer.Serialize(value);

        Assert.Contains("\"media_type\":\"movies\"", json, StringComparison.Ordinal);
        Assert.Contains("\"container_key\":\"q22092344\"", json, StringComparison.Ordinal);
        Assert.Contains("\"show_missing\":true", json, StringComparison.Ordinal);
    }
}

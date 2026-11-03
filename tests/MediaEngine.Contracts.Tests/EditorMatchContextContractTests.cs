using System.Text.Json;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of the Match tool's starting reads and the by-number sibling match (universal editor v3, WP4).</summary>
public sealed class EditorMatchContextContractTests
{
    [Fact]
    public void MatchContext_UsesSnakeCaseNames_AndRoundTripsTheBestGuess()
    {
        var id = Guid.NewGuid();
        var context = new EditorMatchContextDto
        {
            EntityId = id,
            MediaType = "TV",
            FileName = "Show.S02E05.mkv",
            ParsedTitle = null,
            ParsedShow = "Show",
            ParsedSeason = 2,
            ParsedEpisode = 5,
            BestGuess = new EditorMatchBestGuessDto { ProviderName = "tvdb", ProviderItemId = "200", Name = "Show", Year = "2015" },
        };

        var json = JsonSerializer.Serialize(context);
        var back = JsonSerializer.Deserialize<EditorMatchContextDto>(json)!;

        Assert.Equal(
            "{\"entity_id\":\"" + id.ToString("D") + "\",\"media_type\":\"TV\",\"file_name\":\"Show.S02E05.mkv\",\"parsed_title\":null,\"parsed_show\":\"Show\",\"parsed_season\":2,\"parsed_episode\":5,\"parsed_year\":null,"
            + "\"best_guess\":{\"provider_name\":\"tvdb\",\"provider_item_id\":\"200\",\"name\":\"Show\",\"year\":\"2015\",\"image_url\":null}}",
            json);
        Assert.Equal("200", back.BestGuess!.ProviderItemId);
        Assert.Equal(5, back.ParsedEpisode);
    }

    [Fact]
    public void SiblingPreview_UsesSnakeCaseNames()
    {
        var item = Guid.NewGuid();
        var preview = new EpisodeSiblingMatchPreviewDto
        {
            EntityId = item,
            SeriesId = "200",
            ShowName = "Show",
            SeasonNumber = 2,
            MatchableCount = 1,
            Rows = [new EpisodeSiblingMatchRowDto { EntityId = item, SourceFileName = "a.mkv", EpisodeNumber = 3, TargetEpisodeId = "e3", TargetTitle = "Three", Found = true }],
        };

        var json = JsonSerializer.Serialize(preview);

        Assert.Contains("\"matchable_count\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"rows\":[{\"entity_id\":\"" + item.ToString("D") + "\",\"source_file_name\":\"a.mkv\",\"episode_number\":3,\"target_episode_id\":\"e3\",\"target_title\":\"Three\",\"found\":true}]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SiblingMatchKind_IsAKnownDataChange_ThatCarriesItsTargetAsJson()
    {
        Assert.True(EditSessionChangeKinds.IsKnown(EditSessionChangeKinds.SiblingMatch));
        Assert.True(EditSessionChangeKinds.IsDataChange(EditSessionChangeKinds.SiblingMatch));
        Assert.Equal("sibling_match", EditSessionChangeKinds.SiblingMatch);

        var text = EditSessionSiblingMatchTarget.Serialize(new EpisodeSiblingMatchTarget { SeriesId = "200", SeasonNumber = 0 });

        Assert.Equal("{\"series_id\":\"200\",\"season_number\":0}", text);
        Assert.True(EditSessionSiblingMatchTarget.TryParse(text, out var target));
        Assert.Equal("200", target!.SeriesId);
        Assert.Equal(0, target.SeasonNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"series_id\":\"abc\",\"season_number\":1}")]
    [InlineData("{\"series_id\":\"200\",\"season_number\":-1}")]
    [InlineData("{\"series_id\":\"1234567890123\",\"season_number\":1}")]
    public void TryParse_RefusesATargetThatIsNotAShowAndSeason(string? text)
    {
        Assert.False(EditSessionSiblingMatchTarget.TryParse(text, out var target));
        Assert.Null(target);
    }
}

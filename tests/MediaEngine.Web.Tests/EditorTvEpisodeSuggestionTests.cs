using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>Which episode the Match tab pre-selects, and the season the list opens on (universal editor v5, 3c).</summary>
public sealed class EditorTvEpisodeSuggestionTests
{
    private static TvMatchBrowserEpisodeDto Episode(int season, int number, string? aired, int? runtime) =>
        new($"e{season}{number}", "1", "Show", $"s{season}", season, number, $"Episode {number}", aired, null, null, string.Empty, false, false, null, runtime);

    private static TvMatchBrowserSeasonDto Season(int number, bool current = false) => new($"s{number}", "1", number, $"Season {number}", null, current);

    [Fact]
    public void TheNumberInTheFileName_PicksThatEpisode_InTheFileNamesSeason()
    {
        var episodes = new[] { Episode(1, 3, null, null), Episode(2, 3, null, null) };

        var pick = EditorTvEpisodeSuggestion.FromFileName(episodes, new EditorMatchContextDto { ParsedSeason = 2, ParsedEpisode = 3 });

        Assert.Equal("e23", pick!.Episode.EpisodeId);
        Assert.Equal(EditorEpisodeSuggestionSources.FileName, pick.Source);
    }

    [Fact]
    public void AFileNameNumberTheSeasonLacks_SuggestsNothing()
    {
        var episodes = new[] { Episode(2, 1, null, null) };

        Assert.Null(EditorTvEpisodeSuggestion.FromFileName(episodes, new EditorMatchContextDto { ParsedSeason = 2, ParsedEpisode = 9 }));
        Assert.Null(EditorTvEpisodeSuggestion.FromFileName(episodes, new EditorMatchContextDto()));
    }

    [Fact]
    public void WithNoNumber_TheNearestRuntimeWins_ThenTheEarlierEpisodeOnATie()
    {
        var episodes = new[] { Episode(1, 1, "2021-01-01", 58), Episode(1, 2, "2021-01-08", 44), Episode(1, 3, "2021-01-15", 46), Episode(1, 4, "2021-01-22", 48) };

        var pick = EditorTvEpisodeSuggestion.Fallback(episodes, 45 * 60d);

        Assert.Equal(2, pick!.Episode.EpisodeNumber);
        Assert.Equal(EditorEpisodeSuggestionSources.Runtime, pick.Source);
    }

    [Fact]
    public void WithNoNumberAndNoMeasuredLength_TheFirstToAirIsSuggested()
    {
        var episodes = new[] { Episode(1, 1, "2021-02-01", 40), Episode(1, 2, "2021-01-08", 40), Episode(1, 3, null, 40) };

        var pick = EditorTvEpisodeSuggestion.Fallback(episodes, null);

        Assert.Equal(2, pick!.Episode.EpisodeNumber);
        Assert.Equal(EditorEpisodeSuggestionSources.AirOrder, pick.Source);
    }

    [Fact]
    public void ASeasonWithNoEpisodes_SuggestsNothing() => Assert.Null(EditorTvEpisodeSuggestion.Fallback([], 2700));

    [Fact]
    public void TheListOpensOnTheFileNamesSeason_ThenTheItemsOwn_ThenTheFirstRealSeason_NeverSpecialsByDefault()
    {
        IReadOnlyList<TvMatchBrowserSeasonDto> seasons = [Season(0), Season(1), Season(2, current: true), Season(3)];

        Assert.Equal(3, EditorTvEpisodeSuggestion.OpeningSeason(seasons, new EditorMatchContextDto { ParsedSeason = 3 }, useFileName: true));
        Assert.Equal(2, EditorTvEpisodeSuggestion.OpeningSeason(seasons, new EditorMatchContextDto(), useFileName: true));
        Assert.Equal(1, EditorTvEpisodeSuggestion.OpeningSeason([Season(0), Season(1), Season(3)], null, useFileName: true));
        Assert.Equal(0, EditorTvEpisodeSuggestion.OpeningSeason(seasons, new EditorMatchContextDto { ParsedSeason = 0 }, useFileName: true));
    }
}

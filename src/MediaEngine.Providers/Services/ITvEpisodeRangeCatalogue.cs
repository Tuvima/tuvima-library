namespace MediaEngine.Providers.Services;

/// <summary>Lists the episode numbers a TV provider has for one season (aired order).</summary>
public interface ITvEpisodeRangeCatalogue
{
    /// <summary>Returns the season's episode numbers, or null when the list cannot be read.</summary>
    Task<IReadOnlySet<int>?> GetSeasonEpisodeNumbersAsync(
        string seriesId,
        int seasonNumber,
        CancellationToken ct = default);
}

/// <summary>TheTVDB-backed season list; reuses the client's cached default-order episode pages.</summary>
public sealed class TvdbEpisodeRangeCatalogue(TvdbRetailClient client) : ITvEpisodeRangeCatalogue
{
    public async Task<IReadOnlySet<int>?> GetSeasonEpisodeNumbersAsync(
        string seriesId,
        int seasonNumber,
        CancellationToken ct = default)
    {
        var episodes = await client.GetAllEpisodesAsync(seriesId, language: "eng", ct: ct).ConfigureAwait(false);
        var numbers = new HashSet<int>();
        foreach (var episode in episodes)
        {
            if (int.TryParse(episode["seasonNumber"]?.ToString(), out var season)
                && season == seasonNumber
                && int.TryParse(episode["number"]?.ToString(), out var number))
            {
                numbers.Add(number);
            }
        }

        return numbers.Count == 0 ? null : numbers;
    }
}

using System.Text.Json.Nodes;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

/// <summary>TVDB v4 provider for exact, aired-order TV episode discovery.</summary>
public sealed class TvdbMetadataProvider(
    TvdbRetailClient client,
    ILogger<TvdbMetadataProvider> logger) : IExternalMetadataProvider
{
    public string Name => "tvdb";
    public ProviderDomain Domain => ProviderDomain.Video;
    public IReadOnlyList<string> CapabilityTags => ["tv_series", "seasons", "episodes"];
    public Guid ProviderId => WellKnownProviders.Tvdb;
    public bool CanHandle(MediaType mediaType) => mediaType == MediaType.TV;
    public bool CanHandle(EntityType entityType) => entityType is EntityType.Work or EntityType.MediaAsset;

    public async Task<IReadOnlyList<ProviderClaim>> FetchAsync(
        ProviderLookupRequest request,
        CancellationToken ct = default)
    {
        if (!CanHandle(request.MediaType) || !CanHandle(request.EntityType)
            || !int.TryParse(request.SeasonNumber, out var season)
            || !int.TryParse(request.EpisodeNumber, out var episode)
            || !client.IsConfigured())
            return [];
        if (request.Hints?.GetValueOrDefault(MetadataFieldConstants.IdentityProvider) is { } source
            && !source.Equals(Name, StringComparison.OrdinalIgnoreCase))
            return [];

        try
        {
            var showName = request.ShowName ?? request.Series ?? request.Title;
            var confirmedShowId = request.Hints?.GetValueOrDefault(BridgeIdKeys.TvdbId);
            var show = !string.IsNullOrWhiteSpace(confirmedShowId)
                ? await client.GetSeriesAsync(confirmedShowId, ct).ConfigureAwait(false)
                : await FindShowAsync(showName, ct).ConfigureAwait(false);
            if (show is null)
                return [];
            var showId = Id(show);
            if (showId is null)
                return [];
            show = await client.GetSeriesAsync(showId, ct).ConfigureAwait(false) ?? show;

            var confirmedEpisodeId = request.Hints?.GetValueOrDefault(BridgeIdKeys.TvdbEpisodeId);
            var match = !string.IsNullOrWhiteSpace(confirmedEpisodeId)
                ? await client.GetEpisodeAsync(confirmedEpisodeId, ct).ConfigureAwait(false)
                : await FindEpisodeAsync(showId, season, episode, ct).ConfigureAwait(false);
            if (match is null)
                return [];
            if (!string.IsNullOrWhiteSpace(confirmedEpisodeId)
                && Text(match, "seriesId") != showId)
                return [];

            var claims = new List<ProviderClaim>();
            Add("show_name", Text(show, "name") ?? showName, .9);
            Add("author", Text(show, "name") ?? showName, .8);
            Add(BridgeIdKeys.TvdbId, showId, 1);
            Add("season_number", season.ToString(), 1);
            Add("episode_number", episode.ToString(), 1);
            Add("episode_title", Text(match, "name"), .9);
            Add("title", Text(match, "name"), .9);
            Add("episode_description", Text(match, "overview"), .85);
            Add(BridgeIdKeys.TvdbEpisodeId, Id(match), 1);
            Add("air_date", Text(match, "aired"), .9);
            if (Text(show, "firstAired") is { Length: >= 4 } premiered)
                Add("year", premiered[..4], .85);
            else
                Add("year", Text(show, "year"), .8);
            if (show["characters"] is JsonArray characters)
            {
                foreach (var character in characters.Where(node => node is not null))
                {
                    var personName = Text(character, "personName");
                    var personId = Text(character, "peopleId");
                    var role = Text(character, "peopleType");
                    if (string.IsNullOrWhiteSpace(personName)
                        || string.IsNullOrWhiteSpace(personId)
                        || !string.Equals(role, "Actor", StringComparison.OrdinalIgnoreCase))
                        continue;
                    Add(MetadataFieldConstants.CastMember, personName, .9);
                    Add("cast_member_character", Text(character, "name"), .85);
                    Add("cast_member_tvdb_id", personId, .95);
                    Add("cast_member_tvdb_identity", $"{personId}::{personName}", .95);
                    Add("cast_member_profile_url", Text(character, "personImgURL"), .8);
                }
            }
            return claims;

            void Add(string key, string? value, double confidence)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    claims.Add(new ProviderClaim(key, value, confidence));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TVDB episode enrichment failed");
            return [];
        }
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        ProviderLookupRequest request,
        int limit = 25,
        CancellationToken ct = default)
    {
        if (!CanHandle(request.MediaType) || !client.IsConfigured())
            return [];

        try
        {
            var showName = request.ShowName ?? request.Series ?? request.Title;
            if (string.IsNullOrWhiteSpace(showName))
                return [];
            var response = await client.SearchSeriesAsync(showName, ct).ConfigureAwait(false);
            var shows = response?.AsArray().Where(node => node is not null)
                .Take(Math.Clamp(limit, 1, 25)).ToList() ?? [];

            if (!int.TryParse(request.SeasonNumber, out var season))
                return shows.Select(show => new SearchResultItem(
                    Text(show, "name") ?? "Untitled series", null,
                    Text(show, "overview"), Text(show, "year"),
                    null, Id(show), .6, Name, "show",
                    new Dictionary<string, string> { [BridgeIdKeys.TvdbId] = Id(show) ?? "" }))
                    .ToList();

            var results = new List<SearchResultItem>();
            foreach (var show in shows)
            {
                var showId = Id(show);
                if (showId is null)
                    continue;
                var episodes = await client.GetAllEpisodesAsync(showId, ct: ct).ConfigureAwait(false);
                foreach (var episode in episodes)
                {
                    if (episode is null || Number(episode, "seasonNumber") != season)
                        continue;
                    if (int.TryParse(request.EpisodeNumber, out var requestedNumber)
                        && Number(episode, "number") != requestedNumber)
                        continue;
                    var episodeId = Id(episode);
                    if (episodeId is null)
                        continue;
                    results.Add(new SearchResultItem(
                        Text(episode, "name") ?? $"Episode {Number(episode, "number")}",
                        Text(show, "name"), Text(episode, "overview"),
                        Text(episode, "aired") is { Length: >= 4 } airDate ? airDate[..4] : null,
                        null, episodeId, .8, Name, "episode",
                        new Dictionary<string, string>
                        {
                            [BridgeIdKeys.TvdbId] = showId,
                            [BridgeIdKeys.TvdbEpisodeId] = episodeId,
                            ["show_name"] = Text(show, "name") ?? showName,
                            ["season_number"] = season.ToString(),
                            ["episode_number"] = Number(episode, "number")?.ToString() ?? "",
                            ["episode_title"] = Text(episode, "name") ?? "",
                        }));
                    if (results.Count >= limit)
                        return results;
                }
            }
            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TVDB TV search failed");
            return [];
        }
    }

    private async Task<JsonNode?> FindShowAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var results = await client.SearchSeriesAsync(name, ct).ConfigureAwait(false);
        return results?.AsArray().FirstOrDefault(node =>
            string.Equals(Text(node, "name"), name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonNode?> FindEpisodeAsync(string showId, int season, int episode, CancellationToken ct)
    {
        var episodes = await client.GetAllEpisodesAsync(showId, ct: ct).ConfigureAwait(false);
        return episodes.FirstOrDefault(node =>
            Number(node, "seasonNumber") == season && Number(node, "number") == episode);
    }

    private static string? Text(JsonNode? node, string key) => node?[key]?.ToString();
    private static string? Id(JsonNode? node)
    {
        var remote = Text(node, "tvdb_id");
        if (!string.IsNullOrWhiteSpace(remote) && remote.All(char.IsDigit)) return remote;
        var id = Text(node, "id");
        return !string.IsNullOrWhiteSpace(id) && id.All(char.IsDigit) ? id : null;
    }
    private static int? Number(JsonNode? node, string key) => int.TryParse(Text(node, key), out var value) ? value : null;
}

using System.Text.Json.Nodes;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private const string UnmatchedTvShowMessage = ProviderArtworkDiscoveryDto.UnmatchedTvShowMessage;

    private static async Task<string?> ResolveTvdbArtworkIdAsync(
        EditorScopeResolution scope, ICanonicalValueRepository canonicals, CancellationToken ct,
        EditorScopeResolution? seriesScope = null, ProviderApiClient? tvdb = null,
        EditorScopeResolution? seasonScope = null)
    {
        var key = scope.ScopeId switch
        {
            "series" => BridgeIdKeys.TvdbId,
            "season" => BridgeIdKeys.TvdbSeasonId,
            "episode" => BridgeIdKeys.TvdbEpisodeId,
            _ => string.Empty,
        };
        if (key.Length == 0 || NormalizeEditorMediaType(scope.MediaType) != "TV")
        {
            return null;
        }
        var values = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(scope.FieldEntityId, ct));
        var source = GetCanonicalValue(values, MetadataFieldConstants.IdentityProvider);
        if (string.Equals(source, "tvdb", StringComparison.OrdinalIgnoreCase)
            && GetCanonicalValue(values, key) is { Length: > 0 } own)
        {
            return own;
        }
        return await ResolveInheritedTvdbIdAsync(scope, values, seriesScope, seasonScope, canonicals, tvdb, ct);
    }

    /// <summary>
    /// A season or episode with no TheTVDB identity of its own uses the confirmed show above it: the show's TheTVDB id
    /// is read from the show, then the season or episode id is worked out from TheTVDB's default order.
    /// </summary>
    private static async Task<string?> ResolveInheritedTvdbIdAsync(
        EditorScopeResolution scope, IReadOnlyDictionary<string, string> ownValues, EditorScopeResolution? seriesScope,
        EditorScopeResolution? seasonScope, ICanonicalValueRepository canonicals, ProviderApiClient? tvdb, CancellationToken ct)
    {
        if (tvdb is null || !tvdb.IsConfigured("tvdb"))
        {
            return null;
        }
        var seasonValues = seasonScope is null || seasonScope.FieldEntityId == scope.FieldEntityId
            ? ownValues
            : BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(seasonScope.FieldEntityId, ct));
        try
        {
            return await ResolveInheritedTvdbIdCoreAsync(scope, ownValues, seasonValues, seriesScope, canonicals,
                (showId, token) => tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", showId)), token),
                (showId, token) => tvdb.GetAllPagesAsync("tvdb", "series_episodes",
                    ProviderApiClient.Args(("id", showId), ("season_type", "default")), token), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // TheTVDB being unreachable must not turn the picker into an error page; it reads as "no match found".
            System.Diagnostics.Trace.TraceWarning($"TheTVDB lookup for an inherited season or episode failed ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    internal static async Task<string?> ResolveInheritedTvdbIdCoreAsync(
        EditorScopeResolution scope, IReadOnlyDictionary<string, string> ownValues,
        IReadOnlyDictionary<string, string> seasonScopeValues, EditorScopeResolution? seriesScope,
        ICanonicalValueRepository canonicals,
        Func<string, CancellationToken, Task<JsonNode?>> getSeries,
        Func<string, CancellationToken, Task<IReadOnlyList<JsonNode>>> getEpisodes,
        CancellationToken ct)
    {
        if (seriesScope is null || scope.ScopeId is not ("season" or "episode")
            || NormalizeEditorMediaType(scope.MediaType) != "TV")
        {
            return null;
        }
        var ownSource = GetCanonicalValue(ownValues, MetadataFieldConstants.IdentityProvider);
        if (!string.IsNullOrWhiteSpace(ownSource) && !string.Equals(ownSource, "tvdb", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var showId = await ResolveMatchedTvdbShowIdAsync(seriesScope, canonicals, ct);
        // Same order as TheTVDB matching: the season's own number, then the item's, then "Season N" in its title.
        var foundSeason = ParseTvdbNumber(GetCanonicalValue(seasonScopeValues, MetadataFieldConstants.SeasonNumber))
            ?? ParseTvdbNumber(GetCanonicalValue(ownValues, MetadataFieldConstants.SeasonNumber))
            ?? ParseOwnedSeasonNumber(scope);
        if (showId is null || foundSeason is not { } seasonNumber)
        {
            return null;
        }
        if (scope.ScopeId == "season")
        {
            return FindTvdbSeasonId(await getSeries(showId, ct), seasonNumber);
        }
        return int.TryParse(GetCanonicalValue(ownValues, "episode_number"), out var episodeNumber)
            ? FindTvdbEpisodeId(await getEpisodes(showId, ct), seasonNumber, episodeNumber)
            : null;
    }

    /// <summary>The TheTVDB id of a show that is confirmed against TheTVDB, or null when it is not matched.</summary>
    private static async Task<string?> ResolveMatchedTvdbShowIdAsync(
        EditorScopeResolution seriesScope, ICanonicalValueRepository canonicals, CancellationToken ct)
    {
        var values = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(seriesScope.FieldEntityId, ct));
        return string.Equals(GetCanonicalValue(values, MetadataFieldConstants.IdentityProvider), "tvdb", StringComparison.OrdinalIgnoreCase)
            && GetCanonicalValue(values, BridgeIdKeys.TvdbId) is { Length: > 0 } id && id.All(char.IsDigit)
                ? id : null;
    }

    internal static string? FindTvdbSeasonId(JsonNode? series, int seasonNumber) =>
        series?["seasons"] is JsonArray seasons
            ? seasons.Where(season => season is not null && IsDefaultTvdbSeason(season, series)
                    && ParseTvdbNumber(TvdbText(season, "number")) == seasonNumber)
                .Select(season => TvdbText(season, "id")).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            : null;

    internal static string? FindTvdbEpisodeId(IEnumerable<JsonNode> episodes, int seasonNumber, int episodeNumber) =>
        episodes.Where(episode => ParseTvdbNumber(TvdbText(episode, "seasonNumber")) == seasonNumber
                && ParseTvdbNumber(TvdbText(episode, "number")) == episodeNumber)
            .Select(episode => TvdbText(episode, "id")).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

    private static async Task<IReadOnlyList<(string Role, string AssetType, ProviderArtworkCandidate Candidate)>>
        DiscoverTvdbArtworkAsync(EditorScopeResolution scope, string tvdbId, ProviderApiClient tvdb,
            CancellationToken ct)
    {
        JsonNode? detail = scope.ScopeId switch
        {
            "series" => await tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", tvdbId)), ct),
            "season" => await tvdb.GetAsync("tvdb", "season_extended", ProviderApiClient.Args(("id", tvdbId)), ct),
            "episode" => await tvdb.GetAsync("tvdb", "episode_extended", ProviderApiClient.Args(("id", tvdbId)), ct),
            _ => null,
        };
        if (detail is null)
        {
            return [];
        }
        var output = new List<(string, string, ProviderArtworkCandidate)>();
        if (scope.ScopeId == "episode")
        {
            if (SafeTvdbArtworkUrl(TvdbText(detail, "image")) is { } still)
            {
                output.Add(("Primary", "EpisodeStill", new ProviderArtworkCandidate(
                        "tvdb:episode:" + tvdbId, "TheTVDB", still, string.Empty, null, null)));
            }
            return output;
        }

        var typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var types = await tvdb.GetAsync("tvdb", "artwork_types", null, ct);
        if (types is JsonArray typeArray)
        {
            foreach (var type in typeArray.Where(value => value is not null))
            {
                var id = TvdbText(type, "id");
                var name = TvdbText(type, "name");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    typeNames[id] = name;
                }
            }
        }
        if (detail["artworks"] is JsonArray artworks)
        {
            foreach (var artwork in artworks.Where(value => value is not null))
            {
                var url = SafeTvdbArtworkUrl(TvdbText(artwork, "image"));
                if (url is null)
                {
                    continue;
                }
                var type = TvdbText(artwork, "type") ?? TvdbText(artwork, "imageType") ?? string.Empty;
                var typeName = typeNames.GetValueOrDefault(type)
                    ?? (artwork?["type"] is JsonObject typeObject ? TvdbText(typeObject, "name") : null)
                    ?? type;
                var category = ClassifyTvdbArtwork(scope.ScopeId, typeName);
                if (category is null)
                {
                    continue;
                }
                var thumbnail = SafeTvdbArtworkUrl(TvdbText(artwork, "thumbnail")) ?? string.Empty;
                output.Add((category.Value.Role, category.Value.AssetType, new ProviderArtworkCandidate(
                    "tvdb:artwork:" + (TvdbText(artwork, "id") ?? url), "TheTVDB", url,
                    thumbnail, ParseTvdbNumber(TvdbText(artwork, "width")),
                    ParseTvdbNumber(TvdbText(artwork, "height")), TvdbText(artwork, "language"))));
            }
        }
        if (output.Count == 0 && SafeTvdbArtworkUrl(TvdbText(detail, "image")) is { } primary)
        {
            output.Add(("Primary", scope.ScopeId == "season" ? "SeasonPoster" : "CoverArt",
                    new ProviderArtworkCandidate("tvdb:primary:" + tvdbId, "TheTVDB", primary,
                        string.Empty, null, null)));
        }
        return output;
    }

    private static (string Role, string AssetType)? ClassifyTvdbArtwork(string scope, string name) =>
        TvdbArtworkSelection.Classify(scope, name) switch
        {
            AssetType.Logo => ("Logo", "Logo"),
            AssetType.Background => ("Background", "Background"),
            AssetType.CoverArt => ("Primary", "CoverArt"),
            AssetType.SeasonThumb => ("Background", "SeasonThumb"),
            AssetType.SeasonPoster => ("Primary", "SeasonPoster"),
            _ => null,
        };

    private static string? SafeTvdbArtworkUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !(url.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
                 || url.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return url.ToString();
    }

    private static async Task<ProviderArtworkRefreshDto> RefreshTvdbArtworkAsync(
        EditorScopeResolution scope, string tvdbId, ImageEnrichmentService images,
        CancellationToken ct) => ArtworkScopeService.MapProviderArtworkRefreshResult(
            await images.RefreshTvdbScopeImagesAsync(
                scope.ArtworkOwnerEntityId ?? scope.FieldEntityId,
                scope.ScopeId, tvdbId, ct));
}

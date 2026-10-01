using System.Text.Json.Nodes;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private static async Task<string?> ResolveTvdbArtworkIdAsync(
        EditorScopeResolution scope, ICanonicalValueRepository canonicals, CancellationToken ct)
    {
        var key = scope.ScopeId switch
        {
            "series" => BridgeIdKeys.TvdbId,
            "season" => BridgeIdKeys.TvdbSeasonId,
            "episode" => BridgeIdKeys.TvdbEpisodeId,
            _ => string.Empty,
        };
        if (key.Length == 0 || NormalizeEditorMediaType(scope.MediaType) != "TV") return null;
        var values = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(scope.FieldEntityId, ct));
        var source = GetCanonicalValue(values, MetadataFieldConstants.IdentityProvider);
        if (!string.Equals(source, "tvdb", StringComparison.OrdinalIgnoreCase))
            return null;
        return GetCanonicalValue(values, key);
    }

    private static async Task<IReadOnlyList<(string Role, string AssetType, ProviderArtworkCandidate Candidate)>>
        DiscoverTvdbArtworkAsync(EditorScopeResolution scope, string tvdbId, TvdbRetailClient tvdb,
            CancellationToken ct)
    {
        JsonNode? detail = scope.ScopeId switch
        {
            "series" => await tvdb.GetSeriesAsync(tvdbId, ct),
            "season" => await tvdb.GetSeasonAsync(tvdbId, ct),
            "episode" => await tvdb.GetEpisodeAsync(tvdbId, ct),
            _ => null,
        };
        if (detail is null) return [];
        var output = new List<(string, string, ProviderArtworkCandidate)>();
        if (scope.ScopeId == "episode")
        {
            if (SafeTvdbArtworkUrl(TvdbText(detail, "image")) is { } still)
                output.Add(("Primary", "EpisodeStill", new ProviderArtworkCandidate(
                    "tvdb:episode:" + tvdbId, "TheTVDB", still, string.Empty, null, null)));
            return output;
        }

        var typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var types = await tvdb.GetArtworkTypesAsync(ct);
        if (types is JsonArray typeArray)
        {
            foreach (var type in typeArray.Where(value => value is not null))
            {
                var id = TvdbText(type, "id");
                var name = TvdbText(type, "name");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    typeNames[id] = name;
            }
        }
        if (detail["artworks"] is JsonArray artworks)
        {
            foreach (var artwork in artworks.Where(value => value is not null))
            {
                var url = SafeTvdbArtworkUrl(TvdbText(artwork, "image"));
                if (url is null) continue;
                var type = TvdbText(artwork, "type") ?? TvdbText(artwork, "imageType") ?? string.Empty;
                var typeName = typeNames.GetValueOrDefault(type)
                    ?? (artwork?["type"] is JsonObject typeObject ? TvdbText(typeObject, "name") : null)
                    ?? type;
                var category = ClassifyTvdbArtwork(scope.ScopeId, typeName);
                if (category is null) continue;
                var thumbnail = SafeTvdbArtworkUrl(TvdbText(artwork, "thumbnail")) ?? string.Empty;
                output.Add((category.Value.Role, category.Value.AssetType, new ProviderArtworkCandidate(
                    "tvdb:artwork:" + (TvdbText(artwork, "id") ?? url), "TheTVDB", url,
                    thumbnail, ParseTvdbNumber(TvdbText(artwork, "width")),
                    ParseTvdbNumber(TvdbText(artwork, "height")))));
            }
        }
        if (output.Count == 0 && SafeTvdbArtworkUrl(TvdbText(detail, "image")) is { } primary)
            output.Add(("Primary", scope.ScopeId == "season" ? "SeasonPoster" : "CoverArt",
                new ProviderArtworkCandidate("tvdb:primary:" + tvdbId, "TheTVDB", primary,
                    string.Empty, null, null)));
        return output;
    }

    private static (string Role, string AssetType)? ClassifyTvdbArtwork(string scope, string name)
    {
        var normalized = name.ToLowerInvariant();
        if (scope == "series")
        {
            if (normalized.Contains("logo")) return ("Logo", "Logo");
            if (normalized.Contains("background") || normalized.Contains("fanart")) return ("Background", "Background");
            if (normalized.Contains("poster")) return ("Primary", "CoverArt");
        }
        else if (scope == "season")
        {
            if (normalized.Contains("background")) return ("Background", "SeasonThumb");
            if (normalized.Contains("poster")) return ("Primary", "SeasonPoster");
        }
        return null;
    }

    private static string? SafeTvdbArtworkUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !(url.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
                 || url.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase)))
            return null;
        return url.ToString();
    }

    private static async Task<ProviderArtworkRefreshDto> RefreshTvdbArtworkAsync(
        EditorScopeResolution scope, string tvdbId, ImageEnrichmentService images,
        CancellationToken ct) => ArtworkScopeService.MapProviderArtworkRefreshResult(
            await images.RefreshTvdbScopeImagesAsync(
                scope.ArtworkOwnerEntityId ?? scope.FieldEntityId,
                scope.ScopeId, tvdbId, ct));
}

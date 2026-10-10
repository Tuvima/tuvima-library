using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    private JsonNode? ApplyReleaseSelection(
        JsonNode parentNode,
        ReleaseSelectionConfig config,
        ProviderLookupRequest? request = null)
    {
        var nested = JsonPathEvaluator.Evaluate(parentNode, config.Path);
        if (nested is not JsonArray arr || arr.Count == 0)
        {
            _logger.LogDebug("{Provider}: release selection — no nested array at '{Path}'", Name, config.Path);
            return null;
        }

        // Apply hard filters.
        var candidates = arr
            .Where(n => n is not null && PassesFilters(n, config.Filters))
            .ToList();

        if (request is not null && config.RequestFilters.Count > 0)
        {
            candidates = candidates
                .Where(candidate => PassesRequestFilters(candidate!, config.RequestFilters, request))
                .ToList();
        }

        _logger.LogDebug(
            "{Provider}: release selection — {Total} nested items, {Filtered} pass filters",
            Name, arr.Count, candidates.Count);

        // Fallback: if no candidates match primary filters, try fallback types in order.
        if (candidates.Count == 0 && config.FallbackTypes is { Count: > 0 })
        {
            foreach (var fallbackType in config.FallbackTypes)
            {
                candidates = arr
                    .Where(n => n is not null
                        && MatchesJsonPath(n, "release-group.primary-type", fallbackType)
                        && MatchesJsonPath(n, "status", "Official")
                        && (request is null
                            || PassesRequestFilters(n!, config.RequestFilters, request)))
                    .ToList();

                if (candidates.Count > 0)
                {
                    _logger.LogDebug(
                        "{Provider}: release selection — using fallback type '{Type}' ({Count} candidates)",
                        Name, fallbackType, candidates.Count);
                    break;
                }
            }
        }

        if (candidates.Count == 0)
        {
            _logger.LogDebug("{Provider}: release selection — no candidates after filtering", Name);
            return null;
        }

        // A file's album tag outranks the generic ordering below: when any candidate release
        // carries the tagged album's title, only those releases compete for selection.
        var taggedAlbum = request is null ? null : GetRequestedAlbum(request);
        if (!string.IsNullOrWhiteSpace(taggedAlbum) && candidates.Count > 1)
        {
            var titlePath = GetReleaseAlbumTitlePath();
            var albumMatches = candidates
                .Where(candidate => IsStrongAlbumMatch(
                    taggedAlbum,
                    ExtractFirstString(candidate!, [titlePath])))
                .ToList();

            // A tag with an edition label ("... (Collector's Edition)") names a release whose
            // title does not carry it: MusicBrainz keeps that label in the disambiguation.
            if (albumMatches.Count == 0)
            {
                albumMatches = candidates
                    .Where(candidate => MusicAlbumIdentity.IsSameBaseAlbum(
                        taggedAlbum,
                        ExtractFirstString(candidate!, [titlePath])))
                    .ToList();
            }

            if (albumMatches.Count > 1)
            {
                var tagLabel = MusicAlbumIdentity.EditionLabel(taggedAlbum);
                var disambiguationPath = config.DisambiguationPath;
                if (tagLabel.Length > 0 && !string.IsNullOrWhiteSpace(disambiguationPath))
                {
                    var labelled = albumMatches
                        .Where(candidate => MusicAlbumIdentity.LabelIsCoveredBy(
                            tagLabel,
                            ExtractFirstString(candidate!, [disambiguationPath])))
                        .ToList();
                    if (labelled.Count > 0)
                    {
                        albumMatches = labelled;
                    }
                }
            }

            if (albumMatches.Count > 0)
            {
                _logger.LogDebug(
                    "{Provider}: release selection — {Count} of {Total} candidates match tagged album '{Album}'",
                    Name, albumMatches.Count, candidates.Count, taggedAlbum);
                candidates = albumMatches;
            }
        }

        // Sort by configured sort fields.
        if (config.Sort is { Count: > 0 })
        {
            candidates.Sort((a, b) =>
            {
                foreach (var sort in config.Sort!)
                {
                    var aVal = JsonPathEvaluator.GetStringValue(
                        JsonPathEvaluator.Evaluate(a!, sort.Path)) ?? "";
                    var bVal = JsonPathEvaluator.GetStringValue(
                        JsonPathEvaluator.Evaluate(b!, sort.Path)) ?? "";
                    var cmp = string.Compare(aVal, bVal, StringComparison.OrdinalIgnoreCase);
                    if (sort.Direction.Equals("desc", StringComparison.OrdinalIgnoreCase))
                    {
                        cmp = -cmp;
                    }

                    if (cmp != 0)
                    {
                        return cmp;
                    }
                }
                return 0;
            });
        }

        // Soft preferences: among candidates, prefer those matching prefer conditions.
        if (config.Prefer is { Count: > 0 } && candidates.Count > 1)
        {
            var preferred = candidates.Where(c => PassesFilters(c!, config.Prefer)).ToList();
            if (preferred.Count > 0)
            {
                _logger.LogDebug(
                    "{Provider}: release selection — {Count} candidates match soft preferences",
                    Name, preferred.Count);
                return preferred[0];
            }
        }

        return candidates[0];
    }

    /// <summary>
    /// Claim keys that describe the album a track sits on rather than the track itself.
    /// A provider collection that is not the file's tagged album must not supply them.
    /// </summary>
    private static readonly HashSet<string> TaggedAlbumIdentityClaimKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        MetadataFieldConstants.Album,
        MetadataFieldConstants.Year,
        MetadataFieldConstants.Cover,
        MetadataFieldConstants.CoverUrl,
        MetadataFieldConstants.TrackCount,
        BridgeIdKeys.MusicBrainzReleaseId,
        BridgeIdKeys.MusicBrainzReleaseGroupId,
        BridgeIdKeys.AppleMusicCollectionId,
    };

    /// <summary>
    /// JSON path of the release title, read from the configured release-sourced album mapping.
    /// </summary>
    private string GetReleaseAlbumTitlePath()
        => _config.FieldMappings?
            .FirstOrDefault(mapping =>
                string.Equals(mapping.ClaimKey, MetadataFieldConstants.Album, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mapping.Source, "release", StringComparison.OrdinalIgnoreCase))
            ?.JsonPath
        ?? "title";

    /// <summary>
    /// Keeps a music file's tagged album when the provider's album (the selected release or
    /// collection) is a different album. The recording-level claims still apply to the track,
    /// but the album-level claims — album title, release / release-group / collection ids,
    /// year, cover and track count — are dropped, so nothing downstream (parent-Work claim
    /// routing, synthesised parent title, album manifests) can rename or re-identify the
    /// parent album Work from a non-matching release. A file with no album tag is unaffected.
    /// </summary>
    private IReadOnlyList<ProviderClaim> KeepTaggedAlbumWhenCollectionDiffers(
        ProviderLookupRequest request,
        IReadOnlyList<ProviderClaim> claims)
    {
        if (request.MediaType != MediaType.Music)
        {
            return claims;
        }

        var taggedAlbum = GetRequestedAlbum(request);
        if (string.IsNullOrWhiteSpace(taggedAlbum))
        {
            return claims;
        }

        var providerAlbum = claims.FirstOrDefault(claim =>
            string.Equals(claim.Key, MetadataFieldConstants.Album, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(claim.Value))?.Value;
        if (providerAlbum is null || IsStrongAlbumMatch(taggedAlbum, providerAlbum))
        {
            return claims;
        }

        // The same album under a different edition label: the release's id, release group,
        // cover, year and track count describe this album. Only the title is kept from the tag
        // when it carries a label the release title lacks, so the label is not lost.
        if (MusicAlbumIdentity.IsSameBaseAlbum(taggedAlbum, providerAlbum))
        {
            var tagLabel = MusicAlbumIdentity.EditionLabel(taggedAlbum);
            if (tagLabel.Length > 0
                && !MusicAlbumIdentity.LabelIsCoveredBy(tagLabel, MusicAlbumIdentity.EditionLabel(providerAlbum)))
            {
                _logger.LogInformation(
                    "Kept tagged album title '{Tag}' for {EntityId}; {Provider} release '{Release}' is the same album without that edition label",
                    taggedAlbum, request.EntityId, _config.DisplayName ?? Name, providerAlbum);
                return claims
                    .Where(claim => !string.Equals(claim.Key, MetadataFieldConstants.Album, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return claims;
        }

        _logger.LogInformation(
            "Kept tagged album '{Tag}' for {EntityId}; no {Provider} release of the recording matches",
            taggedAlbum, request.EntityId, _config.DisplayName ?? Name);

        return claims
            .Where(claim => !TaggedAlbumIdentityClaimKeys.Contains(claim.Key))
            .ToList();
    }

    /// <summary>
    /// Returns <c>true</c> when the node passes ALL filters in the list.
    /// An empty or null filter list is a pass.
    /// </summary>
    private static bool PassesFilters(JsonNode node, List<SelectionFilter>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return true;
        }

        foreach (var filter in filters)
        {
            var val = JsonPathEvaluator.Evaluate(node, filter.Path);
            if (val is null)
            {
                return false;
            }

            if (filter.EqualsValue.HasValue)
            {
                var strVal = JsonPathEvaluator.GetStringValue(val) ?? "";
                var expected = filter.EqualsValue.Value;

                switch (expected.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.True:
                        if (!strVal.Equals("true", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        break;
                    case System.Text.Json.JsonValueKind.False:
                        if (!strVal.Equals("false", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        break;
                    case System.Text.Json.JsonValueKind.String:
                        if (!strVal.Equals(expected.GetString(), StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        break;
                    default:
                        if (!strVal.Equals(expected.GetRawText(), StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        break;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Quick helper: checks if a JSON path on a node equals an expected string value.
    /// </summary>
    private static bool MatchesJsonPath(JsonNode node, string path, string expected)
    {
        var val = JsonPathEvaluator.Evaluate(node, path);
        if (val is null)
        {
            return false;
        }

        var str = JsonPathEvaluator.GetStringValue(val);
        return string.Equals(str, expected, StringComparison.OrdinalIgnoreCase);
    }

    // -- Claim extraction ----------------------------------------------------

    /// <summary>
    /// Extracts claims using both the top-level result node (recording) and the selected
    /// nested sub-result (release). Each field mapping's <see cref="FieldMappingConfig.Source"/>
    /// determines which node to extract from. Mappings with a <see cref="FieldMappingConfig.Condition"/>
    /// only emit a claim when the condition is met on the source node.
    /// </summary>
    private IReadOnlyList<ProviderClaim> ExtractClaimsWithRelease(
        JsonNode recordingNode, JsonNode? releaseNode, MediaType mediaType = MediaType.Unknown)
    {
        var mappings = FilterMappingsByMediaType(_config.FieldMappings, mediaType);
        if (mappings.Count == 0)
        {
            return [];
        }

        var claims = new List<ProviderClaim>();

        foreach (var mapping in mappings)
        {
            // Route to the correct source node.
            var sourceNode = mapping.Source?.ToLowerInvariant() switch
            {
                "release" => releaseNode,
                "recording" => recordingNode,
                _ => recordingNode, // default: top-level result
            };

            if (sourceNode is null)
            {
                continue;
            }

            // Check condition before extracting (e.g. only emit cover when artwork exists).
            if (mapping.Condition is not null && !PassesFilters(sourceNode, [mapping.Condition]))
            {
                continue;
            }

            var node = JsonPathEvaluator.Evaluate(sourceNode, mapping.JsonPath);
            if (node is null)
            {
                continue;
            }

            var values = ApplyTransform(node, mapping);
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    claims.Add(new ProviderClaim(mapping.ClaimKey, value, mapping.Confidence));
                }
            }
        }

        _logger.LogDebug(
            "{Provider}: extracted {Count} claims from recording+release nodes",
            Name, claims.Count);

        return claims;
    }

    private IReadOnlyList<ProviderClaim> ExtractClaims(JsonNode resultNode, MediaType mediaType = MediaType.Unknown)
    {
        var mappings = FilterMappingsByMediaType(_config.FieldMappings, mediaType);
        if (mappings.Count == 0)
        {
            return [];
        }

        var claims = new List<ProviderClaim>();

        foreach (var mapping in mappings)
        {
            var node = JsonPathEvaluator.Evaluate(resultNode, mapping.JsonPath);
            if (node is null)
            {
                continue;
            }

            var values = ApplyTransform(node, mapping);
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    claims.Add(new ProviderClaim(mapping.ClaimKey, value, mapping.Confidence));
                }
            }
        }

        return claims;
    }

    /// <summary>
    /// Apply the configured transform to the extracted JSON node.
    /// Returns one or more string values (most transforms return exactly one).
    /// </summary>
    private static IReadOnlyList<string> ApplyTransform(JsonNode node, FieldMappingConfig mapping)
    {
        var transformName = mapping.Transform;
        var args = mapping.TransformArgs;

        // Handle transforms that operate on JSON arrays specially.
        if (JsonPathEvaluator.IsArray(node))
        {
            return HandleArrayTransform(node, transformName, args);
        }

        // Scalar value path.
        var raw = JsonPathEvaluator.GetStringValue(node);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var transformed = !string.IsNullOrEmpty(args)
            ? ValueTransformCatalog.Apply(transformName, raw, args)
            : ValueTransformCatalog.Apply(transformName, raw);

        return string.IsNullOrWhiteSpace(transformed) ? [] : [transformed];
    }

    /// <summary>
    /// Handles transforms that expect a JSON array as input:
    /// <c>prefer_isbn13</c>, <c>array_join</c>, <c>array_nested_join</c>.
    /// </summary>
    private static IReadOnlyList<string> HandleArrayTransform(
        JsonNode node, string? transformName, string? args)
    {
        var values = JsonPathEvaluator.GetArrayValues(node);

        return transformName switch
        {
            // prefer_isbn13 handles both string arrays and object arrays internally.
            "prefer_isbn13" => PreferIsbn13(values, node),
            "array_join" => values.Count > 0 ? [string.Join(args ?? ", ", values)] : [],
            "array_nested_join" => HandleNestedJoin(node, args),
            _ => values.Count > 0 ? [values[0]] : []
        };
    }

    /// <summary>
    /// Prefer a 13-character element (ISBN-13), falling back to first non-empty.
    /// Handles both plain string arrays and object arrays with
    /// <c>type</c>/<c>identifier</c> fields.
    /// </summary>
    private static IReadOnlyList<string> PreferIsbn13(IReadOnlyList<string> values, JsonNode node)
    {
        // Check if this is an array of typed identifier objects.
        if (node is JsonArray arr && arr.Count > 0 && arr[0] is JsonObject)
        {
            string? isbn13 = null;
            string? fallback = null;

            foreach (var element in arr)
            {
                if (element is not JsonObject obj)
                {
                    continue;
                }

                var type = JsonPathEvaluator.GetStringValue(obj["type"]);
                var identifier = JsonPathEvaluator.GetStringValue(obj["identifier"]);
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    continue;
                }

                if (string.Equals(type, "ISBN_13", StringComparison.OrdinalIgnoreCase))
                {
                    isbn13 = identifier;
                }

                fallback ??= identifier;
            }

            var result = isbn13 ?? fallback;
            return string.IsNullOrWhiteSpace(result) ? [] : [result];
        }

        // Plain string array (Open Library style: ["9780441172719", "0441172717"]).
        var isbn13Plain = values.FirstOrDefault(v => v.Length == 13);
        var resultPlain = isbn13Plain ?? values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return string.IsNullOrWhiteSpace(resultPlain) ? [] : [resultPlain];
    }

    /// <summary>
    /// From an array of objects, extract a named field from each element and join.
    /// Args = the field name to extract (e.g. <c>"name"</c>).
    /// </summary>
    private static IReadOnlyList<string> HandleNestedJoin(JsonNode node, string? fieldName)
    {
        if (node is not JsonArray arr || string.IsNullOrEmpty(fieldName))
        {
            return [];
        }

        var extracted = new List<string>();
        foreach (var element in arr)
        {
            if (element is null)
            {
                continue;
            }

            var child = JsonPathEvaluator.Evaluate(element, fieldName);
            var str = JsonPathEvaluator.GetStringValue(child);
            if (!string.IsNullOrWhiteSpace(str))
            {
                extracted.Add(str);
            }
        }

        return extracted.Count > 0 ? [string.Join(", ", extracted)] : [];
    }

    // -- Required field check ------------------------------------------------

    private static readonly HashSet<string> GenericTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown", "untitled", "no title", "title", "book", "audiobook",
        "track", "album", "episode", "movie", "video"
    };

    private static bool AllRequiredFieldsPresent(
        SearchStrategyConfig strategy, ProviderLookupRequest request)
    {
        foreach (var field in strategy.RequiredFields)
        {
            var value = ResolveRequestField(request, field);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (value.Length < 3 || GenericTerms.Contains(value.Trim()))
            {
                return false;
            }
        }

        if (strategy.Query is not null)
        {
            foreach (var clause in strategy.Query.Clauses.Where(clause => clause.Required))
            {
                var value = ResolveRequestField(request, clause.Value);
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Map a field name string to the corresponding property on the lookup request.
    /// </summary>
    private static string? ResolveRequestField(ProviderLookupRequest request, string fieldName)
    {
        var direct = fieldName.ToLowerInvariant() switch
        {
            "title" => request.Title,
            "author" => request.Author,
            "narrator" => request.Narrator,
            "artist" => request.Artist,
            "album" => request.Album,
            "series" => request.Series,
            BridgeIdKeys.Isbn => request.Isbn,
            BridgeIdKeys.Asin => request.Asin,
            BridgeIdKeys.AppleBooksId => request.AppleBooksId,
            BridgeIdKeys.AudibleId => request.AudibleId,
            BridgeIdKeys.TmdbId => request.TmdbId,
            BridgeIdKeys.ImdbId => request.ImdbId,
            "person_name" => request.PersonName,
            _ => (string?)null
        };

        // In Sequential pipeline mode, check bridge IDs from prior providers
        // when the direct request property is empty.
        if (direct is not null)
        {
            return direct;
        }

        if (request.PriorProviderBridgeIds?.TryGetValue(fieldName.ToLowerInvariant(), out var bridgeValue) == true)
        {
            return bridgeValue;
        }

        // Fall back to the hints dictionary for any remaining fields (year,
        // series_position, etc.) so config-driven URL templates can reference
        // arbitrary claim keys without code changes.
        if (request.Hints?.TryGetValue(fieldName.ToLowerInvariant(), out var hintValue) == true
            && !string.IsNullOrWhiteSpace(hintValue))
        {
            return hintValue;
        }

        return null;
    }

    // -- Media type filtering ------------------------------------------------

    /// <summary>
    /// Filters search strategies by the request's media type.
    /// Strategies with no <c>media_types</c> filter are always included (universal).
    /// Strategies with a <c>media_types</c> list are only included if the request's
    /// media type matches one of the listed values.
    /// </summary>
    private static List<SearchStrategyConfig>? FilterStrategiesByMediaType(
        List<SearchStrategyConfig>? strategies, MediaType mediaType)
    {
        if (strategies is null or { Count: 0 })
        {
            return strategies;
        }

        // Unknown = wildcard — return all strategies.
        if (mediaType == MediaType.Unknown)
        {
            return strategies;
        }

        var mediaTypeStr = mediaType.ToString();
        return strategies
            .Where(s => s.MediaTypes is null or { Count: 0 }
                     || s.MediaTypes.Any(mt =>
                            string.Equals(mt, mediaTypeStr, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Filters field mappings by the request's media type.
    /// Mappings with no <c>media_types</c> filter are always included (universal).
    /// Unknown media type = wildcard (return all).
    /// </summary>
    private static List<FieldMappingConfig> FilterMappingsByMediaType(
        List<FieldMappingConfig>? mappings, MediaType mediaType)
    {
        if (mappings is null or { Count: 0 })
        {
            return [];
        }

        // Unknown = wildcard — return all mappings.
        if (mediaType == MediaType.Unknown)
        {
            return mappings;
        }

        var mediaTypeStr = mediaType.ToString();
        return mappings
            .Where(m => m.MediaTypes is null or { Count: 0 }
                     || m.MediaTypes.Any(mt =>
                            string.Equals(mt, mediaTypeStr, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // -- Helpers --------------------------------------------------------------

    private static HashSet<T> ParseEnumSet<T>(List<string>? values) where T : struct, Enum
    {
        if (values is null or { Count: 0 })
        {
            return [];
        }

        var set = new HashSet<T>();
        foreach (var val in values)
        {
            if (Enum.TryParse<T>(val, ignoreCase: true, out var parsed))
            {
                set.Add(parsed);
            }
        }

        return set;
    }

    // -- Language strategy ---------------------------------------------------

    /// <summary>
    /// Resolves the effective language for API queries based on the provider's language strategy.
    /// <list type="bullet">
    ///   <item><see cref="LanguageStrategy.Source"/>: always English (provider has poor localization).</item>
    ///   <item><see cref="LanguageStrategy.Localized"/>: use the request's metadata language.</item>
    ///   <item><see cref="LanguageStrategy.Both"/>: primary pass uses metadata language, fallback to English
    ///         is handled by the caller after the primary pass returns empty.</item>
    /// </list>
    /// </summary>
    private string ResolveEffectiveLanguage(ProviderLookupRequest request) =>
        _config.LanguageStrategy switch
        {
            LanguageStrategy.Localized => NormalizeLanguagePart(request.FileLanguage ?? request.Language, "en"),
            LanguageStrategy.Both => NormalizeLanguagePart(request.FileLanguage ?? request.Language, "en"),
            _ => "en",             // Source: always English
        };

    /// <summary>
    /// Creates a shallow copy of a <see cref="ProviderLookupRequest"/> with a different language.
    /// Required because <see cref="ProviderLookupRequest"/> is a sealed class (not a record),
    /// so the <c>with</c> expression is unavailable.
    /// </summary>
    private static ProviderLookupRequest CloneRequestWithLanguage(ProviderLookupRequest source, string language) =>
        CloneRequestWithLocale(source, language, source.Country);

    private static ProviderLookupRequest CloneRequestWithLocale(
        ProviderLookupRequest source,
        string language,
        string country) =>
        new()
        {
            EntityId = source.EntityId,
            EntityType = source.EntityType,
            MediaType = source.MediaType,
            Title = source.Title,
            Author = source.Author,
            Year = source.Year,
            Narrator = source.Narrator,
            ShowName = source.ShowName,
            Album = source.Album,
            Artist = source.Artist,
            Director = source.Director,
            Composer = source.Composer,
            SeasonNumber = source.SeasonNumber,
            EpisodeNumber = source.EpisodeNumber,
            TrackNumber = source.TrackNumber,
            Series = source.Series,
            Genre = source.Genre,
            Asin = source.Asin,
            Isbn = source.Isbn,
            AppleBooksId = source.AppleBooksId,
            AudibleId = source.AudibleId,
            TmdbId = source.TmdbId,
            ImdbId = source.ImdbId,
            PersonName = source.PersonName,
            PersonRole = source.PersonRole,
            PreResolvedQid = source.PreResolvedQid,
            Hints = source.Hints,
            PriorProviderBridgeIds = source.PriorProviderBridgeIds,
            BaseUrl = source.BaseUrl,
            SparqlBaseUrl = source.SparqlBaseUrl,
            Language = language,
            FileLanguage = source.FileLanguage,
            Country = country,
            HydrationPass = source.HydrationPass,
        };

    private sealed record ComicVineVolumeFacts(
        int? IssueCount,
        int? StartYear,
        string? Publisher);

    private sealed record ComicIssueCandidate(
        JsonNode Node,
        double BaseScore,
        int? CandidateYear,
        string? VolumeId,
        int? VolumeStartYear,
        int? VolumeIssueCount,
        string? Publisher)
    {
        public double Score => BaseScore;
    }

    private sealed record ComicVineVolumeSearchCandidate(
        string VolumeId,
        double Score,
        int? IssueCount,
        int? StartYear,
        string? Publisher);

    private sealed record TmdbCollectionPart(
        string Id,
        string Title,
        DateOnly? ReleaseDate);
}

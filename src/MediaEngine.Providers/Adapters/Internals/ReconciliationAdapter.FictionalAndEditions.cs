using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ReconciliationAdapter
{
    private async Task<IReadOnlyList<ProviderClaim>> FetchFictionalEntityAsync(
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var hints = request.Hints ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var qid = request.PreResolvedQid
            ?? hints.GetValueOrDefault(BridgeIdKeys.WikidataQid);

        if (string.IsNullOrWhiteSpace(qid))
        {
            _logger.LogWarning(
                "{Provider}: fictional entity fetch requested without a resolved QID for entity {EntityId}",
                Name,
                request.EntityId);
            return [];
        }

        var entitySubType = hints.GetValueOrDefault("entity_sub_type")
            ?? request.EntityType switch
            {
                EntityType.Location => "Location",
                EntityType.Organization => "Organization",
                EntityType.Event => "Event",
                EntityType.Object => "Object",
                _ => "Character",
            };

        return await LookupFictionalEntityAsync(qid, entitySubType, ct).ConfigureAwait(false);
    }

    private static int? GetCandidateYear(
        IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>? props)
    {
        if (props is null || !props.TryGetValue("P577", out var publicationClaims))
        {
            return null;
        }

        return publicationClaims
            .Select(claim => ParseComparableYear(claim.Value?.RawValue))
            .FirstOrDefault(year => year.HasValue);
    }

    private static bool IsResolvedYearCompatible(
        string? yearHint,
        IReadOnlyList<ProviderClaim> claims,
        MediaType mediaType)
    {
        var hintYear = ParseComparableYear(yearHint);
        var resolvedYear = ParseComparableYear(GetResolvedClaimsYear(claims));
        if (!hintYear.HasValue || !resolvedYear.HasValue)
        {
            return true;
        }

        var maxDifference = mediaType is MediaType.Movies or MediaType.TV ? 1 : 2;
        return Math.Abs(hintYear.Value - resolvedYear.Value) <= maxDifference;
    }

    private static string? GetResolvedClaimsYear(IReadOnlyList<ProviderClaim> claims) =>
        claims.FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.OriginalReleaseYear, StringComparison.OrdinalIgnoreCase))?.Value
        ?? claims.FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Year, StringComparison.OrdinalIgnoreCase))?.Value;

    private static int? ParseComparableYear(string? value)
    {
        var extracted = ExtractYear(value ?? string.Empty);
        return int.TryParse(extracted, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Fetch Wikidata properties for a fictional entity (Character, Location, Organization).
    /// Used by Stage 3 Universe Enrichment — the entity QID is already known,
    /// so no reconciliation is needed, only data extension.
    /// </summary>
    /// <param name="qid">The fictional entity's Wikidata QID (e.g. "Q937618" for Paul Atreides).</param>
    /// <param name="entitySubType">
    /// One of <c>"Character"</c>, <c>"Location"</c>, or <c>"Organization"</c>.
    /// Determines which property group to fetch.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Claims extracted from the entity's Wikidata properties.</returns>
    public async Task<IReadOnlyList<ProviderClaim>> LookupFictionalEntityAsync(
        string qid, string entitySubType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySubType);

        if (_reconciler is null)
        {
            _logger.LogWarning("WikidataReconciler not available — skipping fictional entity lookup for {Qid}", qid);
            return [];
        }

        // Select property group based on entity sub-type.
        var propGroup = GetFictionalEntityPropertyGroup(entitySubType);

        if (propGroup is null || propGroup.Core.Count == 0)
        {
            _logger.LogDebug("No properties configured for entity sub-type {SubType} — skipping {Qid}", entitySubType, qid);
            return [];
        }

        // Build property list (core + bridges if any).
        var language = _configLoader?.LoadCore().Language.Metadata ?? "en";
        var props = new List<string>(propGroup.Core);
        if (propGroup.Bridges.Count > 0)
        {
            props.AddRange(propGroup.Bridges);
        }

        props.Add($"L{language}");  // Label in metadata language
        props.Add($"D{language}");  // Description in metadata language

        // Fetch properties via wbgetentities.
        var extResult = await ExtendAsync([qid], props, ct);

        if (!extResult.TryGetValue(qid, out var entityProps) || entityProps.Count == 0)
        {
            _logger.LogDebug("No properties returned for fictional entity {Qid} ({SubType})", qid, entitySubType);
            return [];
        }

        // Convert to provider claims using existing helper.
        var claims = ExtensionToClaimsCore(
            qid,
            entityProps,
            _config.DataExtension.PropertyLabels,
            isWork: false,
            castMemberLimit: 0,
            metadataLanguage: language,
            editionScopedDates: false,
            imageIsHeadshot: false).ToList();

        _logger.LogDebug("Fictional entity {Qid} ({SubType}): {Count} claims extracted", qid, entitySubType, claims.Count);
        return claims;
    }

    /// <inheritdoc />
    public async Task<FictionalEntityGraphEvidence?> FetchFictionalEntityGraphEvidenceAsync(
        string qid,
        string entitySubType,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitySubType);

        if (_reconciler is null)
        {
            return null;
        }

        var propertyGroup = GetFictionalEntityPropertyGroup(entitySubType);
        if (propertyGroup is null || propertyGroup.Core.Count == 0)
        {
            return null;
        }

        try
        {
            var language = _configLoader?.LoadCore().Language.Metadata ?? "en";
            var entities = await _reconciler.GetEntitiesAsync([qid], language, ct).ConfigureAwait(false);
            return !entities.TryGetValue(qid, out var entity)
                ? null
                : BuildFictionalEntityGraphEvidence(entity, propertyGroup, _config.DataExtension.PropertyLabels);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: graph evidence fetch failed for fictional entity {Qid}", Name, qid);
            return null;
        }
    }

    internal static FictionalEntityGraphEvidence BuildFictionalEntityGraphEvidence(
        WikidataEntityInfo entity,
        DataExtensionPropertyGroup propertyGroup,
        IReadOnlyDictionary<string, string> propertyLabels)
    {
        var configuredProperties = propertyGroup.Core
            .Concat(propertyGroup.Bridges)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statements = new List<FictionalEntityRelationshipStatement>();
        var scalarStatements = new List<FictionalEntityScalarStatement>();
        string? universeQid = null;
        string? universeLabel = null;

        foreach (var (propertyId, claims) in entity.Claims)
        {
            if (!configuredProperties.Contains(propertyId)
                || !propertyLabels.TryGetValue(propertyId, out var claimKey))
            {
                continue;
            }

            foreach (var claim in claims.Where(claim =>
                         !string.Equals(claim.Rank, "deprecated", StringComparison.OrdinalIgnoreCase)
                         && claim.Value is not null))
            {
                var target = claim.Value!;
                if (string.Equals(propertyId, "P1080", StringComparison.OrdinalIgnoreCase))
                {
                    universeQid ??= target.EntityId;
                    universeLabel ??= target.EntityLabel;
                    continue;
                }

                var qualifiers = claim.Qualifiers
                    .SelectMany(pair => pair.Value.Select(value => new FictionalEntityStatementQualifier(
                        pair.Key,
                        GetGraphValue(value),
                        GetGraphValueKind(value))))
                    .Where(qualifier => !string.IsNullOrWhiteSpace(qualifier.Value))
                    .ToList();

                if (string.IsNullOrWhiteSpace(target.EntityId))
                {
                    if (propertyId is "P585" or "P580" or "P582" or "P4895")
                    {
                        scalarStatements.Add(new FictionalEntityScalarStatement(
                            ClaimKey: claimKey,
                            Value: GetGraphValue(target),
                            ValueKind: GetGraphValueKind(target),
                            Confidence: 0.9,
                            Qualifiers: qualifiers));
                    }

                    continue;
                }

                statements.Add(new FictionalEntityRelationshipStatement(
                    ClaimKey: $"{claimKey}_qid",
                    TargetQid: target.EntityId!,
                    TargetLabel: target.EntityLabel,
                    Confidence: 0.9,
                    Qualifiers: qualifiers));
            }
        }

        return new FictionalEntityGraphEvidence(
            SourceProvider: "wikidata",
            Provenance: "Wikidata",
            NarrativeUniverseQid: universeQid,
            NarrativeUniverseLabel: universeLabel,
            Statements: statements,
            ScalarStatements: scalarStatements);
    }

    private DataExtensionPropertyGroup? GetFictionalEntityPropertyGroup(string entitySubType) =>
        entitySubType switch
        {
            "Character" => _config.DataExtension.CharacterProperties,
            "Location" => _config.DataExtension.LocationProperties,
            "Organization" => _config.DataExtension.OrganizationProperties,
            "Event" => _config.DataExtension.EventProperties,
            "Object" => _config.DataExtension.ObjectProperties,
            _ => null,
        };

    private static string GetGraphValue(WikidataValue value) =>
        value.Kind == WikidataValueKind.EntityId
            ? value.EntityId ?? value.RawValue
            : value.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? value.RawValue;

    private static string GetGraphValueKind(WikidataValue value) => value.Kind switch
    {
        WikidataValueKind.EntityId => "WikidataQid",
        WikidataValueKind.Time => "Time",
        WikidataValueKind.Quantity => "Quantity",
        WikidataValueKind.GlobeCoordinate => "Coordinate",
        WikidataValueKind.MonolingualText => "MonolingualText",
        _ => "Text",
    };

    internal static TvManifestProjection BuildTvManifestProjection(
        ChildEntityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var seasonCount = Math.Min(manifest.PrimaryCount, manifest.Children.Count);
        var seasons = manifest.Children.Take(seasonCount).ToList();
        var episodes = manifest.Children.Skip(seasonCount).ToList();

        var seasonNodes = new List<object>(seasons.Count);
        var assignedEpisodeQids = new HashSet<string>(StringComparer.Ordinal);
        var totalEpisodes = 0;

        for (var seasonIndex = 0; seasonIndex < seasons.Count; seasonIndex++)
        {
            var season = seasons[seasonIndex];
            var seasonNumber = season.Ordinal;
            var episodeNodes = new List<object>();

            foreach (var episode in episodes.Where(e => e.Parent == seasonNumber))
            {
                assignedEpisodeQids.Add(episode.Qid);
                var episodeNumber = episode.Ordinal;
                episodeNodes.Add(new
                {
                    qid = episode.Qid,
                    title = episode.Title,
                    ordinal = episodeNumber,
                    episode_number = episodeNumber,
                    air_date = episode.ReleaseDate?.ToString("yyyy-MM-dd"),
                    duration_minutes = episode.Duration is { } d ? (int?)Math.Round(d.TotalMinutes) : null,
                    director = episode.Creators?.GetValueOrDefault("Director"),
                });
            }

            totalEpisodes += episodeNodes.Count;
            seasonNodes.Add(new
            {
                qid = season.Qid,
                label = season.Title,
                ordinal = seasonNumber,
                season_number = seasonNumber,
                episodes = episodeNodes,
            });
        }

        var unassigned = episodes
            .Where(e => !assignedEpisodeQids.Contains(e.Qid))
            .ToList();
        if (unassigned.Count > 0)
        {
            var unassignedNodes = new List<object>(unassigned.Count);
            foreach (var episode in unassigned)
            {
                var fallbackOrdinal = episode.Ordinal;
                unassignedNodes.Add(new
                {
                    qid = episode.Qid,
                    title = episode.Title,
                    ordinal = fallbackOrdinal,
                    episode_number = fallbackOrdinal,
                    air_date = episode.ReleaseDate?.ToString("yyyy-MM-dd"),
                    duration_minutes = episode.Duration is { } d ? (int?)Math.Round(d.TotalMinutes) : null,
                    director = episode.Creators?.GetValueOrDefault("Director"),
                });
            }

            totalEpisodes += unassignedNodes.Count;
            seasonNodes.Add(new
            {
                qid = (string?)null,
                label = "Unassigned",
                ordinal = (int?)null,
                season_number = (int?)null,
                episodes = unassignedNodes,
            });
        }

        return new TvManifestProjection(
            seasonCount,
            totalEpisodes,
            unassigned.Count,
            JsonSerializer.Serialize(new { seasons = seasonNodes }));
    }

    private static string? GetChildDescription(
        string qid,
        IReadOnlyDictionary<string, string>? descriptions)
    {
        if (descriptions is null || string.IsNullOrWhiteSpace(qid))
        {
            return null;
        }

        return descriptions.TryGetValue(qid, out var description)
            ? description
            : null;
    }

    internal sealed record TvManifestProjection(
        int SeasonCount,
        int EpisodeCount,
        int UnassignedEpisodeCount,
        string JsonBlob);

    internal static IReadOnlyList<ProviderClaim> BuildResolvedAuthorPseudonymClaims(
        AuthorResolutionResult authorResolution)
    {
        ArgumentNullException.ThrowIfNull(authorResolution);

        var claims = new List<ProviderClaim>();
        foreach (var resolved in authorResolution.Authors)
        {
            if (string.IsNullOrWhiteSpace(resolved.Qid))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(resolved.RealNameQid))
            {
                claims.Add(new ProviderClaim(
                    BridgeIdKeys.AuthorRealNameQid,
                    resolved.RealNameQid,
                    ClaimConfidence.WikidataProperty));
            }

            if (resolved.Pseudonyms is { Count: > 0 })
            {
                foreach (var penName in resolved.Pseudonyms)
                {
                    if (string.IsNullOrWhiteSpace(penName))
                    {
                        continue;
                    }

                    claims.Add(new ProviderClaim(
                        BridgeIdKeys.AuthorPseudonym,
                        penName,
                        ClaimConfidence.WikidataProperty));
                }
            }

            if (resolved.RealAuthors is { Count: > 0 })
            {
                var penName = StringHelpers.FirstNonBlankOr(string.Empty, resolved.CanonicalName, resolved.OriginalName, resolved.Qid);
                claims.Add(new ProviderClaim("author_qid", $"{resolved.Qid}::{penName}", ClaimConfidence.PenName));
                claims.Add(new ProviderClaim("author_is_collective_pseudonym", "true", ClaimConfidence.CollectivePseudonym));

                foreach (var realAuthor in resolved.RealAuthors)
                {
                    if (string.IsNullOrWhiteSpace(realAuthor.Qid))
                    {
                        continue;
                    }

                    var realName = StringHelpers.FirstNonBlankOr(string.Empty, realAuthor.CanonicalName, realAuthor.Qid);
                    claims.Add(new ProviderClaim(
                        "collective_members_qid",
                        $"{realAuthor.Qid}::{realName}",
                        ClaimConfidence.WikidataProperty));
                }
            }
        }

        return claims;
    }

    /// <summary>
    /// Resolves and downloads a person headshot from Wikimedia Commons to a local folder.
    /// The filename stored in Wikidata P18 is appended to the Commons Special:FilePath URL.
    /// </summary>
    /// <param name="commonsFilename">The filename value from Wikidata P18 (e.g. "Frank_Herbert.jpg").</param>
    /// <param name="personFolderPath">Local directory to write the downloaded image.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The local file path if downloaded successfully, or <c>null</c> on failure.</returns>
    public async Task<string?> ResolveAndDownloadPersonImageAsync(
        string commonsFilename,
        string personFolderPath,
        CancellationToken ct = default)
    {
        return await _commonsImageResolver.ResolveAndDownloadPersonImageAsync(
            Name,
            commonsFilename,
            personFolderPath,
            ct).ConfigureAwait(false);
    }

    private static string? GetEditionNarrator(
        EditionInfo edition,
        IReadOnlyDictionary<string, string?>? resolvedLabels = null) =>
        GetEditionClaimLabel(edition, "P175", resolvedLabels);

    private static string? GetEditionClaimValue(EditionInfo edition, string propertyId)
    {
        if (!edition.Claims.TryGetValue(propertyId, out var claims) || claims.Count == 0)
        {
            return null;
        }

        return claims[0].Value?.RawValue;
    }

    private static string? GetEditionClaimLabel(
        EditionInfo edition,
        string propertyId,
        IReadOnlyDictionary<string, string?>? resolvedLabels = null)
    {
        if (!edition.Claims.TryGetValue(propertyId, out var claims) || claims.Count == 0)
        {
            return null;
        }

        var value = claims[0].Value;
        if (!string.IsNullOrWhiteSpace(value?.EntityLabel))
        {
            return value.EntityLabel;
        }

        var entityId = value?.EntityId;
        if (string.IsNullOrWhiteSpace(entityId) && IsExactQid(value?.RawValue))
        {
            entityId = value!.RawValue;
        }

        if (!string.IsNullOrWhiteSpace(entityId)
            && resolvedLabels?.TryGetValue(entityId, out var resolvedLabel) == true
            && !string.IsNullOrWhiteSpace(resolvedLabel))
        {
            return resolvedLabel;
        }

        return IsExactQid(value?.RawValue) || IsExactQid(entityId)
            ? null
            : value?.RawValue ?? entityId;
    }

    private static string? GetEditionClaimEntityId(EditionInfo edition, string propertyId)
    {
        if (!edition.Claims.TryGetValue(propertyId, out var claims) || claims.Count == 0)
        {
            return null;
        }

        var value = claims[0].Value;
        if (IsExactQid(value?.EntityId))
        {
            return value!.EntityId;
        }

        return IsExactQid(value?.RawValue) ? value!.RawValue : null;
    }

    private static bool IsExactQid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(value.Trim(), @"^Q\d+$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Discovers audiobook editions of a work via P747 (has_edition_or_translation)
    /// followed by P31 filtering to retain only audiobook-class items.
    /// When <paramref name="narratorHint"/> is provided and multiple editions exist,
    /// results are ranked by fuzzy narrator match (best match first).
    /// </summary>
    /// <param name="workQid">The Wikidata Q-identifier of the work.</param>
    /// <param name="narratorHint">Optional narrator name for disambiguation ranking.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<AudiobookEditionData>> DiscoverAudiobookEditionsAsync(
        string workQid,
        string? narratorHint = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workQid) || _reconciler is null)
        {
            return [];
        }

        try
        {
            var audiobookClasses = GetAudiobookEditionClasses();

            var language = _configLoader?.LoadCore().Language.Metadata ?? "en";
            var editions = await _reconciler.GetEditionsAsync(
                workQid, audiobookClasses, language, ct).ConfigureAwait(false);

            if (editions.Count == 0)
            {
                return [];
            }

            var referencedQids = editions
                .SelectMany(edition => new[]
                {
                    GetEditionClaimEntityId(edition, "P175"),
                    GetEditionClaimEntityId(edition, "P123"),
                })
                .Where(qid => !string.IsNullOrWhiteSpace(qid))
                .Select(qid => qid!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            IReadOnlyDictionary<string, string?> resolvedLabels = new Dictionary<string, string?>();
            if (referencedQids.Count > 0)
            {
                resolvedLabels = await _reconciler.Labels
                    .GetBatchAsync(referencedQids, language, withFallbackLanguage: true, ct)
                    .ConfigureAwait(false);
            }

            var results = editions.Select(e =>
            {
                var narrator = GetEditionNarrator(e, resolvedLabels);
                var duration = GetEditionClaimValue(e, "P2047");
                var asin = GetEditionClaimValue(e, "P5749");
                var publisher = GetEditionClaimLabel(e, "P123", resolvedLabels);
                return new AudiobookEditionData(e.EntityId, e.Label, narrator, duration, asin, publisher);
            }).ToList();

            if (!string.IsNullOrWhiteSpace(narratorHint) && results.Count > 1)
            {
                results = results
                    .OrderByDescending(e => _fuzzy.ComputeTokenSetRatio(narratorHint, e.Narrator ?? ""))
                    .ToList();
            }

            _logger.LogDebug("{Provider}: discovered {Count} audiobook edition(s) for {QID}", Name, results.Count, workQid);
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: DiscoverAudiobookEditionsAsync failed for {QID}", Name, workQid);
            return [];
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Library-backed Wikidata identity resolution.
    //
    // The public ResolveAsync / ResolveBatchAsync methods on this adapter
    // dispatch into this region. Bridge and music resolution are
    // delegated to Tuvima.Wikidata.BridgeResolutionService; we add a follow-up Data
    // Extension call to populate ProviderClaim payloads (BridgeResolutionResult
    // deliberately does not carry claims). The hand-rolled ResolveBridgeAsync
    // / ResolveMusicAlbumAsync / ResolveByTextAsync helpers were removed in
    // Commit F2 of the adapter slimdown remediation; see commit history for
    // the rationale and the parity baseline at tests/fixtures/stage2-baseline-v2.json.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wikidata bridge identifier P-codes collected via Data Extension after
    /// every successful Wikidata bridge resolution. The collected values populate
    /// <see cref="WikidataResolveResult.CollectedBridgeIds"/> for downstream
    /// consumers (most notably <c>WikidataBridgeWorker</c>).
    /// </summary>
    private static readonly IReadOnlyList<string> BridgeResolutionPCodes =
    [
        "P31",   // instance_of — for post-resolution media-type validation
        "P175",  // performer — validate album identity before accepting claims
        "P212",  // ISBN-13
        "P957",  // ISBN-10
        "P6395", // Apple Books ID
        "P5749", // ASIN
        "P4947", // TMDB movie ID
        "P4983", // TMDB TV series ID
        "P345",  // IMDb ID
        "P4835", // TheTVDB series ID
        "P7043", // TheTVDB episode ID
        "P9586", // Apple TV movie ID
        "P9751", // Apple TV show ID
        "P9750", // Apple TV episode ID
        "P6381", // iTunes TV season ID
        "P6398", // iTunes movie ID
        "P2281", // Apple Music album ID
        "P2850", // Apple Music artist ID
        "P10110", // Apple Music track ID
        "P1243", // ISRC
        "P577",  // publication/release date (kept work- or edition-scoped)

        "P434",  // MusicBrainz artist ID
        "P435",  // MusicBrainz work ID
        "P436",  // MusicBrainz release group ID
        "P5813", // MusicBrainz release ID
        "P4404", // MusicBrainz recording ID
        "P5905", // Comic Vine ID
        "P2969", // Goodreads ID
        "P648",  // Open Library ID
        "P1085", // LibraryThing ID
    ];

    /// <summary>
    /// Builds the bridge resolution request for a
    /// <see cref="WikidataResolveRequest"/>. Returns <c>null</c> when none of
    /// music, bridge, or text resolution is applicable, in which case
    /// the caller leaves the result as <see cref="WikidataResolveResult.NotFound"/>.
    /// </summary>
    private BridgeResolutionRequest? BuildBridgeResolutionRequest(WikidataResolveRequest r)
    {
        var language = ResolveSearchLanguage(TryGetMetadataLanguage(), r.FileLanguage);
        var realBridgeIds = r.BridgeIds?
            .Where(kvp => !kvp.Key.StartsWith('_') && !string.IsNullOrWhiteSpace(kvp.Value))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── Music branch — album-aware grouping ─────────────────────────────
        var title = r.MediaType switch
        {
            MediaType.Music when string.Equals(r.ResolutionScope, "MusicAlbum", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(r.AlbumTitle) => r.AlbumTitle,
            MediaType.Comics when !string.IsNullOrWhiteSpace(r.SeriesTitle) => r.SeriesTitle,
            _ => r.Title,
        };

        // ── Bridge branch — at least one real (non-sentinel) external ID ────
        // Sentinel keys (those starting with '_') are stripped here so the
        // library's strict bridge resolver doesn't trip on them.
        if (realBridgeIds.Count == 0 && !CanUseConstrainedTextFallback(r, title))
        {
            return null;
        }

        // ── Text fallback — only when title and a known media type are present ─
        return new BridgeResolutionRequest
        {
            CorrelationKey = r.CorrelationKey,
            MediaKind = ToBridgeMediaKind(r.MediaType, r, realBridgeIds.Count),
            BridgeIds = realBridgeIds,
            CustomWikidataProperties = r.WikidataProperties,
            // Version 3.9.1 uses any supplied title to initiate its own fallback.
            // Keep title hints locally for acceptance, but do not authorize a
            // dependency search that this scope explicitly prohibits.
            Title = r.AllowConstrainedTextFallback ? title : null,
            Creator = r.Artist ?? r.Author,
            Year = int.TryParse(r.Year, out var parsedYear) ? parsedYear : null,
            SeriesTitle = GetSeriesHint(r),
            SeasonNumber = r.SeasonNumber,
            EpisodeNumber = r.EpisodeNumber,
            IssueNumber = r.IssueNumber,
            Language = language,
            RollupTarget = ToBridgeRollupTarget(r)
        };
    }

    private static string? GetSeriesHint(WikidataResolveRequest request)
        => !string.IsNullOrWhiteSpace(request.SeriesTitle)
            ? request.SeriesTitle
            : request.MediaType is MediaType.Books or MediaType.Audiobooks
                ? request.AlbumTitle
                : null;

    private static bool HasRealBridgeIds(WikidataResolveRequest request)
        => request.BridgeIds?.Any(kvp =>
            !kvp.Key.StartsWith('_')
            && !string.IsNullOrWhiteSpace(kvp.Value)) == true;

    private static BridgeResolutionRequest? BuildConstrainedTextFallbackRequest(
        WikidataResolveRequest request,
        Func<WikidataResolveRequest, BridgeResolutionRequest?> build)
    {
        if (!HasRealBridgeIds(request))
        {
            return null;
        }

        var textOnly = new WikidataResolveRequest
        {
            CorrelationKey = request.CorrelationKey,
            MediaType = request.MediaType,
            ResolutionScope = request.ResolutionScope,
            Strategy = request.Strategy,
            BridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            WikidataProperties = request.WikidataProperties,
            IsEditionAware = request.IsEditionAware,
            AllowConstrainedTextFallback = request.AllowConstrainedTextFallback,
            AlbumTitle = request.AlbumTitle,
            Artist = request.Artist,
            Title = request.Title,
            Author = request.Author,
            Year = request.Year,
            FileLanguage = request.FileLanguage,
            SeriesTitle = request.SeriesTitle,
            SeasonNumber = request.SeasonNumber,
            EpisodeNumber = request.EpisodeNumber,
            IssueNumber = request.IssueNumber,
        };

        return build(textOnly);
    }

    private static bool CanUseConstrainedTextFallback(
        WikidataResolveRequest request,
        string? title)
    {
        if (!request.AllowConstrainedTextFallback)
        {
            return false;
        }

        if (request.MediaType is not (MediaType.Books or MediaType.Audiobooks))
        {
            return request.MediaType == MediaType.Comics
                && !string.IsNullOrWhiteSpace(request.SeriesTitle)
                && !string.IsNullOrWhiteSpace(title);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(request.Author)
               || !string.IsNullOrWhiteSpace(request.Artist)
               || !string.IsNullOrWhiteSpace(request.SeriesTitle)
               || !string.IsNullOrWhiteSpace(request.AlbumTitle);
    }

    /// <summary>
    /// Translates the app media type and available hints into the bridge media kind
    /// used by Tuvima.Wikidata for property selection and ranking.
    /// </summary>
    private static BridgeMediaKind ToBridgeMediaKind(MediaType mediaType, WikidataResolveRequest request, int realBridgeIdCount) => mediaType switch
    {
        MediaType.Books => BridgeMediaKind.Book,
        MediaType.Audiobooks when request.AllowConstrainedTextFallback && realBridgeIdCount == 0 => BridgeMediaKind.Book,
        MediaType.Audiobooks => BridgeMediaKind.Audiobook,
        MediaType.Movies => BridgeMediaKind.Movie,
        // Wikidata identity is deliberately resolved at the root show. Episode
        // coordinates remain provider-local facts used by TMDB and subtitles.
        MediaType.TV => BridgeMediaKind.TvSeries,
        MediaType.Comics => !string.IsNullOrWhiteSpace(request.SeriesTitle)
            ? BridgeMediaKind.ComicSeries
            : string.IsNullOrWhiteSpace(request.IssueNumber)
            ? BridgeMediaKind.ComicSeries
            : BridgeMediaKind.ComicIssue,
        MediaType.Music when string.Equals(request.ResolutionScope, "MusicAlbum", StringComparison.OrdinalIgnoreCase) => BridgeMediaKind.MusicAlbum,
        // Music Wikidata identity is deliberately resolved at the album/release
        // group. Track/recording IDs remain available for playback and lyrics.
        MediaType.Music => BridgeMediaKind.MusicAlbum,
        _ => BridgeMediaKind.Unknown
    };

    private static BridgeRollupTarget ToBridgeRollupTarget(WikidataResolveRequest request)
    {
        if (request.AllowConstrainedTextFallback
            && request.MediaType is MediaType.Books or MediaType.Audiobooks)
        {
            return BridgeRollupTarget.ReturnWorkAndEdition;
        }

        if (!request.IsEditionAware)
        {
            return BridgeRollupTarget.ReturnWorkAndEdition;
        }

        return request.MediaType == MediaType.Audiobooks
            ? BridgeRollupTarget.PreferEdition
            : BridgeRollupTarget.ReturnWorkAndEdition;
    }

    /// <summary>
    /// Returns the per-media-type CirrusSearch P31 allow-list from
    /// <c>instance_of_classes</c> in the reconciliation provider config.
    /// Returns an empty list when the media type has no configured classes
    /// (in which case text fallback is skipped entirely for this media type).
    /// </summary>
    private IReadOnlyList<string> GetCirrusTypesForMediaType(MediaType mediaType)
    {
        // For edition-aware media types (Books, Audiobooks, Music), use the narrow
        // work_classes from edition_pivot — these are work-level P31 types only.
        // The broad instance_of_classes list includes edition types, series types,
        // and other adjacent classes that cause CirrusSearch to return false positives
        // (e.g. a film adaptation instead of the novel).
        var config = CurrentConfig;
        var pivotRule = config.GetEditionPivotConfiguration().GetRuleFor(mediaType);
        if (pivotRule is not null && pivotRule.WorkClasses.Count > 0)
        {
            return pivotRule.WorkClasses;
        }

        // Non-edition-aware types (Movies, TV, Comics) use instance_of_classes.
        var mediaTypeKey = mediaType.ToString();
        if (config.InstanceOfClasses.TryGetValue(mediaTypeKey, out var classes) && classes.Count > 0)
        {
            return classes;
        }

        return [];
    }

    /// <summary>
    /// Returns the audiobook edition P31 classes from the edition_pivot config,
    /// falling back to the instance_of_classes Audiobooks list.
    /// </summary>
    private IReadOnlyList<string> GetAudiobookEditionClasses()
    {
        var config = CurrentConfig;
        var rule = config.GetEditionPivotConfiguration().GetRuleFor(MediaType.Audiobooks);
        if (rule is not null && rule.EditionClasses.Count > 0)
        {
            return rule.EditionClasses;
        }

        // Fallback to instance_of_classes if edition_pivot is not configured.
        return config.InstanceOfClasses.TryGetValue("Audiobooks", out var classes) && classes.Count > 0
            ? classes
            : (IReadOnlyList<string>)["Q122731938", "Q106833962"];
    }

    private static ResolveStrategy MapBridgeResolutionStrategy(BridgeResolutionStrategy m) => m switch
    {
        BridgeResolutionStrategy.BridgeId => ResolveStrategy.BridgeId,
        BridgeResolutionStrategy.TextSearch => ResolveStrategy.TextSearch,
        BridgeResolutionStrategy.NotResolved => ResolveStrategy.NotResolved,
        _ => ResolveStrategy.NotResolved,
    };

    private string? TryGetMetadataLanguage()
    {
        if (_configLoader is null)
        {
            return null;
        }

        try { return _configLoader.LoadCore().Language?.Metadata; }
        catch { return null; }
    }

    /// <summary>
    /// After BridgeResolutionService resolves a QID, fetches its bridge property claims
    /// via Data Extension and produces the same <see cref="ProviderClaim"/> list
    /// + <c>CollectedBridgeIds</c> dictionary that the legacy
    /// <c>ResolveBridgeAsync</c> path produces. BridgeResolutionResult deliberately does
    /// not carry claims, so this follow-up call is required for parity with
    /// the consumer contract (<c>WikidataBridgeWorker.AdditionalClaims</c>).
    /// </summary>
}

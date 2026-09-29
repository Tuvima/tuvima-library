using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Assigns works to ContentGroup collections based on stable shelf identities.
/// QIDs are preferred when Stage 2 has resolved them; provider-backed keys such
/// as TMDB movie collections and TV show IDs keep shelves stable before Wikidata
/// catches up. Broader franchise/universe relationships remain rollup inputs.
///
/// Uses QID/rule-hash lookups to avoid duplicates and
/// <see cref="ICollectionRepository.AssignWorkToCollectionAsync"/> to set the FK.
/// Idempotent — skips works that already have a collection_id.
/// </summary>
public sealed class CollectionAssignmentService
{
    private readonly ICollectionRepository _collectionRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly ICanonicalValueArrayRepository? _arrayRepo;
    private readonly IWorkRepository _workRepo;
    private readonly ILogger<CollectionAssignmentService> _logger;
    private readonly ISeriesManifestRepository? _manifestRepo;

    // Per-shelf semaphores serialise concurrent find-or-create calls so two
    // workers cannot race past lookup and both UpsertAsync the same ContentGroup
    // collection. The service is a singleton, so entries are process-lifetime.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ShelfLocks = new();

    public CollectionAssignmentService(
        ICollectionRepository collectionRepo,
        ICanonicalValueRepository canonicalRepo,
        IWorkRepository workRepo,
        ILogger<CollectionAssignmentService> logger,
        ICanonicalValueArrayRepository? arrayRepo = null,
        ISeriesManifestRepository? manifestRepo = null)
    {
        _collectionRepo = collectionRepo;
        _canonicalRepo = canonicalRepo;
        _arrayRepo = arrayRepo;
        _workRepo = workRepo;
        _logger = logger;
        _manifestRepo = manifestRepo;
    }

    /// <summary>
    /// Finds or creates a ContentGroup collection for the work's parent entity
    /// (album, series, show) and assigns the work to it via collection_id FK.
    /// </summary>
    public async Task<CollectionAssignmentResult> AssignAsync(Guid entityId, CancellationToken ct = default)
    {
        var lineage = await _workRepo.GetLineageByAssetAsync(entityId, ct);

        Guid? workId = lineage?.WorkId;
        if (workId is null)
        {
            workId = await _collectionRepo.GetWorkIdByMediaAssetAsync(entityId, ct);
        }

        if (workId is null)
        {
            _logger.LogDebug("CollectionAssignment: no work found for entity {EntityId}", entityId);
            return new CollectionAssignmentResult(
                CollectionAssignmentOutcome.SkippedNoWork,
                entityId,
                Message: "No work found for entity.");
        }

        var existingCollectionId = await _collectionRepo.GetCollectionIdByWorkIdAsync(workId.Value, ct);

        // Stage 3 hierarchy claims are parent-scoped, so TV/music/book items need
        // the root work's canonicals instead of the file asset's canonicals.
        var canonicalEntityId = lineage?.TargetForParentScope ?? workId.Value;
        var canonicals = await _canonicalRepo.GetByEntityAsync(canonicalEntityId, ct);
        var lookup = canonicals.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        if (canonicalEntityId != entityId)
        {
            var assetCanonicals = await _canonicalRepo.GetByEntityAsync(entityId, ct);
            foreach (var value in assetCanonicals)
            {
                lookup.TryAdd(value.Key, value.Value);
            }
        }

        var mediaType = lineage?.MediaType ?? ResolveMediaType(lookup);
        await MergeArrayBackedRelationshipHintsAsync(lookup, canonicalEntityId, entityId, ct).ConfigureAwait(false);

        // Try to find an immediate shelf identity. Broader franchise/universe
        // values are relationships on the shelf and become Collections only
        // when multiple shelves share them.
        var shelf = ResolveShelfIdentity(lookup, mediaType);

        if (shelf is null)
        {
            _logger.LogDebug("CollectionAssignment: no shelf identity for work {WorkId}; standalone", workId);
            return new CollectionAssignmentResult(
                CollectionAssignmentOutcome.SkippedNoShelfIdentity,
                entityId,
                workId.Value,
                Message: "No shelf identity found.");
        }

        if (existingCollectionId is not null)
        {
            var existingCollection = await _collectionRepo.GetByIdAsync(existingCollectionId.Value, ct);
            if (existingCollection is not null && CollectionMatchesShelf(existingCollection, shelf))
            {
                await UpgradeCollectionIdentityAsync(existingCollection, shelf, ct);
                var relationshipsAdded = await EnsureCollectionRelationshipsAsync(existingCollection.Id, lookup, ct);
                await EnsureSequenceFactsAsync(existingCollection.Id, mediaType, lookup, ct);
                await UpsertProviderSequenceManifestAsync(existingCollection.Id, lookup, ct);

                _logger.LogDebug(
                    "CollectionAssignment: work {WorkId} already assigned to collection {CollectionId}",
                    workId,
                    existingCollectionId);
                return new CollectionAssignmentResult(
                    CollectionAssignmentOutcome.AlreadyAssigned,
                    entityId,
                    workId.Value,
                    existingCollection.Id,
                    RelationshipsAdded: relationshipsAdded,
                    IdentityKey: shelf.Qid ?? shelf.ProviderKey);
            }

            if (existingCollection is not null)
            {
                _logger.LogInformation(
                    "CollectionAssignment: work {WorkId} is assigned to broader collection '{CollectionName}' ({ExistingQid}) but immediate shelf is {ParentQid}; reassigning",
                    workId,
                    existingCollection.DisplayName,
                    existingCollection.WikidataQid,
                    shelf.Qid ?? shelf.ProviderKey);
            }
        }

        // Find or create a ContentGroup collection for this parent QID.
        // Serialise on the QID so two workers cannot race past FindByQidAsync
        // and both Upsert a duplicate collection for the same album/show/series.
        var gate = ShelfLocks.GetOrAdd(shelf.LockKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        Collection collection;
        var createdCollection = false;
        var relationshipCount = 0;
        try
        {
            var existing = await FindShelfCollectionAsync(shelf, ct);
            if (existing is not null)
            {
                collection = existing;
                await UpgradeCollectionIdentityAsync(collection, shelf, ct);
            }
            else
            {
                // Sanitize the label — fall back to QID if label is just a QID or empty
                var displayName = SanitizeShelfLabel(shelf.Label, shelf.Qid, shelf.ProviderKey);

                collection = new Collection
                {
                    Id = Guid.NewGuid(),
                    DisplayName = displayName,
                    WikidataQid = shelf.Qid,
                    RuleHash = shelf.ProviderKey,
                    GroupByField = GetGroupByField(shelf.MediaType),
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                collection.RestoreDefinition(
                    CollectionType.ContentGroup,
                    CollectionScope.Library,
                    CollectionResolution.Materialized,
                    CollectionMatchMode.All,
                    CollectionSortDirection.Desc,
                    CollectionUniverseStatus.Unknown);

                await _collectionRepo.UpsertAsync(collection, ct);
                createdCollection = true;

                _logger.LogInformation(
                    "CollectionAssignment: created ContentGroup collection '{Name}' ({Identity}) for work {WorkId}",
                    collection.DisplayName, shelf.Qid ?? shelf.ProviderKey, workId);
            }

            relationshipCount = await EnsureCollectionRelationshipsAsync(collection.Id, lookup, ct);
            await EnsureSequenceFactsAsync(collection.Id, mediaType, lookup, ct);
            await _collectionRepo.AssignWorkToCollectionAsync(workId.Value, collection.Id, ct);
            await UpsertProviderSequenceManifestAsync(collection.Id, lookup, ct);
        }
        finally
        {
            gate.Release();
        }

        _logger.LogInformation(
            "CollectionAssignment: assigned work {WorkId} to collection '{CollectionName}' ({Identity})",
            workId, collection.DisplayName, shelf.Qid ?? shelf.ProviderKey);

        return new CollectionAssignmentResult(
            CollectionAssignmentOutcome.Assigned,
            entityId,
            workId.Value,
            collection.Id,
            CreatedCollection: createdCollection,
            RelationshipsAdded: relationshipCount,
            IdentityKey: shelf.Qid ?? shelf.ProviderKey);
    }

    private async Task<Collection?> FindShelfCollectionAsync(ShelfIdentity shelf, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(shelf.Qid))
        {
            var byQid = await _collectionRepo.FindByQidAsync(shelf.Qid, ct);
            if (byQid is not null)
            {
                return byQid;
            }
        }

        return string.IsNullOrWhiteSpace(shelf.ProviderKey)
            ? null
            : await _collectionRepo.FindByRuleHashAsync(shelf.ProviderKey, ct);
    }

    private async Task UpgradeCollectionIdentityAsync(Collection collection, ShelfIdentity shelf, CancellationToken ct)
    {
        var changed = false;

        if (!string.IsNullOrWhiteSpace(shelf.Qid)
            && !string.Equals(collection.WikidataQid, shelf.Qid, StringComparison.OrdinalIgnoreCase))
        {
            collection.WikidataQid = shelf.Qid;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(shelf.ProviderKey)
            && !string.Equals(collection.RuleHash, shelf.ProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            collection.RuleHash = shelf.ProviderKey;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(collection.DisplayName) || IsQidLike(collection.DisplayName))
        {
            collection.DisplayName = SanitizeShelfLabel(shelf.Label, shelf.Qid, shelf.ProviderKey);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(collection.GroupByField))
        {
            collection.GroupByField = GetGroupByField(shelf.MediaType);
            changed = true;
        }

        if (changed)
        {
            await _collectionRepo.UpsertAsync(collection, ct);
        }
    }

    private async Task EnsureSequenceFactsAsync(
        Guid collectionId,
        MediaType mediaType,
        Dictionary<string, string> lookup,
        CancellationToken ct)
    {
        var total = StringHelpers.FirstNonBlank(
            ValueOrNull(lookup, MetadataFieldConstants.SequenceTotal),
            mediaType switch
            {
                MediaType.Music => ValueOrNull(lookup, MetadataFieldConstants.TrackCount),
                MediaType.TV => ValueOrNull(lookup, MetadataFieldConstants.EpisodeCount),
                MediaType.Comics => ValueOrNull(lookup, MetadataFieldConstants.IssueCount),
                _ => null
            })?.Trim();

        if (string.IsNullOrWhiteSpace(total) || !int.TryParse(total, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return;
        }

        var scope = StringHelpers.FirstNonBlank(
            ValueOrNull(lookup, MetadataFieldConstants.SequenceTotalScope),
            SequenceCountScope.MainSequence.ToString())!.Trim();

        var providerId = mediaType switch
        {
            MediaType.Music => WellKnownProviders.AppleApi,
            MediaType.TV => WellKnownProviders.Tvdb,
            MediaType.Movies => WellKnownProviders.Tmdb,
            MediaType.Comics => WellKnownProviders.ComicVine,
            _ => (Guid?)null
        };

        var now = DateTimeOffset.UtcNow;
        await _canonicalRepo.UpsertBatchAsync(
            [
                new CanonicalValue
                {
                    EntityId = collectionId,
                    Key = MetadataFieldConstants.SequenceTotal,
                    Value = parsed.ToString(CultureInfo.InvariantCulture),
                    LastScoredAt = now,
                    WinningProviderId = providerId
                },
                new CanonicalValue
                {
                    EntityId = collectionId,
                    Key = MetadataFieldConstants.SequenceTotalScope,
                    Value = scope,
                    LastScoredAt = now,
                    WinningProviderId = providerId
                }
            ],
            ct);
    }

    private async Task UpsertProviderSequenceManifestAsync(
        Guid collectionId,
        Dictionary<string, string> lookup,
        CancellationToken ct)
    {
        if (_manifestRepo is null
            || !TryGetValue(lookup, MetadataFieldConstants.SequenceManifestJson, out var rawManifest))
        {
            return;
        }

        ProviderSequenceManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ProviderSequenceManifest>(rawManifest);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "CollectionAssignment: invalid provider sequence manifest for collection {CollectionId}", collectionId);
            return;
        }

        if (manifest is null
            || string.IsNullOrWhiteSpace(manifest.Provider)
            || string.IsNullOrWhiteSpace(manifest.ContainerId)
            || string.IsNullOrWhiteSpace(manifest.ExternalIdKey)
            || manifest.Items.Count == 0)
        {
            return;
        }

        var externalIds = manifest.Items
            .Select(item => item.ExternalId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ownedByExternalId = await _manifestRepo.FindWorkIdsByExternalIdsAsync(
            manifest.ExternalIdKey,
            externalIds,
            ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var rows = manifest.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.ExternalId) && !string.IsNullOrWhiteSpace(item.Title))
            .Select((item, index) =>
            {
                var linkedWorkId = ownedByExternalId.TryGetValue(item.ExternalId, out var workIds)
                    ? workIds.FirstOrDefault()
                    : (Guid?)null;
                var parsedOrdinal = double.TryParse(item.Ordinal, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : (double?)null;

                return new SeriesManifestItemRecord
                {
                    Id = Guid.NewGuid(),
                    CollectionId = collectionId,
                    SeriesQid = manifest.ContainerId,
                    ItemQid = $"{manifest.Provider}:{manifest.ExternalIdKey}:{item.ExternalId}",
                    ItemLabel = item.Title,
                    ItemDescription = item.Description,
                    MediaType = manifest.MediaType,
                    MediaKind = ResolveProviderManifestMediaKind(manifest.MediaType),
                    RawOrdinal = item.Ordinal,
                    ParsedOrdinal = parsedOrdinal,
                    OrdinalScopeQid = manifest.ContainerId,
                    SortOrder = parsedOrdinal ?? index + 1,
                    PublicationDate = item.ReleaseDate,
                    Duration = item.Duration,
                    MembershipScope = SeriesMembershipScopeNames.MainSequence,
                    SourcePropertiesJson = JsonSerializer.Serialize(new[] { $"{manifest.Provider}:sequence-manifest" }),
                    RelationshipsJson = "[]",
                    OrderSource = "ProviderOrdinal",
                    OwnershipState = linkedWorkId.HasValue ? "Owned" : "Missing",
                    LinkedWorkId = linkedWorkId,
                    LastHydratedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
            })
            .ToList();

        if (rows.Count == 0)
        {
            return;
        }

        var manifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', rows.Select(row => $"{row.ItemQid}:{row.RawOrdinal}"))))).ToLowerInvariant();
        var hydration = new SeriesManifestHydration
        {
            SeriesQid = manifest.ContainerId,
            CollectionId = collectionId,
            SeriesLabel = manifest.ContainerLabel,
            ManifestSource = manifest.Provider,
            ManifestVersion = "1",
            ManifestHash = manifestHash,
            KnownItemQidsHash = manifestHash,
            WarningsJson = "[]",
            ApiMetadataJson = JsonSerializer.Serialize(new
            {
                containerKind = manifest.ContainerKind ?? "OrderedSeries",
                completeness = manifest.IsAuthoritative ? "Complete" : "Partial",
                expectedTotal = manifest.ExpectedTotal ?? rows.Count,
                expectedTotalKind = manifest.ExpectedTotalKind ?? "manifest_items",
                expectedTotalSource = $"{manifest.Provider}-sequence-manifest",
                expectedTotalConfidence = manifest.IsAuthoritative ? 1.0 : 0.7,
            }),
            LastHydratedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _manifestRepo.UpsertManifestAsync(hydration, rows, ct).ConfigureAwait(false);
        await _manifestRepo.LinkOwnedWorksAsync(collectionId, rows, ct).ConfigureAwait(false);
    }

    private static string ResolveProviderManifestMediaKind(string mediaType)
        => Enum.TryParse<MediaType>(mediaType, true, out var parsed)
            ? parsed switch
            {
                MediaType.Books => "LiteraryWork",
                MediaType.Audiobooks => "Audiobook",
                MediaType.Movies => "Film",
                MediaType.TV => "Television",
                MediaType.Comics => "Comic",
                MediaType.Music => "Music",
                _ => "Unknown",
            }
            : "Unknown";

    private static bool CollectionMatchesShelf(Collection collection, ShelfIdentity shelf)
    {
        if (!string.IsNullOrWhiteSpace(shelf.Qid)
            && string.Equals(collection.WikidataQid, shelf.Qid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(shelf.ProviderKey)
            && string.Equals(collection.RuleHash, shelf.ProviderKey, StringComparison.OrdinalIgnoreCase);
    }

    private static ShelfIdentity? ResolveShelfIdentity(
        Dictionary<string, string> lookup,
        MediaType mediaType)
    {
        var relationshipKeys = BuildRelationshipKeys(lookup);

        if (mediaType is MediaType.Movies)
        {
            var movieProviderKey = ResolveProviderKey(lookup, mediaType);
            var movieProviderLabel = ResolveProviderLabel(lookup, mediaType);
            if (!string.IsNullOrWhiteSpace(movieProviderKey) && !string.IsNullOrWhiteSpace(movieProviderLabel))
            {
                return new ShelfIdentity(mediaType, movieProviderLabel, null, movieProviderKey, relationshipKeys);
            }
        }

        if (TryGetQid(lookup, "series", out var qid, out var label))
        {
            return new ShelfIdentity(
                mediaType,
                label,
                qid,
                ResolveProviderKey(lookup, mediaType),
                relationshipKeys);
        }

        if (mediaType is MediaType.Comics
            && TryGetValue(lookup, "wikidata_qid", out var rawComicSeriesQid)
            && IsQidLike(RetailHints.NormalizeQid(rawComicSeriesQid)))
        {
            var comicSeriesQid = RetailHints.NormalizeQid(rawComicSeriesQid);
            var comicSeriesLabel = StringHelpers.FirstNonBlank(
                ValueOrNull(lookup, MetadataFieldConstants.Series),
                ValueOrNull(lookup, MetadataFieldConstants.Title),
                comicSeriesQid)?.Trim();

            return new ShelfIdentity(
                mediaType,
                comicSeriesLabel!,
                comicSeriesQid,
                ResolveProviderKey(lookup, mediaType),
                relationshipKeys);
        }

        var providerKey = ResolveProviderKey(lookup, mediaType);
        var providerLabel = ResolveProviderLabel(lookup, mediaType);
        if (!string.IsNullOrWhiteSpace(providerKey) && !string.IsNullOrWhiteSpace(providerLabel))
        {
            return new ShelfIdentity(mediaType, providerLabel, null, providerKey, relationshipKeys);
        }

        var localLabel = ResolveLocalShelfLabel(lookup, mediaType);
        if (string.IsNullOrWhiteSpace(localLabel))
        {
            return null;
        }

        var localKey = $"local:{mediaType.ToString().ToLowerInvariant()}:{NormalizeKey(localLabel)}";
        return new ShelfIdentity(mediaType, localLabel, null, localKey, relationshipKeys);
    }

    private static string? ResolveProviderKey(Dictionary<string, string> lookup, MediaType mediaType)
    {
        return mediaType switch
        {
            MediaType.Movies when TryGetValue(lookup, "tmdb_collection_id", out var tmdbCollectionId)
                => $"tmdb:collection:{tmdbCollectionId}",
            MediaType.TV when TryGetValue(lookup, BridgeIdKeys.TvdbId, out var tvdbId)
                => $"tvdb:tv:{tvdbId}",
            MediaType.Music when TryGetValue(lookup, BridgeIdKeys.AppleMusicCollectionId, out var appleCollectionId)
                => $"applemusic:album:{appleCollectionId}",
            MediaType.Music when TryGetValue(lookup, BridgeIdKeys.MusicBrainzReleaseGroupId, out var releaseGroupId)
                => $"musicbrainz:release-group:{releaseGroupId}",
            MediaType.Comics when TryGetValue(lookup, BridgeIdKeys.ComicVineVolumeId, out var comicVineVolumeId)
                => $"comicvine:volume:{comicVineVolumeId}",
            _ => null,
        };
    }

    private static string? ResolveProviderLabel(Dictionary<string, string> lookup, MediaType mediaType)
    {
        return mediaType switch
        {
            MediaType.Movies => StringHelpers.FirstNonBlank(
                ValueOrNull(lookup, "tmdb_collection_name"),
                ValueOrNull(lookup, MetadataFieldConstants.Series))?.Trim(),
            MediaType.TV => StringHelpers.FirstNonBlank(
                ValueOrNull(lookup, MetadataFieldConstants.ShowName),
                ValueOrNull(lookup, MetadataFieldConstants.Title),
                ValueOrNull(lookup, MetadataFieldConstants.Series))?.Trim(),
            MediaType.Music => StringHelpers.FirstNonBlank(
                ValueOrNull(lookup, MetadataFieldConstants.Album),
                ValueOrNull(lookup, MetadataFieldConstants.Title))?.Trim(),
            MediaType.Comics => ValueOrNull(lookup, MetadataFieldConstants.Series),
            _ => null,
        };
    }

    private static string? ResolveLocalShelfLabel(Dictionary<string, string> lookup, MediaType mediaType)
    {
        return mediaType switch
        {
            MediaType.TV => StringHelpers.FirstNonBlank(
                ValueOrNull(lookup, MetadataFieldConstants.ShowName),
                ValueOrNull(lookup, MetadataFieldConstants.Series))?.Trim(),
            MediaType.Music => StringHelpers.FirstNonBlank(
                ValueOrNull(lookup, MetadataFieldConstants.Album),
                ValueOrNull(lookup, MetadataFieldConstants.Title))?.Trim(),
            MediaType.Books or MediaType.Audiobooks or MediaType.Comics => ValueOrNull(lookup, MetadataFieldConstants.Series),
            _ => null,
        };
    }

    private static IReadOnlyList<string> BuildRelationshipKeys(Dictionary<string, string> lookup)
    {
        var keys = new List<string>();
        foreach (var claimKey in new[] { "series", "franchise", "fictional_universe", "based_on" })
        {
            if (TryGetQid(lookup, claimKey, out var qid, out _))
            {
                keys.Add($"{claimKey}:{qid}");
            }
        }

        return keys;
    }

    private static MediaType ResolveMediaType(Dictionary<string, string> lookup)
        => Enum.TryParse<MediaType>(ValueOrNull(lookup, MetadataFieldConstants.MediaTypeField), ignoreCase: true, out var mediaType)
            ? mediaType
            : MediaType.Unknown;

    private static string SanitizeShelfLabel(string label, string? qid, string? providerKey)
        => string.IsNullOrWhiteSpace(label) || IsQidLike(label)
            ? StringHelpers.FirstNonBlank(qid, providerKey, "Untitled shelf")!.Trim()
            : label.Trim();

    private static string? GetGroupByField(MediaType mediaType) => mediaType switch
    {
        MediaType.TV => MetadataFieldConstants.ShowName,
        MediaType.Music => MetadataFieldConstants.Album,
        MediaType.Books or MediaType.Audiobooks or MediaType.Comics or MediaType.Movies => MetadataFieldConstants.Series,
        _ => null,
    };

    private static bool TryGetValue(Dictionary<string, string> lookup, string key, out string value)
    {
        value = string.Empty;
        if (!lookup.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
        return true;
    }

    private static string? ValueOrNull(Dictionary<string, string> lookup, string key)
        => TryGetValue(lookup, key, out var value) ? value : null;

    private static bool IsQidLike(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length > 1
           && value[0] is 'Q'
           && char.IsDigit(value[1]);

    private static string NormalizeKey(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSeparator = false;

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    /// <summary>
    /// Extracts the immediate shelf QID from canonical values.
    /// Series (P179) drives lane shelves; franchise/universe values are rollup relationships.
    /// Returns the QID and human-readable label.
    /// </summary>
    private static (string? Qid, string? Label) ResolveParentQid(
        Dictionary<string, string> lookup)
    {
        // Try series first (most specific: album, TV show, book series)
        if (TryGetQid(lookup, "series", out var qid, out var label))
        {
            return (qid, label);
        }

        return (null, null);
    }

    /// <summary>
    /// Extracts a clean QID and label from canonical values for a given claim key.
    /// Handles URI prefixes and :: suffixes.
    /// </summary>
    private static bool TryGetQid(
        Dictionary<string, string> lookup,
        string claimKey,
        out string qid,
        out string label)
    {
        qid = string.Empty;
        label = string.Empty;

        if (!lookup.TryGetValue($"{claimKey}_qid", out var rawQid) ||
            string.IsNullOrWhiteSpace(rawQid))
        {
            return false;
        }

        // Strip entity URI prefix
        qid = rawQid.Contains('/') ? rawQid.Split('/')[^1] : rawQid;

        // Strip ::Label suffix
        if (qid.Contains("::"))
        {
            qid = qid.Split("::", 2)[0];
        }

        if (string.IsNullOrWhiteSpace(qid))
        {
            return false;
        }

        // Get the label
        if (lookup.TryGetValue(claimKey, out var rawLabel) && !string.IsNullOrWhiteSpace(rawLabel))
        {
            label = rawLabel.Contains("::") ? rawLabel.Split("::", 2)[^1].Trim() : rawLabel.Trim();
        }
        else
        {
            label = qid;
        }

        return true;
    }

    private async Task<int> EnsureCollectionRelationshipsAsync(
        Guid collectionId,
        Dictionary<string, string> lookup,
        CancellationToken ct)
    {
        var desiredRelationships = BuildCollectionRelationships(collectionId, lookup);
        if (desiredRelationships.Count == 0)
        {
            return 0;
        }

        var existingRelationships = await _collectionRepo.GetRelationshipsAsync(collectionId, ct);
        var missingRelationships = desiredRelationships
            .Where(candidate => !existingRelationships.Any(existing =>
                string.Equals(existing.RelType, candidate.RelType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.RelQid, candidate.RelQid, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missingRelationships.Count == 0)
        {
            return 0;
        }

        await _collectionRepo.InsertRelationshipsAsync(missingRelationships, ct);

        _logger.LogInformation(
            "CollectionAssignment: added {Count} relationship(s) to collection {CollectionId}",
            missingRelationships.Count,
            collectionId);

        return missingRelationships.Count;
    }

    private static IReadOnlyList<CollectionRelationship> BuildCollectionRelationships(
        Guid collectionId,
        Dictionary<string, string> lookup)
    {
        var relationships = new List<CollectionRelationship>();
        foreach (var claimKey in new[] { "series", "franchise", "fictional_universe", "based_on" })
        {
            if (!TryGetQid(lookup, claimKey, out var qid, out var label))
            {
                continue;
            }

            relationships.Add(new CollectionRelationship
            {
                Id = Guid.NewGuid(),
                CollectionId = collectionId,
                RelType = claimKey,
                RelQid = qid,
                RelLabel = label,
                Confidence = 1.0,
                DiscoveredAt = DateTimeOffset.UtcNow,
            });
        }

        return relationships;
    }

    private async Task MergeArrayBackedRelationshipHintsAsync(
        Dictionary<string, string> lookup,
        Guid canonicalEntityId,
        Guid sourceEntityId,
        CancellationToken ct)
    {
        if (_arrayRepo is null)
        {
            return;
        }

        await MergeArrayBackedRelationshipHintsForEntityAsync(lookup, canonicalEntityId, ct).ConfigureAwait(false);
        if (sourceEntityId != canonicalEntityId)
        {
            await MergeArrayBackedRelationshipHintsForEntityAsync(lookup, sourceEntityId, ct).ConfigureAwait(false);
        }
    }

    private async Task MergeArrayBackedRelationshipHintsForEntityAsync(
        Dictionary<string, string> lookup,
        Guid entityId,
        CancellationToken ct)
    {
        var arrays = await _arrayRepo!.GetAllByEntityAsync(entityId, ct).ConfigureAwait(false);
        if (arrays.TryGetValue("based_on", out var basedOnEntries))
        {
            AddFirstQidArrayHint(lookup, "based_on", basedOnEntries);
        }

        if (arrays.TryGetValue(MetadataFieldConstants.Characters, out var characterEntries))
        {
            var shelfLabel = StringHelpers.FirstNonBlank(
                ValueOrNull(lookup, MetadataFieldConstants.Series),
                ValueOrNull(lookup, MetadataFieldConstants.ShowName),
                ValueOrNull(lookup, MetadataFieldConstants.Title))?.Trim();

            if (!string.IsNullOrWhiteSpace(shelfLabel))
            {
                var matchingShelfCharacter = characterEntries.FirstOrDefault(entry =>
                    !string.IsNullOrWhiteSpace(entry.ValueQid)
                    && string.Equals(NormalizeKey(entry.Value), NormalizeKey(shelfLabel), StringComparison.OrdinalIgnoreCase));

                if (matchingShelfCharacter is not null)
                {
                    AddArrayHint(lookup, "based_on", matchingShelfCharacter);
                }
            }
        }
    }

    private static void AddFirstQidArrayHint(
        Dictionary<string, string> lookup,
        string claimKey,
        IReadOnlyList<CanonicalArrayEntry> entries)
    {
        var entry = entries.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value.ValueQid));
        if (entry is not null)
        {
            AddArrayHint(lookup, claimKey, entry);
        }
    }

    private static void AddArrayHint(
        Dictionary<string, string> lookup,
        string claimKey,
        CanonicalArrayEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ValueQid))
        {
            return;
        }

        lookup.TryAdd(claimKey, entry.Value);
        lookup.TryAdd($"{claimKey}_qid", entry.ValueQid);
    }
}

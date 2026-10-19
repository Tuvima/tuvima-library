using System.Collections.Concurrent;
using System.Text.Json;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Services;

public sealed class WikidataSeriesManifestHydrationService
{
    private readonly WikidataReconciler _reconciler;
    private readonly ISeriesManifestRepository _manifestRepo;
    private readonly ICollectionRepository _collectionRepo;
    private readonly IConfigurationLoader _configLoader;
    private readonly ILogger<WikidataSeriesManifestHydrationService> _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private const int ManifestMaxDepth = 2;
    private const int ManifestMaxItems = 500;
    private const string ManifestScopeFilter = "relationship-scoped-v2";

    public WikidataSeriesManifestHydrationService(
        WikidataReconciler reconciler,
        ISeriesManifestRepository manifestRepo,
        ICollectionRepository collectionRepo,
        IConfigurationLoader configLoader,
        ILogger<WikidataSeriesManifestHydrationService> logger)
    {
        _reconciler = reconciler;
        _manifestRepo = manifestRepo;
        _collectionRepo = collectionRepo;
        _configLoader = configLoader;
        _logger = logger;
    }

    public async Task HydrateAsync(SeriesManifestHydrationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!ShouldHydrate(context.MediaType))
        {
            return;
        }

        var candidates = await ResolveSeriesManifestCandidatesAsync(context, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            await HydrateManifestCandidateAsync(context, candidate, ct).ConfigureAwait(false);
        }
    }

    private async Task HydrateManifestCandidateAsync(
        SeriesManifestHydrationContext context,
        SeriesManifestCandidate candidate,
        CancellationToken ct)
    {
        var cacheKey = $"{context.IngestionRunId?.ToString("D") ?? "process"}:{candidate.Qid}";
        var lazy = _inFlight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task>(() => HydrateCoreAsync(context, candidate, ct)));

        try
        {
            await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
            {
                _inFlight.TryRemove(cacheKey, out _);
            }
        }
    }

    private async Task HydrateCoreAsync(
        SeriesManifestHydrationContext context,
        SeriesManifestCandidate candidate,
        CancellationToken ct)
    {
        try
        {
            var seriesQid = candidate.Qid;
            var seriesLabel = candidate.Label;
            var ttlDays = Math.Max(1, _configLoader.LoadHydration().SeriesManifestRefreshDays);
            var language = NormalizeLanguage(_configLoader.LoadCore().Language.Metadata);
            var now = DateTimeOffset.UtcNow;
            var mediaType = context.MediaType.ToString();
            var cachedHydration = await _manifestRepo.GetHydrationAsync(seriesQid, mediaType, ct).ConfigureAwait(false);

            if (cachedHydration is not null
                && IsCachedHydrationCurrent(cachedHydration)
                && now - cachedHydration.LastHydratedAt < TimeSpan.FromDays(ttlDays))
            {
                var cachedItems = await _manifestRepo.GetItemsBySeriesQidAsync(seriesQid, mediaType, ct).ConfigureAwait(false);
                if (cachedItems.Count > 0
                    && cachedItems.Any(i => string.Equals(i.ItemQid, context.ResolvedWorkQid, StringComparison.OrdinalIgnoreCase)))
                {
                    var relinked = await RelinkItemsAsync(
                        cachedHydration.CollectionId,
                        cachedItems,
                        context,
                        ct).ConfigureAwait(false);

                    await _manifestRepo.UpsertManifestAsync(cachedHydration.WithUpdatedTimestamp(), relinked, ct)
                        .ConfigureAwait(false);
                    await _manifestRepo.LinkOwnedWorksAsync(cachedHydration.CollectionId, relinked, ct)
                        .ConfigureAwait(false);

                    _logger.LogDebug(
                        "Series manifest cache hit for {SeriesQid}; relinked {Count} cached rows",
                        seriesQid,
                        relinked.Count);

                    await HydrateParentCollectionManifestsAsync(
                        context,
                        relinked,
                        seriesQid,
                        language,
                        now,
                        ttlDays,
                        ct).ConfigureAwait(false);

                    return;
                }
            }

            var manifest = await _reconciler.Series.GetManifestAsync(
                CreateManifestRequest(seriesQid, language),
                ct).ConfigureAwait(false);

            if (!IsImmediateManifestContainer(manifest.ContainerKind)
                || (candidate.RequireKnownImmediateContainer && manifest.ContainerKind == WikidataContainerKind.Unknown))
            {
                _logger.LogInformation(
                    "Skipping Wikidata manifest {SeriesQid} ({SeriesLabel}) because it is classified as {ContainerKind}, not an immediate shelf",
                    manifest.SeriesQid,
                    manifest.SeriesLabel ?? seriesLabel,
                    manifest.ContainerKind);
                return;
            }

            var collection = await UpsertCollectionAsync(
                manifest.SeriesQid,
                StringHelpers.FirstNonBlankOr("Unknown series", manifest.SeriesLabel, seriesLabel, context.SeriesHint, manifest.SeriesQid),
                mediaType,
                ct).ConfigureAwait(false);

            var scopedItems = FilterManifestItems(manifest.Items, context, manifest.SeriesQid).ToList();
            var itemRecords = NormalizeManifestItems(scopedItems)
                .Select(item => ToRecord(collection.Id, manifest.SeriesQid, context.MediaType, item.Item, item.SortOrder, now))
                .ToList();

            itemRecords = await RelinkItemsAsync(collection.Id, itemRecords, context, ct)
                .ConfigureAwait(false);

            var warningDtos = BuildWarningDiagnostics(
                manifest.Warnings,
                manifest.SeriesQid,
                context.ResolvedWorkQid,
                resolvedWorkPresentInManifest: itemRecords.Count == 0
                    || itemRecords.Any(i => string.Equals(i.ItemQid, context.ResolvedWorkQid, StringComparison.OrdinalIgnoreCase)));

            var warningCount = warningDtos.Count;
            if (warningCount > 0)
            {
                _logger.LogDebug(
                    "Stored {Count} Wikidata series manifest diagnostic warning(s) for {SeriesQid}; no review queue item was created",
                    warningCount,
                    manifest.SeriesQid);
            }

            var warningsJson = JsonSerializer.Serialize(
                warningDtos,
                MediaEngineJson.Web);

            var itemQids = itemRecords
                .Select(i => i.ItemQid)
                .OrderBy(q => q, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var manifestJson = JsonSerializer.Serialize(manifest, MediaEngineJson.Web);
            var hydration = new SeriesManifestHydration
            {
                SeriesQid = manifest.SeriesQid,
                CollectionId = collection.Id,
                SeriesLabel = manifest.SeriesLabel ?? seriesLabel,
                ManifestVersion = typeof(SeriesManifestRequest).Assembly.GetName().Version?.ToString(),
                ManifestHash = Hashing.Sha256Hex(manifestJson),
                KnownItemQidsHash = Hashing.Sha256Hex(string.Join('\n', itemQids)),
                WarningsJson = warningsJson,
                ApiMetadataJson = BuildApiMetadataJson(language, manifest),
                LastHydratedAt = now,
                CreatedAt = cachedHydration?.CreatedAt ?? now,
                UpdatedAt = now,
            };

            await _manifestRepo.UpsertManifestAsync(hydration, itemRecords, ct).ConfigureAwait(false);
            await _manifestRepo.LinkOwnedWorksAsync(collection.Id, itemRecords, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Hydrated Wikidata series manifest {SeriesQid} ({SeriesLabel}) with {Count} named items",
                manifest.SeriesQid,
                manifest.SeriesLabel ?? seriesLabel,
                itemRecords.Count);

            await HydrateParentCollectionManifestsAsync(
                context,
                itemRecords,
                manifest.SeriesQid,
                language,
                now,
                ttlDays,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Series manifest hydration failed for resolved QID {Qid} ({Title}); ingestion will continue",
                context.ResolvedWorkQid,
                context.Title);
        }
    }

    private async Task HydrateParentCollectionManifestsAsync(
        SeriesManifestHydrationContext context,
        IReadOnlyList<SeriesManifestItemRecord> childManifestItems,
        string childSeriesQid,
        string language,
        DateTimeOffset now,
        int ttlDays,
        CancellationToken ct)
    {
        var currentWorkItems = childManifestItems
            .Where(item => IsCurrentWorkManifestItem(item, context))
            .ToList();

        foreach (var parent in ResolveParentCollectionManifestCandidates(currentWorkItems, childSeriesQid))
        {
            try
            {
                await HydrateParentCollectionManifestAsync(
                    context,
                    parent,
                    childSeriesQid,
                    language,
                    now,
                    ttlDays,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Parent collection manifest hydration failed for {ParentQid} from series {SeriesQid}; ingestion will continue",
                    parent.Qid,
                    childSeriesQid);
            }
        }
    }

    private async Task HydrateParentCollectionManifestAsync(
        SeriesManifestHydrationContext context,
        ParentCollectionManifestCandidate parent,
        string childSeriesQid,
        string language,
        DateTimeOffset now,
        int ttlDays,
        CancellationToken ct)
    {
        var mediaType = context.MediaType.ToString();
        var cachedHydration = await _manifestRepo.GetHydrationAsync(parent.Qid, mediaType, ct).ConfigureAwait(false);
        if (cachedHydration is not null
            && IsCachedHydrationCurrent(cachedHydration)
            && now - cachedHydration.LastHydratedAt < TimeSpan.FromDays(ttlDays))
        {
            var cachedItems = await _manifestRepo.GetItemsBySeriesQidAsync(parent.Qid, mediaType, ct).ConfigureAwait(false);
            if (cachedItems.Count > 0
                && cachedItems.Any(i => string.Equals(i.ItemQid, context.ResolvedWorkQid, StringComparison.OrdinalIgnoreCase)))
            {
                var relinked = await RelinkItemsAsync(
                    cachedHydration.CollectionId,
                    cachedItems,
                    context,
                    ct).ConfigureAwait(false);

                await _manifestRepo.UpsertManifestAsync(cachedHydration.WithUpdatedTimestamp(), relinked, ct)
                    .ConfigureAwait(false);
                await _manifestRepo.LinkOwnedWorksAsync(cachedHydration.CollectionId, relinked, ct)
                    .ConfigureAwait(false);
                return;
            }
        }

        var manifest = await _reconciler.Series.GetManifestAsync(
            CreateManifestRequest(parent.Qid, language),
            ct).ConfigureAwait(false);

        if (!IsImmediateManifestContainer(manifest.ContainerKind))
        {
            _logger.LogInformation(
                "Skipping parent Wikidata manifest {SeriesQid} ({SeriesLabel}) from series {ChildSeriesQid} because it is classified as {ContainerKind}, not an immediate shelf",
                manifest.SeriesQid,
                manifest.SeriesLabel ?? parent.Label,
                childSeriesQid,
                manifest.ContainerKind);
            return;
        }

        // The parent collection keeps its manifest (the rows say which works it lists) but never becomes the shelf of
        // those works: the placement rule puts them in the Series whose manifest lists this parent.
        var collection = await UpsertCollectionAsync(
            manifest.SeriesQid,
            StringHelpers.FirstNonBlankOr("Unknown series", manifest.SeriesLabel, parent.Label, manifest.SeriesQid),
            mediaType,
            ct).ConfigureAwait(false);

        var scopedItems = FilterManifestItems(manifest.Items, context, manifest.SeriesQid).ToList();
        var itemRecords = NormalizeManifestItems(scopedItems)
            .Select(item => ToRecord(collection.Id, manifest.SeriesQid, context.MediaType, item.Item, item.SortOrder, now))
            .ToList();

        itemRecords = await RelinkItemsAsync(collection.Id, itemRecords, context, ct)
            .ConfigureAwait(false);

        var warningDtos = BuildWarningDiagnostics(
            manifest.Warnings,
            manifest.SeriesQid,
            context.ResolvedWorkQid,
            resolvedWorkPresentInManifest: itemRecords.Count == 0
                || itemRecords.Any(i => string.Equals(i.ItemQid, context.ResolvedWorkQid, StringComparison.OrdinalIgnoreCase)));

        var itemQids = itemRecords
            .Select(i => i.ItemQid)
            .OrderBy(q => q, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manifestJson = JsonSerializer.Serialize(manifest, MediaEngineJson.Web);
        var hydration = new SeriesManifestHydration
        {
            SeriesQid = manifest.SeriesQid,
            CollectionId = collection.Id,
            SeriesLabel = manifest.SeriesLabel ?? parent.Label,
            ManifestVersion = typeof(SeriesManifestRequest).Assembly.GetName().Version?.ToString(),
            ManifestHash = Hashing.Sha256Hex(manifestJson),
            KnownItemQidsHash = Hashing.Sha256Hex(string.Join('\n', itemQids)),
            WarningsJson = JsonSerializer.Serialize(warningDtos, MediaEngineJson.Web),
            ApiMetadataJson = BuildApiMetadataJson(
                language,
                manifest,
                sourceSeriesQid: childSeriesQid,
                parentCollectionHydration: true),
            LastHydratedAt = now,
            CreatedAt = cachedHydration?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        await _manifestRepo.UpsertManifestAsync(hydration, itemRecords, ct).ConfigureAwait(false);
        await _manifestRepo.LinkOwnedWorksAsync(collection.Id, itemRecords, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Hydrated Wikidata parent collection manifest {SeriesQid} ({SeriesLabel}) from series {ChildSeriesQid} with {Count} named items",
            manifest.SeriesQid,
            manifest.SeriesLabel ?? parent.Label,
            childSeriesQid,
            itemRecords.Count);
    }

    private static SeriesManifestRequest CreateManifestRequest(string seriesQid, string language) => new()
    {
        SeriesQid = seriesQid,
        Language = language,
        IncludeCollections = true,
        ExpandCollections = true,
        IncludePublicationDate = true,
        IncludeDescriptions = true,
        MaxDepth = ManifestMaxDepth,
        MaxItems = ManifestMaxItems,
    };

    private static bool IsCurrentWorkManifestItem(
        SeriesManifestItemRecord item,
        SeriesManifestHydrationContext context)
        => string.Equals(item.ItemQid, context.ResolvedWorkQid, StringComparison.OrdinalIgnoreCase)
            || (context.WorkId.HasValue && item.LinkedWorkId == context.WorkId.Value);

    private async Task<IReadOnlyList<SeriesManifestCandidate>> ResolveSeriesManifestCandidatesAsync(
        SeriesManifestHydrationContext context,
        CancellationToken ct)
    {
        var candidates = ResolveClaimedSeriesManifestCandidates(
            context.FullClaims,
            context.MediaType,
            context.ResolvedWorkQid,
            context.SeriesHint ?? context.Title).ToList();
        if (candidates.Count > 0)
        {
            return candidates;
        }

        if (context.Lineage is not null)
        {
            var collectionId = await _collectionRepo.GetCollectionIdByWorkIdAsync(
                context.Lineage.TargetForParentScope,
                ct).ConfigureAwait(false);

            if (collectionId is not null)
            {
                var relationships = await _collectionRepo.GetRelationshipsAsync(collectionId.Value, ct).ConfigureAwait(false);
                var seriesRelationship = relationships.FirstOrDefault(r =>
                    string.Equals(r.RelType, "series", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(r.RelQid));
                if (seriesRelationship is not null)
                {
                    AddSeriesManifestCandidate(candidates, seriesRelationship.RelQid, seriesRelationship.RelLabel);
                }

                if (candidates.Count == 0)
                {
                    var collection = await _collectionRepo.GetByIdAsync(collectionId.Value, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(collection?.WikidataQid))
                    {
                        AddSeriesManifestCandidate(candidates, collection.WikidataQid, collection.DisplayName);
                    }
                }
            }
        }

        return candidates;
    }

    internal static IReadOnlyList<SeriesManifestCandidate> ResolveClaimedSeriesManifestCandidates(
        IReadOnlyList<ProviderClaim> claims,
        MediaType mediaType,
        string? resolvedWorkQid = null,
        string? resolvedWorkLabel = null)
    {
        var candidates = new List<SeriesManifestCandidate>();
        foreach (var key in new[] { "series_qid", "part_of_the_series_qid", "part_of_series_qid" })
        {
            foreach (var claim in claims.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                if (TryParseQidValue(claim.Value, out var qid, out var label))
                {
                    AddSeriesManifestCandidate(candidates, qid, label);
                }
            }
        }

        if (candidates.Count == 0
            && mediaType == MediaType.Comics
            && !string.IsNullOrWhiteSpace(resolvedWorkQid))
        {
            AddSeriesManifestCandidate(
                candidates,
                resolvedWorkQid,
                resolvedWorkLabel,
                requireKnownImmediateContainer: true);
        }

        return candidates;
    }

    private static void AddSeriesManifestCandidate(
        List<SeriesManifestCandidate> candidates,
        string? qid,
        string? label,
        bool requireKnownImmediateContainer = false)
    {
        if (string.IsNullOrWhiteSpace(qid)
            || IsUnsupportedSeriesCandidateLabel(label)
            || candidates.Any(candidate => string.Equals(candidate.Qid, qid, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(new SeriesManifestCandidate(qid, label, requireKnownImmediateContainer));
    }

    private static bool IsImmediateManifestContainer(WikidataContainerKind containerKind)
        => containerKind is WikidataContainerKind.Unknown
            or WikidataContainerKind.OrderedSeries
            or WikidataContainerKind.AlbumRelease
            or WikidataContainerKind.TvShow
            or WikidataContainerKind.TvSeason
            or WikidataContainerKind.ComicSeries
            or WikidataContainerKind.MangaSeries;

    private static bool IsUnsupportedSeriesCandidateLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var normalized = label.Trim().ToLowerInvariant();
        return normalized.StartsWith("list of ", StringComparison.Ordinal)
            || normalized.Contains("wikimedia list", StringComparison.Ordinal)
            || normalized.Contains("production list", StringComparison.Ordinal)
            || normalized.Contains("productions", StringComparison.Ordinal)
            || normalized.Contains("filmography", StringComparison.Ordinal)
            || normalized.Contains("franchise", StringComparison.Ordinal)
            || normalized.Contains("fictional universe", StringComparison.Ordinal)
            || normalized.Contains("shared universe", StringComparison.Ordinal);
    }

    private static string BuildApiMetadataJson(
        string language,
        SeriesManifestResult manifest,
        string? sourceSeriesQid = null,
        bool parentCollectionHydration = false)
    {
        var expectedCounts = manifest.ExpectedCounts;
        var expectedTotal = SelectExpectedTotalFact(expectedCounts);
        return JsonSerializer.Serialize(new
        {
            language,
            includeCollections = true,
            expandCollections = true,
            includeFranchiseMembers = false,
            includePublicationDate = true,
            includeDescriptions = true,
            scopeFilter = ManifestScopeFilter,
            sourceSeriesQid,
            parentCollectionHydration,
            maxDepth = ManifestMaxDepth,
            maxItems = ManifestMaxItems,
            containerKind = manifest.ContainerKind.ToString(),
            completeness = manifest.Completeness.ToString(),
            expectedTotal = expectedTotal?.Count,
            expectedTotalKind = expectedTotal?.Kind,
            expectedTotalSource = expectedTotal?.Source,
            expectedTotalConfidence = expectedTotal?.Confidence,
            expectedCounts,
        }, MediaEngineJson.Web);
    }

    private static ManifestCountFact? SelectExpectedTotalFact(IReadOnlyList<ManifestCountFact> expectedCounts)
        => expectedCounts
            .Where(fact => fact.Count > 0 && fact.Scope == SeriesManifestItemScope.MainSequence)
            .OrderByDescending(fact => fact.Confidence)
            .ThenByDescending(fact => fact.Count)
            .FirstOrDefault();

    private static IEnumerable<SeriesManifestItem> FilterManifestItems(
        IReadOnlyList<SeriesManifestItem> items,
        SeriesManifestHydrationContext context,
        string seriesQid)
    {
        foreach (var item in items)
        {
            if (!IsManifestItemInScope(item, context, seriesQid))
            {
                continue;
            }

            yield return item;
        }
    }

    private static bool IsManifestItemInScope(
        SeriesManifestItem item,
        SeriesManifestHydrationContext context,
        string seriesQid)
    {
        if (string.IsNullOrWhiteSpace(item.Qid)
            || string.Equals(item.Qid, seriesQid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var label = item.Label?.Trim();
        var currentWork = string.Equals(item.Qid, context.ResolvedWorkQid, StringComparison.OrdinalIgnoreCase);
        if (!currentWork && (string.IsNullOrWhiteSpace(label) || IsQidOnlyLabel(label, item.Qid)))
        {
            return false;
        }

        if (context.MediaType is MediaType.Books or MediaType.Audiobooks or MediaType.Comics)
        {
            if (LooksLikeEditionOrTranslation(item))
            {
                return false;
            }
        }

        return IsMemberMediaCompatible(item.MediaKind, context.MediaType);
    }

    private static bool IsMemberMediaCompatible(SeriesManifestMediaKind itemKind, MediaType contextMediaType)
        => contextMediaType switch
        {
            MediaType.Books => itemKind is SeriesManifestMediaKind.Unknown or SeriesManifestMediaKind.LiteraryWork,
            MediaType.Audiobooks => itemKind is SeriesManifestMediaKind.Unknown
                or SeriesManifestMediaKind.LiteraryWork
                or SeriesManifestMediaKind.Audiobook,
            MediaType.Comics => itemKind is SeriesManifestMediaKind.Unknown or SeriesManifestMediaKind.Comic,
            MediaType.Movies => itemKind is SeriesManifestMediaKind.Unknown or SeriesManifestMediaKind.Film,
            MediaType.TV => itemKind is SeriesManifestMediaKind.Unknown or SeriesManifestMediaKind.Television,
            _ => itemKind != SeriesManifestMediaKind.StageWork,
        };

    private static bool IsQidOnlyLabel(string label, string qid) =>
        string.Equals(label, qid, StringComparison.OrdinalIgnoreCase)
        || (label.Length > 1 && label[0] is 'Q' && label.Skip(1).All(char.IsDigit));

    private static bool LooksLikeEditionOrTranslation(SeriesManifestItem item)
    {
        var haystack = string.Join(
            ' ',
            item.Label,
            item.Description,
            item.ParentCollectionLabel,
            item.IsExpandedFromCollection ? "expanded-from-collection" : null).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(haystack))
        {
            return false;
        }

        var editionMarkers = new[]
        {
            "translation",
            "translated",
            "edition",
            "version",
            "omnibus",
            "boxed set",
            "box set",
            "anthology",
            "collected",
            "collection of",
        };
        if (editionMarkers.Any(marker => haystack.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!item.IsExpandedFromCollection)
        {
            return false;
        }

        var languageMarkers = new[]
        {
            "spanish",
            "espanol",
            "german",
            "deutsch",
            "french",
            "francais",
            "italian",
            "portuguese",
            "russian",
            "polish",
            "dutch",
            "swedish",
            "japanese",
            "chinese",
            "korean",
        };
        return languageMarkers.Any(marker => haystack.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<OrderedSeriesManifestItem> NormalizeManifestItems(
        IEnumerable<SeriesManifestItem> items)
    {
        var deduped = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Qid))
            .GroupBy(item => item.Qid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.ParsedSeriesOrdinal ?? decimal.MaxValue)
                .ThenBy(item => item.PublicationDate ?? DateOnly.MaxValue)
                .ThenBy(item => item.Label ?? item.Qid, StringComparer.OrdinalIgnoreCase)
                .First());

        return deduped
            .OrderBy(item => item.ParsedSeriesOrdinal ?? decimal.MaxValue)
            .ThenBy(item => item.PublicationDate ?? DateOnly.MaxValue)
            .ThenBy(item => item.Label ?? item.Qid, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => new OrderedSeriesManifestItem(item, index + 1))
            .ToList();
    }

    internal static IReadOnlyList<ParentCollectionManifestCandidate> ResolveParentCollectionManifestCandidates(
        IReadOnlyList<SeriesManifestItemRecord> childManifestItems,
        string childSeriesQid)
    {
        var candidates = new Dictionary<string, ParentCollectionManifestCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in childManifestItems)
        {
            var parentQid = item.ParentCollectionQid?.Trim();
            if (string.IsNullOrWhiteSpace(parentQid)
                || string.Equals(parentQid, childSeriesQid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidates.TryGetValue(parentQid, out var existing)
                && !string.IsNullOrWhiteSpace(existing.Label))
            {
                continue;
            }

            candidates[parentQid] = new ParentCollectionManifestCandidate(
                parentQid,
                string.IsNullOrWhiteSpace(item.ParentCollectionLabel) ? existing?.Label : item.ParentCollectionLabel);
        }

        return candidates.Values.ToList();
    }

    internal static IReadOnlyList<SeriesManifestWarningDto> BuildWarningDiagnostics(
        IReadOnlyList<SeriesManifestWarning> warnings,
        string seriesQid,
        string resolvedWorkQid,
        bool resolvedWorkPresentInManifest)
    {
        var diagnostics = warnings.Select(w => new SeriesManifestWarningDto
        {
            Code = w.Code,
            Message = w.Message,
            Qid = w.Qid,
        }).ToList();

        if (!resolvedWorkPresentInManifest)
        {
            diagnostics.Add(new SeriesManifestWarningDto
            {
                Code = "ResolvedWorkMissingFromManifest",
                Message = $"Resolved work {resolvedWorkQid} claims series {seriesQid}, but it was not present in the fetched Wikidata series manifest.",
                Qid = resolvedWorkQid,
            });
        }

        return diagnostics;
    }

    private static bool IsCachedHydrationCurrent(SeriesManifestHydration hydration)
    {
        if (string.IsNullOrWhiteSpace(hydration.ApiMetadataJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(hydration.ApiMetadataJson);
            return document.RootElement.TryGetProperty("scopeFilter", out var value)
                && string.Equals(value.GetString(), ManifestScopeFilter, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseQidValue(string? value, out string? qid, out string? label)
    {
        qid = null;
        label = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var parts = trimmed.Split("::", 2, StringSplitOptions.TrimEntries);
        var qidPart = parts[0];
        var slashIndex = qidPart.LastIndexOf('/');
        if (slashIndex >= 0)
        {
            qidPart = qidPart[(slashIndex + 1)..];
        }

        if (qidPart.Length > 1 && qidPart[0] is 'Q' && qidPart.Skip(1).All(char.IsDigit))
        {
            qid = qidPart;
            label = parts.Length > 1 ? parts[1] : null;
            return true;
        }

        return false;
    }

    private async Task<Collection> UpsertCollectionAsync(string seriesQid, string label, string mediaType, CancellationToken ct)
    {
        // A Series is its QID plus its media type: a book Series and an audiobook Series of the same QID are two shelves.
        var existing = await _collectionRepo.FindSeriesShelfAsync(seriesQid, mediaType, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = string.IsNullOrWhiteSpace(label) ? seriesQid : label,
            WikidataQid = seriesQid,
            MediaType = mediaType,
            IconName = "collections_bookmark",
            CreatedAt = DateTimeOffset.UtcNow,
            SortField = "sort_order",
        };
        collection.RestoreDefinition(
            CollectionType.ContentGroup,
            CollectionScope.Library,
            CollectionResolution.Materialized,
            CollectionMatchMode.All,
            CollectionSortDirection.Asc,
            CollectionUniverseStatus.Unknown);

        await _collectionRepo.UpsertAsync(collection, ct).ConfigureAwait(false);
        return collection;
    }

    private async Task<List<SeriesManifestItemRecord>> RelinkItemsAsync(
        Guid collectionId,
        IReadOnlyList<SeriesManifestItemRecord> items,
        SeriesManifestHydrationContext context,
        CancellationToken ct)
    {
        // A Series only holds works of its own media type, so an audiobook is never linked into the book Series.
        var workMatches = await _manifestRepo.FindWorkIdsByQidsAsync(
            items.Select(i => i.ItemQid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            context.MediaType.ToString(),
            ct).ConfigureAwait(false);

        var relinked = new List<SeriesManifestItemRecord>(items.Count);
        foreach (var item in items)
        {
            workMatches.TryGetValue(item.ItemQid, out var matches);
            matches ??= [];

            if (matches.Count == 0
                && string.Equals(item.ItemQid, context.ResolvedWorkQid, StringComparison.OrdinalIgnoreCase)
                && context.WorkId.HasValue)
            {
                matches = [context.WorkId.Value];
            }

            if (matches.Count > 1
                && context.MediaType is MediaType.Books or MediaType.Audiobooks)
            {
                var selectedMatch = context.WorkId.HasValue && matches.Contains(context.WorkId.Value)
                    ? context.WorkId.Value
                    : matches[0];
                matches = [selectedMatch];
            }

            var state = matches.Count switch
            {
                0 => "Missing",
                1 => "Owned",
                _ => "Ambiguous",
            };

            if (state == "Ambiguous")
            {
                _logger.LogInformation(
                    "Series manifest item {ItemQid} has {Count} local works; treating as a variant/duplicate diagnostic, not a review queue item.",
                    item.ItemQid,
                    matches.Count);
            }

            relinked.Add(item.WithOwnership(collectionId, state, matches.Count == 1 ? matches[0] : null));
        }

        return relinked;
    }

    private static SeriesManifestItemRecord ToRecord(
        Guid collectionId,
        string seriesQid,
        MediaType contextMediaType,
        SeriesManifestItem item,
        int sortOrder,
        DateTimeOffset now)
    {
        return new SeriesManifestItemRecord
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            SeriesQid = seriesQid,
            ItemQid = item.Qid,
            ItemLabel = string.IsNullOrWhiteSpace(item.Label) ? item.Qid : item.Label,
            ItemDescription = item.Description,
            MediaType = contextMediaType.ToString(),
            MediaKind = item.MediaKind.ToString(),
            InstanceOfQidsJson = JsonSerializer.Serialize(item.InstanceOfQids, MediaEngineJson.Web),
            RawOrdinal = item.RawSeriesOrdinal,
            ParsedOrdinal = item.ParsedSeriesOrdinal.HasValue ? (double)item.ParsedSeriesOrdinal.Value : null,
            OrdinalScopeQid = item.OrdinalScopeQid,
            SortOrder = sortOrder,
            PublicationDate = item.PublicationDate?.ToString("O"),
            PreviousQid = item.PreviousQid,
            NextQid = item.NextQid,
            ParentCollectionQid = item.ParentCollectionQid,
            ParentCollectionLabel = item.ParentCollectionLabel,
            IsCollection = item.IsCollection,
            IsExpandedFromCollection = item.IsExpandedFromCollection,
            MembershipScope = item.MembershipScope.ToString(),
            SourcePropertiesJson = JsonSerializer.Serialize(item.SourceProperties, MediaEngineJson.Web),
            RelationshipsJson = JsonSerializer.Serialize(item.Relationships, MediaEngineJson.Web),
            OrderSource = item.OrderSource.ToString(),
            OwnershipState = "Missing",
            LastHydratedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static bool ShouldHydrate(MediaType mediaType)
        => mediaType is MediaType.Books or MediaType.Audiobooks or MediaType.Comics or MediaType.Movies or MediaType.TV;

    private static string NormalizeLanguage(string? value)
        => string.IsNullOrWhiteSpace(value) ? "en" : value.Split('-', '_')[0].ToLowerInvariant();

    internal sealed record OrderedSeriesManifestItem(SeriesManifestItem Item, int SortOrder);

    internal sealed record SeriesManifestCandidate(
        string Qid,
        string? Label,
        bool RequireKnownImmediateContainer = false);

    internal sealed record ParentCollectionManifestCandidate(string Qid, string? Label);
}

internal static class SeriesManifestRecordExtensions
{
    public static SeriesManifestHydration WithUpdatedTimestamp(this SeriesManifestHydration hydration)
    {
        var now = DateTimeOffset.UtcNow;
        return new SeriesManifestHydration
        {
            SeriesQid = hydration.SeriesQid,
            CollectionId = hydration.CollectionId,
            SeriesLabel = hydration.SeriesLabel,
            ManifestSource = hydration.ManifestSource,
            ManifestVersion = hydration.ManifestVersion,
            ManifestHash = hydration.ManifestHash,
            KnownItemQidsHash = hydration.KnownItemQidsHash,
            WarningsJson = hydration.WarningsJson,
            ApiMetadataJson = hydration.ApiMetadataJson,
            LastHydratedAt = hydration.LastHydratedAt,
            CreatedAt = hydration.CreatedAt,
            UpdatedAt = now,
        };
    }

    public static SeriesManifestItemRecord WithOwnership(
        this SeriesManifestItemRecord item,
        Guid collectionId,
        string ownershipState,
        Guid? linkedWorkId)
    {
        return new SeriesManifestItemRecord
        {
            Id = item.Id,
            CollectionId = collectionId,
            SeriesQid = item.SeriesQid,
            ItemQid = item.ItemQid,
            ItemLabel = item.ItemLabel,
            ItemDescription = item.ItemDescription,
            MediaType = item.MediaType,
            MediaKind = item.MediaKind,
            InstanceOfQidsJson = item.InstanceOfQidsJson,
            RawOrdinal = item.RawOrdinal,
            ParsedOrdinal = item.ParsedOrdinal,
            OrdinalScopeQid = item.OrdinalScopeQid,
            SortOrder = item.SortOrder,
            PublicationDate = item.PublicationDate,
            PreviousQid = item.PreviousQid,
            NextQid = item.NextQid,
            ParentCollectionQid = item.ParentCollectionQid,
            ParentCollectionLabel = item.ParentCollectionLabel,
            IsCollection = item.IsCollection,
            IsExpandedFromCollection = item.IsExpandedFromCollection,
            MembershipScope = item.MembershipScope,
            SourcePropertiesJson = item.SourcePropertiesJson,
            RelationshipsJson = item.RelationshipsJson,
            OrderSource = item.OrderSource,
            OwnershipState = ownershipState,
            LinkedWorkId = linkedWorkId,
            LastHydratedAt = item.LastHydratedAt,
            CreatedAt = item.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}


using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using static MediaEngine.Api.Services.ReadServices.UniverseConnectionsReadService;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Builds the one read behind the Dashboard's Universe page. The shelves and counts come from
/// <see cref="UniverseClientReadService"/> (the same read the helper apps get, so order and totals match); the
/// Connections (Characters, Cast and Adaptations, each row with its source) come from the same build, and the detail's
/// own Cast and Adaptations are left empty so the rows are not sent twice. This adds what only the Dashboard shows: the
/// Universe's own standalone works, the missing entries per Series the profile chose to see, how far enrichment has
/// got, and the size of the connection graph. Everything is limited to what the profile may see.
/// </summary>
public sealed class UniversePageReadService(
    UniverseClientReadService universes,
    ICollectionRepository collections,
    DisplayCardBuilder cards,
    ISeriesManifestRepository manifests,
    IProfileSequencePreferencesRepository preferences,
    IConfigurationLoader configuration,
    IFictionalEntityRepository entities,
    IEntityRelationshipRepository relationships,
    IUniverseEnrichmentStatusRepository enrichment,
    ILogger<UniversePageReadService> logger)
{
    /// <summary>Title of the shelf of works that belong directly to the Universe.</summary>
    public const string StandaloneTitle = "Standalone";

    /// <summary>Null when the QID is malformed, names no Universe, or the profile may not see any of it.</summary>
    public async Task<UniversePageDto?> LoadAsync(string qid, Profile? profile, CancellationToken ct = default)
    {
        // The helper-app read plus the Universe's own standalone works: a Universe whose only visible works are standalone is
        // still a Universe. The detail's shelves are exactly the helper-app answer; its Cast and Adaptations move to the
        // Connections, where each row names its source.
        var build = await universes.BuildAsync(
            qid, profile, includeStandalone: true, includeCharacters: true, ct).ConfigureAwait(false);
        if (build is null)
        {
            return null;
        }

        var allRows = build.AllRows;
        var standalone = BuildStandalone(build.StandaloneRows);
        var detail = WithPageTotals(build.Detail, standalone);
        var memberIds = detail.Shelves.Select(shelf => shelf.CollectionId).ToHashSet();
        var visibleWorkIds = allRows
            .Where(row => row.CollectionId is { } id && (memberIds.Contains(id) || id == build.UniverseId))
            .Select(row => row.WorkId)
            .Distinct()
            .ToList();
        var visibleWorkQids = await universes.LoadVisibleWorkQidsAsync(ct).ConfigureAwait(false);

        var missing = await BuildMissingAsync(detail, profile, ct).ConfigureAwait(false);
        var scope = await LoadVisibleUniverseScopeAsync(detail.Qid, entities, visibleWorkQids, ct).ConfigureAwait(false);
        var entityQids = scope.Entities
            .Select(entity => entity.WikidataQid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relationshipCount = entityQids.Count == 0
            ? 0
            : (await relationships.GetByUniverseAsync(entityQids, ct).ConfigureAwait(false)).Count;

        return new UniversePageDto
        {
            Detail = detail,
            Connections = build.Connections,
            Standalone = standalone?.Shelf,
            Missing = missing,
            Enrichment = await BuildEnrichmentAsync(visibleWorkIds, ct).ConfigureAwait(false),
            GraphEntityCount = scope.Entities.Count,
            GraphRelationshipCount = relationshipCount,
        };
    }

    private sealed record StandaloneWorks(DisplayShelfDto Shelf, int Total, IReadOnlyList<string> Kinds);

    private StandaloneWorks? BuildStandalone(IReadOnlyList<DisplayWorkRow> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var tvRows = rows.Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) == "TV").ToList();
        var cardList = new List<DisplayCardDto>();
        cardList.AddRange(cards.BuildTvShowCards(tvRows));
        cardList.AddRange(rows
            .Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) != "TV")
            .GroupBy(row => row.WorkId)
            .Select(group => group.First())
            .OrderBy(row => row.Year, StringComparer.Ordinal)
            .ThenBy(row => row.SortTitle ?? row.Title, StringComparer.OrdinalIgnoreCase)
            .Select(row => cards.FromWork(row, "collection", null)));
        if (cardList.Count == 0)
        {
            return null;
        }

        var shelf = new DisplayShelfDto(
            "standalone",
            StandaloneTitle,
            cardList.Count == 1 ? "1 item" : $"{cardList.Count} items",
            cardList.Take(UniverseClientReadService.MaxShelfItems).ToList(),
            null);
        var kinds = rows
            .Select(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new StandaloneWorks(shelf, cardList.Count, kinds);
    }

    // The page's totals count the standalone works too ("Series or formats in it"); the helper-app read is left as it was.
    // The page's Cast and Adaptations live in Connections, so the detail never repeats them.
    private static UniverseClientDetailDto WithPageTotals(UniverseClientDetailDto detail, StandaloneWorks? standalone)
    {
        var mediaTypes = detail.MediaTypes.ToList();
        foreach (var kind in standalone?.Kinds.Where(kind => !mediaTypes.Contains(kind, StringComparer.Ordinal)) ?? [])
        {
            mediaTypes.Add(kind);
        }

        return new UniverseClientDetailDto
        {
            Qid = detail.Qid,
            Name = detail.Name,
            Description = detail.Description,
            GroupType = detail.GroupType,
            ItemCount = detail.ItemCount + (standalone?.Total ?? 0),
            SeriesCount = detail.SeriesCount,
            MediaTypes = mediaTypes,
            Shelves = detail.Shelves,
            Cast = [],
            Adaptations = [],
        };
    }

    private async Task<UniverseEnrichmentStateDto> BuildEnrichmentAsync(
        IReadOnlyCollection<Guid> workIds,
        CancellationToken ct)
    {
        var progress = await enrichment.GetProgressAsync(workIds, ct).ConfigureAwait(false);
        var state = progress.WorkCount > 0 && progress.EnrichedWorkCount >= progress.WorkCount
            ? UniverseEnrichmentStateDto.Ready
            : progress.ActiveWorkCount > 0 || progress.EnrichedWorkCount > 0
                ? UniverseEnrichmentStateDto.Running
                : UniverseEnrichmentStateDto.NotStarted;
        return new UniverseEnrichmentStateDto
        {
            State = state,
            EnrichedWorkCount = progress.EnrichedWorkCount,
            WorkCount = progress.WorkCount,
        };
    }

    private async Task<IReadOnlyList<UniverseShelfMissingDto>> BuildMissingAsync(
        UniverseClientDetailDto detail,
        Profile? profile,
        CancellationToken ct)
    {
        var libraryPreferences = LoadLibraryPreferences();
        var seriesById = (await collections.GetByIdsAsync(detail.Shelves.Select(shelf => shelf.CollectionId), ct).ConfigureAwait(false))
            .ToDictionary(series => series.Id);
        var result = new List<UniverseShelfMissingDto>();
        foreach (var shelf in detail.Shelves)
        {
            var manifest = await manifests.GetViewByCollectionIdAsync(shelf.CollectionId, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            var mainSequence = manifest.Items
                .Where(item => !item.IsCollection
                    && string.Equals(item.MembershipScope, SeriesMembershipScopeNames.MainSequence, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.ParsedOrdinal ?? item.SortOrder ?? double.MaxValue)
                .ThenBy(item => item.ItemLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // The Series panel's "N missing": the manifest's own count, which also covers entries known only from an
            // authoritative total and so have no row to list.
            var missingCount = Math.Max(mainSequence.Count(IsMissing), manifest.MissingCount);
            if (missingCount == 0)
            {
                continue;
            }

            var showMissing = await ResolveShowMissingAsync(
                shelf, seriesById.GetValueOrDefault(shelf.CollectionId), manifest, missingCount, libraryPreferences, profile, ct).ConfigureAwait(false);
            result.Add(new UniverseShelfMissingDto
            {
                CollectionId = shelf.CollectionId,
                ShowMissing = showMissing,
                MissingCount = missingCount,
                Items = showMissing ? BuildMissingItems(shelf, mainSequence) : [],
            });
        }

        return result;
    }

    private static bool IsMissing(SeriesManifestItemDto item) =>
        string.Equals(item.OwnershipState, "Missing", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<UniverseMissingItemDto> BuildMissingItems(
        UniverseMemberShelfDto shelf,
        IReadOnlyList<SeriesManifestItemDto> mainSequence)
    {
        // A missing entry is placed after the last owned work of the run that comes before it, using only works the
        // shelf actually lists (so the reference is always a card the profile can see).
        var listedWorkIds = shelf.Shelf.Items
            .Where(card => card.WorkId.HasValue)
            .Select(card => card.WorkId!.Value)
            .ToHashSet();
        Guid? lastOwned = null;
        var items = new List<UniverseMissingItemDto>();
        foreach (var item in mainSequence)
        {
            if (IsMissing(item))
            {
                items.Add(new UniverseMissingItemDto
                {
                    Label = string.IsNullOrWhiteSpace(item.ItemLabel) ? item.ItemQid : item.ItemLabel!,
                    RawOrdinal = item.RawOrdinal,
                    ParsedOrdinal = item.ParsedOrdinal,
                    ItemQid = item.ItemQid,
                    AfterWorkId = lastOwned,
                });
            }
            else if (item.LinkedWorkId is { } workId && listedWorkIds.Contains(workId))
            {
                lastOwned = workId;
            }
        }

        return items;
    }

    /// <summary>
    /// The profile's choice for this Series, by the one shared rule (<see cref="SeriesMissingItemVisibility"/>, which the
    /// Series panel on item pages calls too): the policy of the Series' own media kind must be enabled, and then the
    /// profile's own choice wins (stored under the Series' collection id, else its QID), else the media default decides.
    /// </summary>
    private async Task<bool> ResolveShowMissingAsync(
        UniverseMemberShelfDto shelf,
        Collection? series,
        SeriesManifestViewDto manifest,
        int missingCount,
        LibraryPreferencesSettings? libraryPreferences,
        Profile? profile,
        CancellationToken ct)
    {
        // The Series' own media kind; a Series with none recorded falls back to the kind of its first card.
        var mediaKey = SeriesMissingItemVisibility.PolicyKey(series?.MediaType)
            ?? SeriesMissingItemVisibility.PolicyKey(shelf.Shelf.Items.FirstOrDefault()?.MediaType);
        if (mediaKey is null
            || libraryPreferences is null
            || !libraryPreferences.MissingItemDisplay.TryGetValue(mediaKey, out var policy))
        {
            return false;
        }

        bool? profileOverride = null;
        if (profile is not null && policy.Enabled)
        {
            foreach (var containerKey in SeriesMissingItemVisibility.OverrideKeys(shelf.CollectionId, manifest.SeriesQid))
            {
                var stored = await preferences.GetAsync(profile.Id, mediaKey, containerKey, ct).ConfigureAwait(false);
                if (stored is not null)
                {
                    profileOverride = stored.ShowMissing;
                    break;
                }
            }
        }

        return SeriesMissingItemVisibility.Resolve(policy.Enabled, policy.DefaultVisibility, profileOverride, missingCount);
    }

    private LibraryPreferencesSettings? LoadLibraryPreferences()
    {
        try
        {
            return configuration.LoadConfig<LibraryPreferencesSettings>("ui", "library-preferences");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException
            or MediaEngine.Storage.Configuration.ConfigValidationException)
        {
            // An unreadable preferences file must not take the Universe page down; missing entries stay hidden.
            logger.LogWarning(ex, "Universe page: library preferences could not be read; missing entries stay hidden.");
            return null;
        }
    }
}

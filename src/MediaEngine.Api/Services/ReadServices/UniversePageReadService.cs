using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using static MediaEngine.Api.Services.ReadServices.UniverseConnectionsReadService;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Builds the one read behind the Dashboard's Universe page. The shelves, counts, Cast and Adaptations come from
/// <see cref="UniverseClientReadService"/> (the same read the helper apps get, so order and totals match); this adds
/// what only the Dashboard shows: the Universe's own standalone works, the missing entries per Series the profile
/// chose to see, how far enrichment has got, and the size of the connection graph. Everything is limited to what the
/// profile may see.
/// </summary>
public sealed class UniversePageReadService(
    UniverseClientReadService universes,
    ICollectionRepository collections,
    IDisplayProjectionReadService display,
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
        var detail = await universes.LoadAsync(qid, profile, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return null;
        }

        var universe = await collections.FindUniverseByQidAsync(detail.Qid, ct).ConfigureAwait(false);
        var allRows = await display.LoadWorksAsync(ct).ConfigureAwait(false);

        var standaloneRows = universe is null
            ? []
            : allRows.Where(row => row.CollectionId == universe.Id).ToList();
        var memberIds = detail.Shelves.Select(shelf => shelf.CollectionId).ToHashSet();
        var visibleWorkIds = allRows
            .Where(row => row.CollectionId is { } id && (memberIds.Contains(id) || id == universe?.Id))
            .Select(row => row.WorkId)
            .Distinct()
            .ToList();
        var visibleWorkQids = allRows
            .Select(row => row.IdentityQid)
            .Where(workQid => !string.IsNullOrWhiteSpace(workQid))
            .Select(workQid => workQid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            Standalone = BuildStandalone(standaloneRows),
            Missing = missing,
            Enrichment = await BuildEnrichmentAsync(visibleWorkIds, ct).ConfigureAwait(false),
            GraphEntityCount = scope.Entities.Count,
            GraphRelationshipCount = relationshipCount,
        };
    }

    private DisplayShelfDto? BuildStandalone(IReadOnlyList<DisplayWorkRow> rows)
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

        return new DisplayShelfDto(
            "standalone",
            StandaloneTitle,
            cardList.Count == 1 ? "1 item" : $"{cardList.Count} items",
            cardList.Take(UniverseClientReadService.MaxShelfItems).ToList(),
            null);
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
                shelf, manifest, missingCount, libraryPreferences, profile, ct).ConfigureAwait(false);
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
    /// The profile's choice for this Series: its own override when it set one, otherwise the media default from the
    /// library preferences. A media type whose policy does not show missing entries at all never shows them. This is
    /// the rule the Series panel on item pages applies, read here once per Series.
    /// </summary>
    private async Task<bool> ResolveShowMissingAsync(
        UniverseMemberShelfDto shelf,
        SeriesManifestViewDto manifest,
        int missingCount,
        LibraryPreferencesSettings? libraryPreferences,
        Profile? profile,
        CancellationToken ct)
    {
        var mediaKey = PolicyKeyFor(shelf);
        if (mediaKey is null
            || libraryPreferences is null
            || !libraryPreferences.MissingItemDisplay.TryGetValue(mediaKey, out var policy)
            || !policy.Enabled
            || missingCount <= 0)
        {
            return false;
        }

        if (profile is not null)
        {
            // The Series panel keys the override by the Series' own identity: its QID, or its collection id.
            foreach (var containerKey in new[] { manifest.SeriesQid, shelf.CollectionId.ToString("D") })
            {
                if (string.IsNullOrWhiteSpace(containerKey))
                {
                    continue;
                }

                var overridden = await preferences.GetAsync(profile.Id, mediaKey, containerKey, ct).ConfigureAwait(false);
                if (overridden is not null)
                {
                    return overridden.ShowMissing;
                }
            }
        }

        return string.Equals(policy.DefaultVisibility, "shown", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PolicyKeyFor(UniverseMemberShelfDto shelf)
    {
        var mediaType = shelf.Shelf.Items.FirstOrDefault()?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        return DisplayMediaRules.NormalizeDisplayKind(mediaType) switch
        {
            "TV" => "tv",
            "Comic" => "comics",
            "Book" => "books",
            "Movie" => "movies",
            "Music" => "music",
            "Audiobook" => "audiobooks",
            _ => null,
        };
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

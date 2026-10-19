using System.Text.RegularExpressions;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using ManifestItem = MediaEngine.Domain.Entities.SeriesManifestItemRecord;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Builds the Universe detail a paired native app reads: the member Series (and TV shows) of one Universe as display
/// shelves. A Universe is a Collection of type Universe with no parent whose Wikidata QID is the rollup identity; its
/// member shelves are the Collections below it. Cards come from the same authorised display projection as
/// <c>/api/v1/display</c>, so the profile's content limit, libraries and features are already applied: an item the
/// profile may not see never becomes a card and is never counted. Only structure is read here; no graph edges,
/// entities or lore. The detail also carries hero totals, "Also in this world" rows per Series, and the read-only Cast and
/// Adaptations lists, every one limited to what the profile may see.
/// </summary>
public sealed partial class UniverseClientReadService(
    ICollectionRepository collections,
    IDisplayProjectionReadService display,
    DisplayCardBuilder cards,
    UniverseConnectionsReadService connections,
    ISeriesManifestRepository? manifests = null)
{
    /// <summary>Cards listed on each member shelf; <c>item_count</c> still reports the whole shelf.</summary>
    public const int MaxShelfItems = 24;

    private const int MaxDepth = 4;

    [GeneratedRegex("^Q[0-9]{1,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex QidPattern();

    /// <summary>Returns the trimmed QID when it is well formed (<c>Q</c> and digits), otherwise null.</summary>
    public static string? NormalizeQid(string? qid)
    {
        var trimmed = qid?.Trim();
        return trimmed is not null && QidPattern().IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// Returns null when the QID is malformed, names no Universe, the profile may not open it, or none of its shelves
    /// holds an item the profile may see. The caller answers all of those the same way.
    /// </summary>
    public async Task<UniverseClientDetailDto?> LoadAsync(string qid, Profile? profile, CancellationToken ct = default)
    {
        var normalized = qid.Trim();
        if (!QidPattern().IsMatch(normalized))
        {
            return null;
        }

        var universe = await collections.FindUniverseByQidAsync(normalized, ct).ConfigureAwait(false);
        if (universe is null
            || universe.CollectionType != CollectionType.Universe
            || universe.ParentCollectionId is not null
            || !universe.IsEnabled
            || !CollectionAccessPolicy.CanAccess(universe, profile))
        {
            return null;
        }

        var members = await LoadMemberCollectionsAsync(universe, profile, ct).ConfigureAwait(false);
        if (members.Count == 0)
        {
            return null;
        }

        var memberIds = members.Select(member => member.Id).ToHashSet();
        var allRows = await display.LoadWorksAsync(ct).ConfigureAwait(false);
        var rowsByCollection = allRows
            .Where(row => row.CollectionId is { } id && memberIds.Contains(id))
            .GroupBy(row => row.CollectionId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        // One read for every member shelf's manifest rows, not one per shelf.
        var placementsByShelf = await LoadManifestPlacementsAsync(members.Select(member => member.Id).ToList(), ct).ConfigureAwait(false);

        var shelves = new List<UniverseMemberShelfDto>();
        var mediaTypes = new List<string>();
        foreach (var member in members)
        {
            if (!rowsByCollection.TryGetValue(member.Id, out var rows))
            {
                continue;
            }

            var memberCards = BuildCards(
                rows,
                placementsByShelf.TryGetValue(member.Id, out var placements)
                    ? placements
                    : new Dictionary<Guid, ManifestItem>());
            if (memberCards.Run.Count == 0 && memberCards.Also.Count == 0)
            {
                continue;
            }

            foreach (var kind in memberCards.Kinds)
            {
                if (!mediaTypes.Contains(kind, StringComparer.Ordinal))
                {
                    mediaTypes.Add(kind);
                }
            }

            var isShow = rows.All(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) == "TV");
            var title = string.IsNullOrWhiteSpace(member.DisplayName) ? "Series" : member.DisplayName!;
            shelves.Add(new UniverseMemberShelfDto
            {
                CollectionId = member.Id,
                GroupType = isShow ? CollectionGroupTypes.Show : CollectionGroupTypes.Series,
                ItemCount = memberCards.Run.Count,
                Shelf = new DisplayShelfDto(
                    member.Id.ToString("D"),
                    title,
                    ItemsLabel(memberCards.Run.Count),
                    memberCards.Run.Take(MaxShelfItems).ToList(),
                    $"/api/v1/display/groups/{member.Id:D}"),
                AlsoInThisWorldCount = memberCards.Also.Count,
                AlsoInThisWorld = memberCards.Also.Count == 0
                    ? null
                    : new DisplayShelfDto(
                        $"{member.Id:D}:also-in-this-world",
                        "Also in this world",
                        ItemsLabel(memberCards.Also.Count),
                        memberCards.Also.Take(MaxShelfItems).ToList(),
                        null),
            });
        }

        if (shelves.Count == 0)
        {
            return null;
        }

        var visibleWorkQids = allRows
            .Select(row => row.IdentityQid)
            .Where(workQid => !string.IsNullOrWhiteSpace(workQid))
            .Select(workQid => workQid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var helperLists = await connections.GetHelperListsAsync(normalized, visibleWorkQids, ct).ConfigureAwait(false);

        return new UniverseClientDetailDto
        {
            Qid = normalized,
            Name = string.IsNullOrWhiteSpace(universe.DisplayName) ? normalized : universe.DisplayName!,
            Description = universe.Description,
            ItemCount = shelves.Sum(shelf => shelf.ItemCount + shelf.AlsoInThisWorldCount),
            SeriesCount = shelves.Count,
            MediaTypes = mediaTypes,
            Shelves = shelves,
            Cast = helperLists.Cast,
            Adaptations = helperLists.Adaptations,
        };
    }

    private static string ItemsLabel(int count) => count == 1 ? "1 item" : $"{count} items";

    private async Task<IReadOnlyList<Collection>> LoadMemberCollectionsAsync(
        Collection universe,
        Profile? profile,
        CancellationToken ct)
    {
        var members = new List<Collection>();
        var seen = new HashSet<Guid> { universe.Id };
        var level = new List<Collection> { universe };
        for (var depth = 0; depth < MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<Collection>();
            foreach (var parent in level)
            {
                foreach (var child in await collections.GetChildCollectionsAsync(parent.Id, ct).ConfigureAwait(false))
                {
                    if (!seen.Add(child.Id) || !child.IsEnabled || !CollectionAccessPolicy.CanAccess(child, profile))
                    {
                        continue;
                    }

                    next.Add(child);
                    members.Add(child);
                }
            }

            level = next;
        }

        return members;
    }

    /// <summary>The manifest row that places each owned work on this Series shelf, for the shared Series ordering.</summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, ManifestItem>>> LoadManifestPlacementsAsync(
        IReadOnlyCollection<Guid> shelfIds,
        CancellationToken ct)
    {
        if (manifests is null || shelfIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyDictionary<Guid, ManifestItem>>();
        }

        var rows = await manifests.GetLinkedItemsByCollectionIdsAsync(shelfIds, ct).ConfigureAwait(false);
        return rows
            .GroupBy(item => item.CollectionId)
            .ToDictionary(
                shelf => shelf.Key,
                shelf => (IReadOnlyDictionary<Guid, ManifestItem>)shelf
                    .GroupBy(item => item.LinkedWorkId!.Value)
                    .ToDictionary(
                        group => group.Key,
                        group => group
                            .OrderBy(item => item.IsExpandedFromCollection)
                            .ThenBy(item => SeriesPlacementRule.ScopeRank(item.MembershipScope))
                            .First()));
    }

    private ShelfCards BuildCards(
        IReadOnlyList<DisplayWorkRow> rows,
        IReadOnlyDictionary<Guid, ManifestItem> manifestByWork)
    {
        var run = new List<DisplayCardDto>();
        var also = new List<DisplayCardDto>();
        var kinds = new List<string>();
        var tvRows = rows.Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) == "TV").ToList();
        run.AddRange(cards.BuildTvShowCards(tvRows));
        if (tvRows.Count > 0)
        {
            kinds.Add("TV");
        }

        // The same Series ordering as the shelf and the item page: the manifest's ordinal, then the work's own decimal
        // position, then publication date, then title, with "Also in this world" members after the Series run.
        var seriesRows = rows
            .Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) != "TV")
            .GroupBy(row => row.WorkId)
            .Select(group => group.First())
            .ToList();
        var ordered = SeriesOrdering.Order(seriesRows, row =>
        {
            manifestByWork.TryGetValue(row.WorkId, out var placement);
            return SeriesOrdering.BuildInput(
                placement?.MembershipScope,
                placement?.ParsedOrdinal,
                placement?.OrdinalScopeQid,
                placement?.SeriesQid,
                DisplayMediaRules.ParseDouble(row.SeriesPosition),
                row.Year,
                row.SortTitle ?? row.Title);
        });
        foreach (var row in ordered)
        {
            var kind = DisplayMediaRules.NormalizeDisplayKind(row.MediaType);
            if (!kinds.Contains(kind, StringComparer.Ordinal))
            {
                kinds.Add(kind);
            }

            // The same split the item page uses: supplementary, collected and broader members are "Also in this world".
            manifestByWork.TryGetValue(row.WorkId, out var placement);
            var card = cards.FromWork(row, "collection", null);
            if (SeriesOrdering.IsAlsoInThisWorld(placement?.MembershipScope))
            {
                also.Add(card);
            }
            else
            {
                run.Add(card);
            }
        }

        return new ShelfCards(run, also, kinds);
    }

    private sealed record ShelfCards(
        IReadOnlyList<DisplayCardDto> Run,
        IReadOnlyList<DisplayCardDto> Also,
        IReadOnlyList<string> Kinds);
}

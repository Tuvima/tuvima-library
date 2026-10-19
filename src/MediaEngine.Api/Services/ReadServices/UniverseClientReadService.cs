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
using ManifestItem = MediaEngine.Domain.Models.SeriesManifestItemDto;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Builds the Universe detail a paired native app reads: the member Series (and TV shows) of one Universe as display
/// shelves. A Universe is a Collection of type Universe with no parent whose Wikidata QID is the rollup identity; its
/// member shelves are the Collections below it. Cards come from the same authorised display projection as
/// <c>/api/v1/display</c>, so the profile's content limit, libraries and features are already applied: an item the
/// profile may not see never becomes a card and is never counted. Only structure is read here; no graph edges,
/// entities or lore.
/// </summary>
public sealed partial class UniverseClientReadService(
    ICollectionRepository collections,
    IDisplayProjectionReadService display,
    DisplayCardBuilder cards,
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
        var rowsByCollection = (await display.LoadWorksAsync(ct).ConfigureAwait(false))
            .Where(row => row.CollectionId is { } id && memberIds.Contains(id))
            .GroupBy(row => row.CollectionId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var shelves = new List<UniverseMemberShelfDto>();
        foreach (var member in members)
        {
            if (!rowsByCollection.TryGetValue(member.Id, out var rows))
            {
                continue;
            }

            var memberCards = BuildCards(rows, await LoadManifestPlacementsAsync(member.Id, ct).ConfigureAwait(false));
            if (memberCards.Count == 0)
            {
                continue;
            }

            var isShow = rows.All(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) == "TV");
            var title = string.IsNullOrWhiteSpace(member.DisplayName) ? "Series" : member.DisplayName!;
            shelves.Add(new UniverseMemberShelfDto
            {
                CollectionId = member.Id,
                GroupType = isShow ? CollectionGroupTypes.Show : CollectionGroupTypes.Series,
                ItemCount = memberCards.Count,
                Shelf = new DisplayShelfDto(
                    member.Id.ToString("D"),
                    title,
                    memberCards.Count == 1 ? "1 item" : $"{memberCards.Count} items",
                    memberCards.Take(MaxShelfItems).ToList(),
                    $"/api/v1/display/groups/{member.Id:D}"),
            });
        }

        if (shelves.Count == 0)
        {
            return null;
        }

        return new UniverseClientDetailDto
        {
            Qid = normalized,
            Name = string.IsNullOrWhiteSpace(universe.DisplayName) ? normalized : universe.DisplayName!,
            Description = universe.Description,
            ItemCount = shelves.Sum(shelf => shelf.ItemCount),
            Shelves = shelves,
        };
    }

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
    private async Task<IReadOnlyDictionary<Guid, ManifestItem>> LoadManifestPlacementsAsync(Guid collectionId, CancellationToken ct)
    {
        if (manifests is null)
        {
            return new Dictionary<Guid, ManifestItem>();
        }

        var view = await manifests.GetViewByCollectionIdAsync(collectionId, ct).ConfigureAwait(false);
        return (view?.Items ?? [])
            .Where(item => item.LinkedWorkId.HasValue && !item.IsCollection)
            .GroupBy(item => item.LinkedWorkId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.IsExpandedFromCollection)
                    .ThenBy(item => SeriesPlacementRule.ScopeRank(item.MembershipScope))
                    .First());
    }

    private IReadOnlyList<DisplayCardDto> BuildCards(
        IReadOnlyList<DisplayWorkRow> rows,
        IReadOnlyDictionary<Guid, ManifestItem> manifestByWork)
    {
        var result = new List<DisplayCardDto>();
        var tvRows = rows.Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) == "TV").ToList();
        result.AddRange(cards.BuildTvShowCards(tvRows));
        // The same Series ordering as the shelf and the item page: the manifest's ordinal, then the work's own decimal
        // position, then publication date, then title, with "Also in this world" members after the Series run.
        var seriesRows = rows
            .Where(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType) != "TV")
            .GroupBy(row => row.WorkId)
            .Select(group => group.First())
            .ToList();
        result.AddRange(SeriesOrdering
            .Order(seriesRows, row =>
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
            })
            .Select(row => cards.FromWork(row, "collection", null)));
        return result;
    }
}

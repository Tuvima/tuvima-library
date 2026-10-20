using System.Text.RegularExpressions;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
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
    ISeriesManifestRepository? manifests = null,
    UniverseLinkCache? linkCache = null,
    ICanonicalValueArrayRepository? arrays = null)
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
    public async Task<UniverseClientDetailDto?> LoadAsync(string qid, Profile? profile, CancellationToken ct = default) =>
        (await BuildAsync(qid, profile, includeStandalone: false, ct).ConfigureAwait(false))?.Detail;

    /// <summary>A Universe's detail, plus the visible works that belong directly to the Universe and to no Series.</summary>
    internal sealed record UniverseBuild(
        UniverseClientDetailDto Detail,
        Guid UniverseId,
        IReadOnlyList<DisplayWorkRow> AllRows,
        IReadOnlyList<DisplayWorkRow> StandaloneRows);

    /// <summary>
    /// The Universe detail <see cref="LoadAsync"/> returns (its shelves, totals and lists count Series members only), and,
    /// when <paramref name="includeStandalone"/> is set, the standalone works too. Without them the answer is the helper-app
    /// answer exactly: null when no Series shelf has a visible item. With them, a Universe whose only visible works are
    /// standalone is still a Universe (empty shelves, the standalone rows returned beside the detail).
    /// </summary>
    internal async Task<UniverseBuild?> BuildAsync(string qid, Profile? profile, bool includeStandalone, CancellationToken ct)
    {
        var normalized = qid.Trim();
        var scope = await ResolveVisibleUniverseAsync(normalized, profile, includeStandalone, ct).ConfigureAwait(false);
        if (scope is null)
        {
            return null;
        }

        var (universe, members) = scope.Value;
        var memberIds = members.Select(member => member.Id).ToHashSet();
        var allRows = await display.LoadWorksAsync(ct).ConfigureAwait(false);
        var standaloneRows = includeStandalone ? StandaloneRowsOf(universe, allRows) : [];
        var rowsByCollection = allRows
            .Where(row => row.CollectionId is { } id && memberIds.Contains(id))
            .GroupBy(row => row.CollectionId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        // One read for every member shelf's manifest rows, not one per shelf.
        var placementsByShelf = await SeriesShelfSplit.LoadPlacementsAsync(manifests, members.Select(member => member.Id).ToList(), ct).ConfigureAwait(false);
        var seriesPairs = await SeriesShelfSplit.LoadSeriesPairsAsync(arrays, rowsByCollection.Values.SelectMany(rows => rows).ToList(), ct).ConfigureAwait(false);

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
                    : new Dictionary<Guid, ManifestItem>(),
                member.WikidataQid,
                seriesPairs);
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

        if (shelves.Count == 0 && standaloneRows.Count == 0)
        {
            return null;
        }

        var visibleWorkQids = allRows
            .Select(row => row.IdentityQid)
            .Where(workQid => !string.IsNullOrWhiteSpace(workQid))
            .Select(workQid => workQid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var helperLists = await connections.GetHelperListsAsync(normalized, visibleWorkQids, ct).ConfigureAwait(false);

        return new UniverseBuild(
            new UniverseClientDetailDto
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
            },
            universe.Id,
            allRows,
            standaloneRows);
    }

    // Works that belong directly to the Universe collection itself, not to any Series below it.
    private static List<DisplayWorkRow> StandaloneRowsOf(Collection universe, IReadOnlyList<DisplayWorkRow> allRows) =>
        allRows.Where(row => row.CollectionId == universe.Id).ToList();

    /// <summary>
    /// How many Series and formats of one Universe the profile can see, and its name, without building any card.
    /// Uses the same Universe, member and visibility rules as <see cref="LoadAsync"/>, and the Universe's standalone works
    /// count toward the formats (they are in the Universe even though they are in no Series). Null when nothing in the
    /// Universe is visible to the profile.
    /// </summary>
    public async Task<(int SeriesCount, int FormatCount, string Name)?> GetMembershipSummaryAsync(
        string qid,
        Profile? profile,
        CancellationToken ct = default)
    {
        var normalized = qid.Trim();
        var scope = await ResolveVisibleUniverseAsync(normalized, profile, allowNoMembers: true, ct).ConfigureAwait(false);
        if (scope is null)
        {
            return null;
        }

        var (universe, members) = scope.Value;
        var memberIds = members.Select(member => member.Id).ToHashSet();
        var allRows = await display.LoadWorksAsync(ct).ConfigureAwait(false);
        var rowsByCollection = allRows
            .Where(row => row.CollectionId is { } id && memberIds.Contains(id))
            .GroupBy(row => row.CollectionId!.Value)
            .ToList();
        var standaloneRows = StandaloneRowsOf(universe, allRows);
        if (rowsByCollection.Count == 0 && standaloneRows.Count == 0)
        {
            return null;
        }

        var formats = rowsByCollection
            .SelectMany(group => group)
            .Concat(standaloneRows)
            .Select(row => DisplayMediaRules.NormalizeDisplayKind(row.MediaType))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var name = string.IsNullOrWhiteSpace(universe.DisplayName) ? normalized : universe.DisplayName!;
        return (rowsByCollection.Count, formats, name);
    }

    /// <summary>
    /// The "Part of the {name} Universe" link for an item page: present only when the profile can see this Universe
    /// and the Library holds two or more Series, or two or more formats, in it. Otherwise null.
    /// </summary>
    public async Task<UniverseLinkViewModel?> GetUniverseLinkAsync(
        string qid,
        Profile? profile,
        CancellationToken ct = default)
    {
        var normalized = NormalizeQid(qid);
        if (normalized is null)
        {
            return null;
        }

        // The visibility rule needs the profile's whole authorised projection, which every item page would otherwise
        // read again; the answer is remembered per profile and Universe for a minute (see UniverseLinkCache).
        return linkCache is null
            ? await LoadLinkAsync(normalized, profile, ct).ConfigureAwait(false)
            : await linkCache.GetOrLoadAsync(profile?.Id, normalized, () => LoadLinkAsync(normalized, profile, ct)).ConfigureAwait(false);
    }

    private async Task<UniverseLinkViewModel?> LoadLinkAsync(string normalizedQid, Profile? profile, CancellationToken ct)
    {
        if (await GetMembershipSummaryAsync(normalizedQid, profile, ct).ConfigureAwait(false) is not { } summary
            || !ShowsPartOfLine(summary.SeriesCount, summary.FormatCount))
        {
            return null;
        }

        return new UniverseLinkViewModel { Qid = normalizedQid, Name = summary.Name };
    }

    /// <summary>The one rule for the "Part of" line: two or more Series, or two or more formats, in the Universe.</summary>
    public static bool ShowsPartOfLine(int seriesCount, int formatCount) => seriesCount >= 2 || formatCount >= 2;

    /// <summary>The Universe the profile may open and the member Series below it, or null when there is none.</summary>
    private async Task<(Collection Universe, IReadOnlyList<Collection> Members)?> ResolveVisibleUniverseAsync(
        string normalizedQid,
        Profile? profile,
        bool allowNoMembers,
        CancellationToken ct)
    {
        if (!QidPattern().IsMatch(normalizedQid))
        {
            return null;
        }

        var universe = await FindOpenUniverseAsync(normalizedQid, profile, ct).ConfigureAwait(false);
        if (universe is null)
        {
            return null;
        }

        var members = await LoadMemberCollectionsAsync(universe, profile, ct).ConfigureAwait(false);
        return members.Count == 0 && !allowNoMembers ? null : (universe, members);
    }

    /// <summary>
    /// The visible works that sit on the member Series or shows of one Universe: the rows the Universe page's shelves are
    /// built from. Empty when the QID names no Universe the profile may open. Rows of the same work or show may repeat
    /// (a TV show has a row per episode); the caller groups them.
    /// </summary>
    internal async Task<IReadOnlyList<DisplayWorkRow>> LoadMemberWorkRowsAsync(
        string qid,
        Profile? profile,
        IReadOnlyList<DisplayWorkRow> visibleRows,
        CancellationToken ct = default)
    {
        var normalized = NormalizeQid(qid);
        var universe = normalized is null
            ? null
            : await FindOpenUniverseAsync(normalized, profile, ct).ConfigureAwait(false);
        if (universe is null)
        {
            return [];
        }

        var memberIds = (await LoadMemberCollectionsAsync(universe, profile, ct).ConfigureAwait(false))
            .Select(member => member.Id)
            .ToHashSet();
        return visibleRows
            .Where(row => row.CollectionId is { } id && memberIds.Contains(id))
            .ToList();
    }

    private async Task<Collection?> FindOpenUniverseAsync(string normalizedQid, Profile? profile, CancellationToken ct)
    {
        var universe = await collections.FindUniverseByQidAsync(normalizedQid, ct).ConfigureAwait(false);
        return universe is null
            || universe.CollectionType != CollectionType.Universe
            || universe.ParentCollectionId is not null
            || !universe.IsEnabled
            || !CollectionAccessPolicy.CanAccess(universe, profile)
                ? null
                : universe;
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

    private ShelfCards BuildCards(
        IReadOnlyList<DisplayWorkRow> rows,
        IReadOnlyDictionary<Guid, ManifestItem> manifestByWork,
        string? seriesQid,
        IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalArrayEntry>> seriesPairs)
    {
        var split = SeriesShelfSplit.Split(rows, manifestByWork, seriesQid, seriesPairs);
        var run = new List<DisplayCardDto>(cards.BuildTvShowCards(split.TvRows));
        run.AddRange(split.Run.Select(row => cards.FromWork(row, "collection", null)));
        var also = split.Also.Select(row => cards.FromWork(row, "collection", null)).ToList();
        return new ShelfCards(run, also, split.Kinds);
    }

    private sealed record ShelfCards(
        IReadOnlyList<DisplayCardDto> Run,
        IReadOnlyList<DisplayCardDto> Also,
        IReadOnlyList<string> Kinds);
}

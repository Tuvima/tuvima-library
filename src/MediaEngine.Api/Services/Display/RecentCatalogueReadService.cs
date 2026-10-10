using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Display;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>Fresh uncached catalogue projection. Initial materialization trades memory for reuse of current grant filtering.</summary>
internal sealed class RecentCatalogueReadService(
    DisplayWorkProjectionReader reader,
    AuthorizedDisplayProjectionReadService authorization,
    DisplayCardBuilder cards,
    IDatabaseConnection database)
{
    public async Task<IReadOnlyList<DisplayRecentItemDto>> LoadAsync(string type, Guid profileId, DisplayRecentBoundary? boundary, int take, CancellationToken ct)
    {
        // Rank the shows, albums and works from a light listing, then load full rows only for the leading ones.
        // Content ratings are only known on full rows, so a page that comes up short widens to the next batch.
        var candidates = await authorization.FilterRecentCandidatesAsync(await reader.LoadRecentCandidatesAsync(ct), profileId, ct);
        var groups = RankGroups(candidates, type, boundary);
        ct.ThrowIfCancellationRequested();
        var additions = LoadAdditions(ct);
        var states = await authorization.LoadStatesAsync(profileId, null, ct);
        var progress = states.GroupBy(s => s.WorkId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.LastAccessed).First());
        var loaded = new List<DisplayWorkRow>();
        var loadedGroups = 0;
        var batch = Math.Max(take + 6, 24);
        while (true)
        {
            var next = groups.Skip(loadedGroups).Take(batch).ToList();
            if (next.Count > 0)
            {
                var filter = new DisplayRecentGroupFilter(
                    next.Where(g => g.IsRoot).Select(g => g.Id).ToList(),
                    next.Where(g => !g.IsRoot).Select(g => g.Id).ToList());
                var rows = await authorization.FilterRecentWorksAsync(await reader.LoadAsync(ct, groups: filter, lean: true), profileId, ct);
                ApplyAdditions(rows, additions);
                loaded.AddRange(rows);
                loadedGroups += next.Count;
            }
            var items = Compose(loaded, cards, type, boundary, take, progress);
            // Unloaded groups never sort above their listed time, so a full page that ends later than the next one is final.
            if (loadedGroups >= groups.Count
                || (items.Count >= take && items[take - 1].AddedAt > groups[loadedGroups].AddedAt))
            {
                return items;
            }
            batch *= 2;
        }
    }

    private Dictionary<(Guid WorkId, string LibraryId), Addition> LoadAdditions(CancellationToken ct)
    {
        using var connection = database.CreateConnection();
        var visibleAsset = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        return connection.Query<Addition>(new CommandDefinition($"""
            SELECT e.work_id AS WorkId, ma.library_id AS LibraryId, MAX(ma.presented_at) AS AddedAt
            FROM media_assets ma JOIN editions e ON e.id = ma.edition_id
            WHERE ma.status = 'Normal' AND ma.is_orphaned = 0 AND {visibleAsset}
            GROUP BY e.work_id, ma.library_id;
            """, cancellationToken: ct)).ToDictionary(a => (a.WorkId, a.LibraryId));
    }

    private static void ApplyAdditions(IReadOnlyList<DisplayWorkRow> rows, Dictionary<(Guid WorkId, string LibraryId), Addition> additions)
    {
        foreach (var row in rows)
        {
            if (row.LibraryId is { } libraryId && additions.TryGetValue((row.WorkId, libraryId), out var addition) && addition.AddedAt is { } at)
            {
                row.CreatedAt = at;
            }
        }
    }

    /// <summary>Mirrors how <see cref="Compose"/> groups rows into cards, ordered the way the shelf is.</summary>
    internal static IReadOnlyList<RecentGroup> RankGroups(IReadOnlyList<RecentCandidate> candidates, string type, DisplayRecentBoundary? boundary)
    {
        var groups = new Dictionary<(bool IsRoot, Guid Id), DateTimeOffset>();
        foreach (var candidate in candidates)
        {
            var include = type switch
            {
                "watch" => DisplayMediaRules.IsWatchKind(candidate.MediaType),
                "read" => DisplayMediaRules.IsReadKind(candidate.MediaType),
                "listen" => DisplayMediaRules.IsListenKind(candidate.MediaType),
                "all" => true,
                _ => false
            };
            if (!include)
            {
                continue;
            }
            var kind = DisplayMediaRules.NormalizeDisplayKind(candidate.MediaType);
            var key = kind switch
            {
                "TV" => (true, candidate.RootWorkId != Guid.Empty ? candidate.RootWorkId : candidate.CollectionId ?? candidate.WorkId),
                "Music" => (true, candidate.RootWorkId == Guid.Empty ? candidate.WorkId : candidate.RootWorkId),
                _ => (false, candidate.WorkId)
            };
            // No listed time means the real one comes from metadata claims: keep the group in play.
            var at = candidate.AddedAt ?? DateTimeOffset.MaxValue;
            groups[key] = groups.TryGetValue(key, out var existing) && existing > at ? existing : at;
        }
        return groups
            .Select(g => new RecentGroup(g.Key.IsRoot, g.Key.Id, g.Value, "catalogue:" + g.Key.Id.ToString("N")))
            .Where(g => g.AddedAt == DateTimeOffset.MaxValue || DisplayRecentCursor.IsAfter(g.AddedAt, g.Key, boundary))
            .OrderByDescending(g => g.AddedAt).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
    }

    internal sealed record RecentGroup(bool IsRoot, Guid Id, DateTimeOffset AddedAt, string Key);

    public static IReadOnlyList<DisplayRecentItemDto> Compose(IReadOnlyList<DisplayWorkRow> rows, DisplayCardBuilder cards, string type, DisplayRecentBoundary? boundary, int take, IReadOnlyDictionary<Guid, DisplayJourneyRow>? progress = null)
    {
        var filtered = rows.Where(r => type switch
        {
            "watch" => DisplayMediaRules.IsWatchKind(r.MediaType),
            "read" => DisplayMediaRules.IsReadKind(r.MediaType),
            "listen" => DisplayMediaRules.IsListenKind(r.MediaType),
            "all" => true,
            _ => false
        }).ToList();
        var result = new List<DisplayCardDto>();
        result.AddRange(cards.BuildTvShowCards(filtered, progress));
        foreach (var group in filtered.Where(r => DisplayMediaRules.NormalizeDisplayKind(r.MediaType) == "Music")
                     .GroupBy(r => r.RootWorkId == Guid.Empty ? r.WorkId : r.RootWorkId))
        {
            var row = group.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.WorkId).ThenBy(r => r.AssetId).First();
            var card = cards.FromWork(row, "recent", null);
            var action = new DisplayActionDto("playAlbum", "Play Album", row.WorkId, row.AssetId, row.CollectionId, $"/details/musicalbum/{group.Key:D}?context=listen");
            result.Add(card with
            {
                Id = group.Key,
                Title = row.Album ?? row.Title,
                GroupingType = "album",
                PreferredShape = "square",
                Presentation = "album",
                Progress = null,
                Actions = [action],
                SortTimestamp = group.Max(r => r.CreatedAt),
                Subject = DisplaySubjectKind.Album
            });
        }
        result.AddRange(filtered.Where(r => DisplayMediaRules.NormalizeDisplayKind(r.MediaType) is not ("TV" or "Music"))
            .GroupBy(r => r.WorkId).Select(g => cards.FromWork(g.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.WorkId).ThenBy(r => r.AssetId).First(), "recent", progress?.GetValueOrDefault(g.Key))));
        return result.Select(card => new DisplayRecentItemDto("catalogue:" + card.Id.ToString("N"), card.SortTimestamp, card, null))
            .Where(item => DisplayRecentCursor.IsAfter(item.AddedAt, item.Key, boundary))
            .OrderByDescending(i => i.AddedAt).ThenBy(i => i.Key, StringComparer.Ordinal).Take(take).ToList();
    }
    private sealed class Addition
    {
        public Guid WorkId { get; init; }
        public string LibraryId { get; init; } = string.Empty;
        public DateTimeOffset? AddedAt { get; init; }
    }
}

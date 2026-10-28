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
    public async Task<IReadOnlyList<DisplayRecentItemDto>> LoadAsync(string type, Guid profileId, DisplayRecentBoundary? boundary, int take, CancellationToken ct, ISet<string>? availableTypes = null)
    {
        // Rank the shows, albums and works from a light listing, then load full rows only for the leading ones.
        // Content ratings are only known on full rows, so a page that comes up short widens to the next batch.
        var candidates = await authorization.FilterRecentCandidatesAsync(await reader.LoadRecentCandidatesAsync(ct), profileId, ct);
        if (availableTypes is not null)
        {
            // Which pills have anything behind them, whatever the current filter is.
            foreach (var part in new[] { "watch", "read", "listen" })
            {
                if (candidates.Any(candidate => DisplayRecentCursor.IncludesMedia(part, candidate.MediaType)))
                {
                    availableTypes.Add(part);
                }
            }
        }
        var groups = RankGroups(candidates, type, boundary);
        ct.ThrowIfCancellationRequested();
        var additions = LoadAdditions(ct);
        var states = await authorization.LoadStatesAsync(profileId, null, ct);
        var progress = states.GroupBy(s => s.WorkId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.LastAccessed).First());
        var loaded = new List<DisplayWorkRow>();
        var tvSummaries = new Dictionary<Guid, DisplayTvShowSummary>();
        var loadedGroups = 0;
        var batch = Math.Max(take + 6, 24);
        while (true)
        {
            var next = groups.Skip(loadedGroups).Take(batch).ToList();
            if (next.Count > 0)
            {
                // A show with many episodes only needs the few episode rows its card shows; the rest are counted from a light listing.
                var windowed = next.Where(g => g.IsRoot && g.IsTv && g.Members > TvWindowThreshold).ToList();
                var full = windowed.Count == 0 ? next : next.Except(windowed).ToList();
                var filter = new DisplayRecentGroupFilter(
                    full.Where(g => g.IsRoot).Select(g => g.Id).ToList(),
                    full.Where(g => !g.IsRoot).Select(g => g.Id).ToList());
                var rows = await authorization.FilterRecentWorksAsync(await reader.LoadAsync(ct, groups: filter, lean: true), profileId, ct);
                ApplyAdditions(rows, additions);
                loaded.AddRange(rows);
                if (windowed.Count > 0)
                {
                    await LoadTvWindowsAsync(windowed.Select(g => g.Id).ToList(), additions, progress, profileId, loaded, tvSummaries, ct);
                }
                loadedGroups += next.Count;
            }
            var items = Compose(loaded, cards, type, boundary, take, progress, tvSummaries);
            // Unloaded groups never sort above their listed time, so a full page that ends later than the next one is final.
            if (loadedGroups >= groups.Count
                || (items.Count >= take && items[take - 1].AddedAt > groups[loadedGroups].AddedAt))
            {
                return items;
            }
            batch *= 2;
        }
    }

    /// <summary>Shows with more episodes than this load only the episodes their card can show.</summary>
    internal const int TvWindowThreshold = 24;

    private async Task LoadTvWindowsAsync(
        IReadOnlyCollection<Guid> rootIds,
        Dictionary<(Guid WorkId, string LibraryId), Addition> additions,
        IReadOnlyDictionary<Guid, DisplayJourneyRow> progress,
        Guid profileId,
        List<DisplayWorkRow> loaded,
        Dictionary<Guid, DisplayTvShowSummary> summaries,
        CancellationToken ct)
    {
        // The same limits as full rows, so counts never include an episode the profile may not see.
        var skeleton = await authorization.FilterRecentWorksAsync(await reader.LoadTvEpisodeSkeletonAsync(rootIds, ct), profileId, ct);
        var episodes = skeleton
            .GroupBy(r => (r.WorkId, r.LibraryId))
            .Select(g => g.First())
            .ToList();
        ApplyAdditions(episodes, additions);
        var picked = new List<Guid>();
        var wholeShows = new List<Guid>();
        foreach (var show in episodes.GroupBy(r => r.RootWorkId))
        {
            var rows = show.ToList();
            summaries[show.Key] = DisplayCardBuilder.SummarizeTvShow(rows);
            var window = SelectTvWindow(rows, progress);
            if (window is null)
            {
                wholeShows.Add(show.Key);
            }
            else
            {
                picked.AddRange(window);
            }
        }
        var filter = new DisplayRecentGroupFilter(wholeShows, picked);
        var fullRows = await authorization.FilterRecentWorksAsync(await reader.LoadAsync(ct, groups: filter, lean: true), profileId, ct);
        ApplyAdditions(fullRows, additions);
        loaded.AddRange(fullRows);
        foreach (var root in wholeShows)
        {
            summaries.Remove(root);
        }
    }

    /// <summary>
    /// Episodes whose rows a show card can draw on: the first and last in order, the newest, the ones with
    /// viewing progress and the one after the current. Null means the order is unclear, so load the whole show.
    /// </summary>
    internal static IReadOnlyList<Guid>? SelectTvWindow(IReadOnlyList<DisplayWorkRow> episodes, IReadOnlyDictionary<Guid, DisplayJourneyRow> progress)
    {
        var unpositioned = episodes.Count(e => DisplayCardBuilder.TvEpisodeSortPosition(e) is null);
        if (unpositioned > 1)
        {
            return null;
        }
        var ordered = episodes
            .OrderBy(e => DisplayCardBuilder.TvEpisodeSortPosition(e) is null)
            .ThenBy(e => DisplayCardBuilder.TvEpisodeSortPosition(e) ?? double.MaxValue)
            .ThenBy(e => e.CreatedAt)
            .ThenBy(e => e.WorkId)
            .ToList();
        var chosen = new HashSet<Guid>();
        void Add(DisplayWorkRow? e)
        {
            if (e is not null)
            {
                chosen.Add(e.WorkId);
            }
        }
        foreach (var e in ordered.Take(2)) { Add(e); }
        Add(ordered[^1]);
        // Equal times keep the show's episode order, the way the card ranks them.
        foreach (var e in ordered.OrderByDescending(e => e.CreatedAt).Take(4)) { Add(e); }
        Add(ordered.FirstOrDefault(e => !progress.TryGetValue(e.WorkId, out var p) || p.ProgressPct < 99.5));
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!progress.TryGetValue(ordered[i].WorkId, out var state))
            {
                continue;
            }
            Add(ordered[i]);
            if (state.ProgressPct is > 0 and < 99.5 && i + 1 < ordered.Count)
            {
                Add(ordered[i + 1]);
            }
        }
        // Episodes sharing a position with a pick sort by title, which this listing does not have: keep them together.
        var positions = ordered.Where(e => chosen.Contains(e.WorkId)).Select(DisplayCardBuilder.TvEpisodeSortPosition).Where(p => p is not null).ToHashSet();
        foreach (var e in ordered.Where(e => DisplayCardBuilder.TvEpisodeSortPosition(e) is { } p && positions.Contains(p)))
        {
            Add(e);
        }
        return chosen.ToList();
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
        var members = new Dictionary<(bool IsRoot, Guid Id), HashSet<Guid>>();
        var tvGroups = new HashSet<(bool IsRoot, Guid Id)>();
        foreach (var candidate in candidates)
        {
            if (!DisplayRecentCursor.IncludesMedia(type, candidate.MediaType))
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
            if (!members.TryGetValue(key, out var set))
            {
                members[key] = set = [];
            }
            set.Add(candidate.WorkId);
            if (kind == "TV")
            {
                tvGroups.Add(key);
            }
        }
        return groups
            .Select(g => new RecentGroup(g.Key.IsRoot, g.Key.Id, g.Value, "catalogue:" + g.Key.Id.ToString("N"), tvGroups.Contains(g.Key), members[g.Key].Count))
            .Where(g => g.AddedAt == DateTimeOffset.MaxValue || DisplayRecentCursor.IsAfter(g.AddedAt, g.Key, boundary))
            .OrderByDescending(g => g.AddedAt).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
    }

    internal sealed record RecentGroup(bool IsRoot, Guid Id, DateTimeOffset AddedAt, string Key, bool IsTv = false, int Members = 0);

    public static IReadOnlyList<DisplayRecentItemDto> Compose(IReadOnlyList<DisplayWorkRow> rows, DisplayCardBuilder cards, string type, DisplayRecentBoundary? boundary, int take, IReadOnlyDictionary<Guid, DisplayJourneyRow>? progress = null, IReadOnlyDictionary<Guid, DisplayTvShowSummary>? tvSummaries = null)
    {
        var filtered = rows.Where(r => DisplayRecentCursor.IncludesMedia(type, r.MediaType)).ToList();
        var result = new List<DisplayCardDto>();
        result.AddRange(cards.BuildTvShowCards(filtered, progress, tvSummaries));
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

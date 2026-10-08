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
        var rows = await authorization.FilterRecentWorksAsync(await reader.LoadAsync(ct), profileId, ct);
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var visibleAsset = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var additions = connection.Query<Addition>(new CommandDefinition($"""
            SELECT e.work_id AS WorkId, ma.library_id AS LibraryId, MAX(ma.presented_at) AS AddedAt
            FROM media_assets ma JOIN editions e ON e.id = ma.edition_id
            WHERE ma.status = 'Normal' AND ma.is_orphaned = 0 AND {visibleAsset}
            GROUP BY e.work_id, ma.library_id;
            """, cancellationToken: ct)).ToDictionary(a => (a.WorkId, a.LibraryId));
        foreach (var row in rows)
        {
            if (row.LibraryId is { } libraryId && additions.TryGetValue((row.WorkId, libraryId), out var addition) && addition.AddedAt is { } at)
            {
                row.CreatedAt = at;
            }
        }
        var states = await authorization.LoadStatesAsync(profileId, null, ct);
        var progress = states.GroupBy(s => s.WorkId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.LastAccessed).First());
        return Compose(rows, cards, type, boundary, take, progress);
    }

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
            var row = group.OrderByDescending(r => r.CreatedAt).First();
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
            .GroupBy(r => r.WorkId).Select(g => cards.FromWork(g.OrderByDescending(r => r.CreatedAt).First(), "recent", progress?.GetValueOrDefault(g.Key))));
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

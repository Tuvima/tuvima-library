using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IPageBookmarkRepository"/>. A page bookmark is a row of the shared bookmark table with kind
/// <c>page</c> and the page number in <c>position_seconds</c>, so pooling, Promote and the editor's Bookmarks tab treat it like every other kind.
/// </summary>
public sealed class PageBookmarkRepository(IDatabaseConnection db) : IPageBookmarkRepository
{
    private const string Select = """
        SELECT b.id AS Id, b.profile_id AS ProfileId, b.asset_id AS AssetId, CAST(b.position_seconds AS INTEGER) AS Page,
               b.label AS Label, b.is_global AS IsGlobal, b.created_at AS CreatedAt, p.display_name AS OwnerName
        FROM audiobook_bookmarks b
        LEFT JOIN profiles p ON p.id = b.profile_id
        """;

    public Task<IReadOnlyList<PageBookmark>> ListByAssetAsync(string userId, Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParse(userId, out var profileId))
        {
            return Task.FromResult<IReadOnlyList<PageBookmark>>([]);
        }

        using var conn = db.CreateConnection();
        var rows = conn.Query<Row>(
            Select + """

            WHERE b.position_kind = 'page' AND b.asset_id = @assetId AND (b.profile_id = @profileId OR b.is_global = 1)
            ORDER BY b.position_seconds, b.created_at;
            """, new { profileId, assetId }).AsList();
        return Task.FromResult<IReadOnlyList<PageBookmark>>(rows.ConvertAll(Map));
    }

    public Task<PageBookmark?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QueryFirstOrDefault<Row>(Select + "\nWHERE b.position_kind = 'page' AND b.id = @id LIMIT 1;", new { id });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task InsertAsync(PageBookmark bookmark, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO audiobook_bookmarks
                    (id, profile_id, work_id, asset_id, position_seconds, label, is_global, created_at, position_kind, locator)
                SELECT @id, @profileId, e.work_id, @assetId, @page, @label, @isGlobal, @createdAt, 'page', @locator
                FROM media_assets ma
                JOIN editions e ON e.id = ma.edition_id
                WHERE ma.id = @assetId;
                """, new
            {
                id = bookmark.Id,
                profileId = Guid.Parse(bookmark.UserId),
                assetId = bookmark.AssetId,
                page = (double)bookmark.Page,
                locator = PageLocator.Format(bookmark.Page),
                label = bookmark.Label,
                isGlobal = bookmark.IsGlobal ? 1 : 0,
                createdAt = bookmark.CreatedAt.ToString("O"),
            }, tx);
            return 0;
        }, ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute("DELETE FROM audiobook_bookmarks WHERE id = @id AND position_kind = 'page';", new { id }, tx);
            return 0;
        }, ct);
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid ProfileId { get; set; }
        public Guid AssetId { get; set; }
        public int Page { get; set; }
        public string? Label { get; set; }
        public bool IsGlobal { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? OwnerName { get; set; }
    }

    private static PageBookmark Map(Row r) => new()
    {
        Id = r.Id,
        UserId = r.ProfileId.ToString("D"),
        AssetId = r.AssetId,
        Page = r.Page,
        Label = r.Label,
        IsGlobal = r.IsGlobal,
        OwnerName = r.OwnerName,
        CreatedAt = DateTime.Parse(r.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
    };
}

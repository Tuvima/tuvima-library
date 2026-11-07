using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IReaderBookmarkRepository"/>. Book bookmarks live in the one bookmark table beside the film and
/// audiobook ones (Global bookmarks spec D13): a row of kind <c>text</c> holds the reader location in <c>locator</c> and the chapter in
/// <c>chapter_index</c>, and has the same Global flag, so Promote and Everyone's work for books as for films.
/// </summary>
public sealed class ReaderBookmarkRepository : IReaderBookmarkRepository
{
    private const string Select = """
        SELECT b.id            AS Id,
               b.profile_id    AS ProfileId,
               b.asset_id      AS AssetId,
               b.chapter_index AS ChapterIndex,
               b.locator       AS CfiPosition,
               b.label         AS Label,
               b.is_global     AS IsGlobal,
               b.created_at    AS CreatedAt,
               p.display_name  AS OwnerName
        FROM   audiobook_bookmarks b
        LEFT JOIN profiles p ON p.id = b.profile_id
        """;

    private readonly IDatabaseConnection _db;

    public ReaderBookmarkRepository(IDatabaseConnection db) => _db = db;

    public Task<IReadOnlyList<ReaderBookmark>> ListByAssetAsync(string userId, Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParse(userId, out var profileId))
        {
            return Task.FromResult<IReadOnlyList<ReaderBookmark>>([]);
        }

        using var conn = _db.CreateConnection();
        var rows = conn.Query<BookmarkRow>(
            Select + """

            WHERE  b.position_kind = 'text' AND b.asset_id = @assetId AND (b.profile_id = @profileId OR b.is_global = 1)
            ORDER BY b.created_at DESC;
            """, new { profileId, assetId }).AsList();

        return Task.FromResult<IReadOnlyList<ReaderBookmark>>(rows.ConvertAll(MapRow));
    }

    public Task<ReaderBookmark?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<BookmarkRow>(
            Select + """

            WHERE  b.position_kind = 'text' AND b.id = @id
            LIMIT  1;
            """, new { id });

        return Task.FromResult(row is null ? null : MapRow(row));
    }

    public Task InsertAsync(ReaderBookmark bookmark, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO audiobook_bookmarks
                    (id, profile_id, work_id, asset_id, chapter_index, label, is_global, created_at, position_kind, locator)
                SELECT @id, @profileId, e.work_id, @assetId, @chapterIndex, @label, @isGlobal, @createdAt, 'text', @locator
                FROM media_assets ma
                JOIN editions e ON e.id = ma.edition_id
                WHERE ma.id = @assetId;
                """, new
            {
                id = bookmark.Id,
                profileId = Guid.Parse(bookmark.UserId),
                assetId = bookmark.AssetId,
                chapterIndex = bookmark.ChapterIndex,
                label = bookmark.Label,
                isGlobal = bookmark.IsGlobal ? 1 : 0,
                createdAt = bookmark.CreatedAt.ToString("O"),
                locator = bookmark.CfiPosition,
            }, tx);
            return 0;
        }, ct);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute("DELETE FROM audiobook_bookmarks WHERE id = @id AND position_kind = 'text';", new { id }, tx);
            return 0;
        }, ct);
    }

    // SQLite stores GUIDs as BLOBs and timestamps as TEXT. Dapper's registered handlers map GUID columns directly;
    // the timestamp remains a string here.
    private sealed class BookmarkRow
    {
        public Guid Id { get; set; }
        public Guid ProfileId { get; set; }
        public Guid AssetId { get; set; }
        public int? ChapterIndex { get; set; }
        public string? CfiPosition { get; set; }
        public string? Label { get; set; }
        public bool IsGlobal { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? OwnerName { get; set; }
    }

    private static ReaderBookmark MapRow(BookmarkRow r) => new()
    {
        Id = r.Id,
        UserId = r.ProfileId.ToString("D"),
        AssetId = r.AssetId,
        ChapterIndex = r.ChapterIndex ?? 0,
        CfiPosition = r.CfiPosition,
        Label = r.Label,
        IsGlobal = r.IsGlobal,
        OwnerName = r.OwnerName,
        CreatedAt = DateTime.Parse(r.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
    };
}

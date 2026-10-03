using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

public sealed class AudiobookBookmarkRepository
{
    private readonly IDatabaseConnection _db;

    public AudiobookBookmarkRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<AudiobookBookmarkDto>> GetByWorkAsync(
        Guid profileId,
        Guid workId,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        EnsureSchema(conn);
        var rows = conn.Query<BookmarkRow>(
            """
            SELECT id AS Id,
                   profile_id AS ProfileId,
                   work_id AS WorkId,
                   asset_id AS AssetId,
                   chapter_index AS ChapterIndex,
                   chapter_title AS ChapterTitle,
                   position_seconds AS PositionSeconds,
                   duration_seconds AS DurationSeconds,
                   label AS Label,
                   note AS Note,
                   created_at AS CreatedAt
            FROM audiobook_bookmarks
            WHERE profile_id = @profileId
              AND (@unrestricted = 1 OR asset_id IN @allowedAssets)
              AND work_id = @workId
            ORDER BY created_at DESC;
            """,
            new
            {
                profileId,
                workId,
                unrestricted = authorizedAssetIds is null ? 1 : 0,
                allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray()
            }).ToList();

        return Task.FromResult<IReadOnlyList<AudiobookBookmarkDto>>(rows.Select(ToDto).ToList());
    }

    public Task<AudiobookBookmarkDto> CreateAsync(
        Guid profileId,
        Guid workId,
        CreateAudiobookBookmarkRequestDto request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            EnsureSchema(conn);

            var bookmark = new BookmarkRow
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                WorkId = workId,
                AssetId = request.AssetId,
                ChapterIndex = request.ChapterIndex,
                ChapterTitle = BlankToNull(request.ChapterTitle),
                PositionSeconds = request.PositionSeconds,
                DurationSeconds = request.DurationSeconds,
                Label = BlankToNull(request.Label),
                Note = BlankToNull(request.Note),
                CreatedAt = DateTimeOffset.UtcNow,
            };

            conn.Execute(
                """
                INSERT INTO audiobook_bookmarks
                    (id, profile_id, work_id, asset_id, chapter_index, chapter_title,
                     position_seconds, duration_seconds, label, note, created_at)
                VALUES
                    (@Id, @ProfileId, @WorkId, @AssetId, @ChapterIndex, @ChapterTitle,
                     @PositionSeconds, @DurationSeconds, @Label, @Note, @CreatedAt);
                """,
                bookmark,
                tx);

            return ToDto(bookmark);
        }, ct);
    }

    public Task<bool> DeleteAsync(Guid profileId, Guid bookmarkId, CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            EnsureSchema(conn);
            var affected = conn.Execute(
                """
                DELETE FROM audiobook_bookmarks
                WHERE profile_id = @profileId
                  AND (@unrestricted = 1 OR asset_id IN @allowedAssets)
                  AND id = @bookmarkId;
                """,
                new
                {
                    profileId,
                    bookmarkId,
                    unrestricted = authorizedAssetIds is null ? 1 : 0,
                    allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray()
                },
                tx);
            return affected > 0;
        }, ct);
    }

    private static void EnsureSchema(System.Data.IDbConnection conn)
    {
        var columns = conn.Query<BookmarkSchemaColumn>("PRAGMA table_info(audiobook_bookmarks);")
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                "Audiobook bookmarks are not present in the initialized database schema. Recreate disposable development state from the current schema before using bookmarks.");
        }

        var required = new[]
        {
            "id", "profile_id", "work_id", "asset_id", "chapter_index", "chapter_title",
            "position_seconds", "duration_seconds", "label", "note", "created_at",
        };
        var missing = required.Where(column => !columns.Contains(column)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"The audiobook bookmark schema is obsolete or incomplete (missing: {string.Join(", ", missing)}). Recreate disposable development state from the current schema; runtime schema migration is not supported.");
        }
    }

    private sealed record BookmarkSchemaColumn
    {
        public string Name { get; init; } = string.Empty;
    }

    private static AudiobookBookmarkDto ToDto(BookmarkRow row) => new()
    {
        Id = row.Id,
        ProfileId = row.ProfileId,
        WorkId = row.WorkId,
        AssetId = row.AssetId,
        ChapterIndex = row.ChapterIndex,
        ChapterTitle = row.ChapterTitle,
        PositionSeconds = row.PositionSeconds,
        DurationSeconds = row.DurationSeconds,
        Label = row.Label,
        Note = row.Note,
        CreatedAt = row.CreatedAt,
    };

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record BookmarkRow
    {
        public Guid Id { get; init; }
        public Guid ProfileId { get; init; }
        public Guid WorkId { get; init; }
        public Guid AssetId { get; init; }
        public int? ChapterIndex { get; init; }
        public string? ChapterTitle { get; init; }
        public double PositionSeconds { get; init; }
        public double? DurationSeconds { get; init; }
        public string? Label { get; init; }
        public string? Note { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}

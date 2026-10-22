using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

/// <summary>
/// Saved spots in a video or an audiobook (Videos spec D43). The table and class keep their audiobook names because the
/// stored data is unchanged; a row works for any playable file. A personal bookmark belongs to the profile that made it;
/// a global one (<c>is_global</c>) is seen by everybody who can see the title and changed only by a server administrator.
/// </summary>
public sealed class AudiobookBookmarkRepository
{
    private readonly IDatabaseConnection _db;

    public AudiobookBookmarkRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    /// <summary>
    /// The profile's own bookmarks for the title plus every global one, newest first, limited to the files the caller may see
    /// (null means no limit). A file the caller cannot see (a Vault file without an unlocked session) contributes nothing.
    /// </summary>
    public Task<IReadOnlyList<AudiobookBookmarkDto>> GetByWorkAsync(
        Guid profileId,
        Guid workId,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
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
                   end_seconds AS EndSeconds,
                   label AS Label,
                   note AS Note,
                   description AS Description,
                   is_global AS IsGlobal,
                   created_at AS CreatedAt
            FROM audiobook_bookmarks
            WHERE (profile_id = @profileId OR is_global = 1)
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

    /// <summary>Saves a bookmark. The caller has already checked that a global one may be made (a server administrator).</summary>
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
                EndSeconds = request.EndSeconds,
                Label = BlankToNull(request.Label),
                Note = BlankToNull(request.Note),
                Description = BlankToNull(request.Description),
                IsGlobal = request.IsGlobal,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            conn.Execute(
                """
                INSERT INTO audiobook_bookmarks
                    (id, profile_id, work_id, asset_id, chapter_index, chapter_title,
                     position_seconds, duration_seconds, end_seconds, label, note, description, is_global, created_at)
                VALUES
                    (@Id, @ProfileId, @WorkId, @AssetId, @ChapterIndex, @ChapterTitle,
                     @PositionSeconds, @DurationSeconds, @EndSeconds, @Label, @Note, @Description, @IsGlobal, @CreatedAt);
                """,
                new
                {
                    bookmark.Id,
                    bookmark.ProfileId,
                    bookmark.WorkId,
                    bookmark.AssetId,
                    bookmark.ChapterIndex,
                    bookmark.ChapterTitle,
                    bookmark.PositionSeconds,
                    bookmark.DurationSeconds,
                    bookmark.EndSeconds,
                    bookmark.Label,
                    bookmark.Note,
                    bookmark.Description,
                    IsGlobal = bookmark.IsGlobal ? 1 : 0,
                    bookmark.CreatedAt,
                },
                tx);

            return ToDto(bookmark);
        }, ct);
    }

    /// <summary>
    /// Deletes the profile's own bookmark, or a global one when <paramref name="canChangeGlobal"/> is true. Anyone else's
    /// personal bookmark, and a global one without that permission, is left alone and reported as not found.
    /// </summary>
    public Task<bool> DeleteAsync(Guid profileId, Guid bookmarkId, CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null, bool canChangeGlobal = false)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var affected = conn.Execute(
                """
                DELETE FROM audiobook_bookmarks
                WHERE ((is_global = 0 AND profile_id = @profileId) OR (is_global = 1 AND @canChangeGlobal = 1))
                  AND (@unrestricted = 1 OR asset_id IN @allowedAssets)
                  AND id = @bookmarkId;
                """,
                new
                {
                    profileId,
                    bookmarkId,
                    canChangeGlobal = canChangeGlobal ? 1 : 0,
                    unrestricted = authorizedAssetIds is null ? 1 : 0,
                    allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray()
                },
                tx);
            return affected > 0;
        }, ct);
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
        EndSeconds = row.EndSeconds,
        Label = row.Label,
        Note = row.Note,
        Description = row.Description,
        IsGlobal = row.IsGlobal,
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
        public double? EndSeconds { get; init; }
        public string? Label { get; init; }
        public string? Note { get; init; }
        public string? Description { get; init; }
        public bool IsGlobal { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}

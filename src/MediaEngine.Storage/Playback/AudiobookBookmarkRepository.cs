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
              AND work_id = @workId AND position_kind = 'time'
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

    /// <summary>
    /// Every profile's personal bookmarks on the title in time order, each with the owner's display name (the administrator's Everyone's
    /// view). Global bookmarks are not included: they already show in the ordinary list. Limited to the files the caller may see.
    /// </summary>
    public Task<IReadOnlyList<AudiobookBookmarkDto>> GetEveryonesByWorkAsync(
        Guid workId,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<BookmarkRow>(
            """
            SELECT b.id AS Id,
                   b.profile_id AS ProfileId,
                   b.work_id AS WorkId,
                   b.asset_id AS AssetId,
                   b.chapter_index AS ChapterIndex,
                   b.chapter_title AS ChapterTitle,
                   b.position_seconds AS PositionSeconds,
                   b.duration_seconds AS DurationSeconds,
                   b.end_seconds AS EndSeconds,
                   b.label AS Label,
                   b.note AS Note,
                   b.description AS Description,
                   b.is_global AS IsGlobal,
                   b.created_at AS CreatedAt,
                   p.display_name AS OwnerName
            FROM audiobook_bookmarks b
            LEFT JOIN profiles p ON p.id = b.profile_id
            WHERE b.is_global = 0
              AND (@unrestricted = 1 OR b.asset_id IN @allowedAssets)
              AND b.work_id = @workId AND b.position_kind = 'time'
            ORDER BY b.position_seconds ASC, b.created_at ASC;
            """,
            new
            {
                workId,
                unrestricted = authorizedAssetIds is null ? 1 : 0,
                allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray()
            }).ToList();

        return Task.FromResult<IReadOnlyList<AudiobookBookmarkDto>>(rows.Select(ToDto).ToList());
    }

    /// <summary>
    /// Makes a personal bookmark Global at once (the caller has checked that they are a server administrator). Any personal bookmark
    /// of any profile on the same file within <paramref name="mergeWindowSeconds"/> of the spot is treated as the same bookmark and is
    /// replaced by the Global one, so nobody loses a bookmark. Returns null when there is no such personal bookmark.
    /// </summary>
    public Task<AudiobookBookmarkDto?> PromoteAsync(
        Guid bookmarkId,
        string? name,
        double mergeWindowSeconds,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync<AudiobookBookmarkDto?>((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var unrestricted = authorizedAssetIds is null ? 1 : 0;
            var allowed = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray();
            var row = conn.QuerySingleOrDefault<BookmarkRow>(
                """
                SELECT id AS Id, profile_id AS ProfileId, work_id AS WorkId, asset_id AS AssetId, chapter_index AS ChapterIndex,
                       chapter_title AS ChapterTitle, position_seconds AS PositionSeconds, duration_seconds AS DurationSeconds,
                       end_seconds AS EndSeconds, label AS Label, note AS Note, description AS Description,
                       is_global AS IsGlobal, created_at AS CreatedAt
                FROM audiobook_bookmarks
                WHERE id = @bookmarkId AND is_global = 0 AND position_kind = 'time'
                  AND (@unrestricted = 1 OR asset_id IN @allowedAssets);
                """,
                new { bookmarkId, unrestricted, allowedAssets = allowed },
                tx);
            if (row is null)
            {
                return null;
            }

            var label = BlankToNull(name) ?? row.Label;
            conn.Execute(
                """
                DELETE FROM audiobook_bookmarks
                WHERE is_global = 0 AND position_kind = 'time' AND id <> @bookmarkId AND asset_id = @assetId
                  AND ABS(position_seconds - @position) <= @window;
                """,
                new { bookmarkId, assetId = row.AssetId, position = row.PositionSeconds, window = mergeWindowSeconds },
                tx);
            conn.Execute(
                "UPDATE audiobook_bookmarks SET is_global = 1, label = @label WHERE id = @bookmarkId;",
                new { bookmarkId, label },
                tx);
            return ToDto(row with { IsGlobal = true, Label = label });
        }, ct);
    }

    /// <summary>Changes a Global bookmark's name, note and description (the caller has checked that they are a server administrator).</summary>
    public Task<AudiobookBookmarkDto?> UpdateGlobalAsync(
        Guid bookmarkId,
        string? label,
        string? note,
        string? description,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync<AudiobookBookmarkDto?>((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var affected = conn.Execute(
                """
                UPDATE audiobook_bookmarks
                SET label = @label, note = @note, description = @description
                WHERE id = @bookmarkId AND is_global = 1
                  AND (@unrestricted = 1 OR asset_id IN @allowedAssets);
                """,
                new
                {
                    bookmarkId,
                    label = BlankToNull(label),
                    note = BlankToNull(note),
                    description = BlankToNull(description),
                    unrestricted = authorizedAssetIds is null ? 1 : 0,
                    allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray()
                },
                tx);
            if (affected == 0)
            {
                return null;
            }

            var row = conn.QuerySingle<BookmarkRow>(
                """
                SELECT id AS Id, profile_id AS ProfileId, work_id AS WorkId, asset_id AS AssetId, chapter_index AS ChapterIndex,
                       chapter_title AS ChapterTitle, position_seconds AS PositionSeconds, duration_seconds AS DurationSeconds,
                       end_seconds AS EndSeconds, label AS Label, note AS Note, description AS Description,
                       is_global AS IsGlobal, created_at AS CreatedAt
                FROM audiobook_bookmarks WHERE id = @bookmarkId;
                """,
                new { bookmarkId },
                tx);
            return ToDto(row);
        }, ct);
    }

    /// <summary>One bookmark from a bookmark set, ready to be saved.</summary>
    public sealed record SetBookmark(
        double PositionSeconds, double? EndSeconds, string? ChapterTitle, string? Note, string? Description);

    /// <summary>What happened when a bookmark set was applied.</summary>
    public sealed record SetApplyOutcome(int Added, int Replaced);

    /// <summary>
    /// Applies a bookmark set in one write (Global bookmarks spec D8): the bookmarks in <paramref name="deleteIds"/> go, every
    /// bookmark in <paramref name="adds"/> is saved (Global when <paramref name="addAsGlobal"/>), and for Global adds any other
    /// profile's personal bookmark on the file within <paramref name="sameSpotSeconds"/> of an added one is replaced by it. Only
    /// the caller's own personal bookmarks and, for an administrator, Global ones can be deleted. With
    /// <paramref name="replaceGlobalSet"/> every Global bookmark on the file goes first.
    /// </summary>
    public Task<SetApplyOutcome> ApplySetAsync(
        Guid profileId,
        Guid workId,
        Guid assetId,
        double? durationSeconds,
        IReadOnlyList<SetBookmark> adds,
        IReadOnlyCollection<Guid> deleteIds,
        bool addAsGlobal,
        bool replaceGlobalSet,
        double sameSpotSeconds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var replaced = 0;
            if (replaceGlobalSet && addAsGlobal)
            {
                replaced += conn.Execute(
                    "DELETE FROM audiobook_bookmarks WHERE is_global = 1 AND position_kind = 'time' AND asset_id = @assetId;",
                    new { assetId },
                    tx);
            }

            foreach (var id in deleteIds)
            {
                replaced += conn.Execute(
                    """
                    DELETE FROM audiobook_bookmarks
                    WHERE id = @id AND asset_id = @assetId
                      AND ((is_global = 0 AND profile_id = @profileId) OR (is_global = 1 AND @addAsGlobal = 1));
                    """,
                    new { id, assetId, profileId, addAsGlobal = addAsGlobal ? 1 : 0 },
                    tx);
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var add in adds)
            {
                if (addAsGlobal)
                {
                    replaced += conn.Execute(
                        """
                        DELETE FROM audiobook_bookmarks
                        WHERE is_global = 0 AND position_kind = 'time' AND asset_id = @assetId AND ABS(position_seconds - @position) <= @window;
                        """,
                        new { assetId, position = add.PositionSeconds, window = sameSpotSeconds },
                        tx);
                }

                conn.Execute(
                    """
                    INSERT INTO audiobook_bookmarks
                        (id, profile_id, work_id, asset_id, chapter_index, chapter_title,
                         position_seconds, duration_seconds, end_seconds, label, note, description, is_global, created_at)
                    VALUES
                        (@Id, @profileId, @workId, @assetId, NULL, @ChapterTitle,
                         @PositionSeconds, @durationSeconds, @EndSeconds, NULL, @Note, @Description, @isGlobal, @now);
                    """,
                    new
                    {
                        Id = Guid.NewGuid(),
                        profileId,
                        workId,
                        assetId,
                        ChapterTitle = BlankToNull(add.ChapterTitle),
                        add.PositionSeconds,
                        durationSeconds,
                        add.EndSeconds,
                        Note = BlankToNull(add.Note),
                        Description = BlankToNull(add.Description),
                        isGlobal = addAsGlobal ? 1 : 0,
                        now,
                    },
                    tx);
            }

            return new SetApplyOutcome(adds.Count, replaced);
        }, ct);
    }

    private static AudiobookBookmarkDto ToDto(BookmarkRow row) => new()
    {
        OwnerName = row.OwnerName,
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
        public string? OwnerName { get; init; }
    }
}

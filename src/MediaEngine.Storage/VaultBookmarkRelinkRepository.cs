using Dapper;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>A Vault bookmark kept in a backup, waiting for the video it belongs to.</summary>
public sealed record VaultPendingBookmark(
    Guid Id,
    Guid ProfileId,
    bool IsGlobal,
    VaultRelinkIdentity Identity,
    double PositionSeconds,
    double? EndSeconds,
    double? DurationSeconds,
    int? ChapterIndex,
    string? ChapterTitle,
    double? ChapterStartSeconds,
    string? Label,
    string? Note,
    string? Description,
    string CreatedAt);

/// <summary>
/// Vault bookmarks waiting for their video after a restore (the backup keeps the bookmark and how to recognise the title, never the
/// Vault catalogue). The re-link reads the waiting rows and the Vault videos a rescan found, and moves each bookmark that matches into
/// the ordinary bookmark list.
/// </summary>
public sealed class VaultBookmarkRelinkRepository
{
    private readonly IDatabaseConnection _db;

    public VaultBookmarkRelinkRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    public Task<int> CountWaitingAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_pending_bookmarks;"));
    }

    public Task<IReadOnlyList<VaultPendingBookmark>> GetWaitingAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.CreateConnection();
        var rows = connection.Query<WaitingRow>(
            """
            SELECT id AS Id, profile_id AS ProfileId, is_global AS IsGlobal, identity_json AS IdentityJson,
                   position_seconds AS PositionSeconds, end_seconds AS EndSeconds, duration_seconds AS DurationSeconds,
                   chapter_index AS ChapterIndex, chapter_title AS ChapterTitle, chapter_start_seconds AS ChapterStartSeconds,
                   label AS Label, note AS Note, description AS Description, created_at AS CreatedAt
            FROM vault_pending_bookmarks
            ORDER BY created_at, id;
            """).ToList();
        return Task.FromResult<IReadOnlyList<VaultPendingBookmark>>(rows.Select(row => new VaultPendingBookmark(
            row.Id, row.ProfileId, row.IsGlobal != 0, VaultRelinkIdentityReader.Parse(row.IdentityJson),
            row.PositionSeconds, row.EndSeconds, row.DurationSeconds, row.ChapterIndex, row.ChapterTitle,
            row.ChapterStartSeconds, row.Label, row.Note, row.Description, row.CreatedAt)).ToList());
    }

    /// <summary>The Vault videos found so far, described the way the re-link recognises them.</summary>
    public Task<IReadOnlyList<VaultRelinkCandidate>> GetVaultVideosAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = _db.CreateConnection();
        var assetIds = connection.Query<Guid>(
            """
            SELECT ma.id
            FROM media_assets ma
            JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE
            ORDER BY ma.id;
            """).ToList();
        var candidates = VaultRelinkIdentityReader.Read(connection, assetIds);
        return Task.FromResult<IReadOnlyList<VaultRelinkCandidate>>(candidates.Values.OrderBy(candidate => candidate.AssetId).ToList());
    }

    /// <summary>
    /// Puts the bookmark on the video as an ordinary bookmark of the same person and removes it from the waiting list, in one step.
    /// Returns false when the bookmark was already handled or its person no longer exists.
    /// </summary>
    public Task<bool> LinkAsync(
        VaultPendingBookmark pending, VaultRelinkCandidate video, BookmarkPackBookmark placed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(placed);
        return _db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var removed = connection.Execute(
                "DELETE FROM vault_pending_bookmarks WHERE id = @id;", new { id = pending.Id }, transaction);
            if (removed == 0)
            {
                return false;
            }

            var personExists = connection.ExecuteScalar<int>(
                "SELECT EXISTS(SELECT 1 FROM profiles WHERE id = @profileId);", new { profileId = pending.ProfileId }, transaction) == 1;
            if (!personExists)
            {
                return false;
            }

            connection.Execute(
                """
                INSERT INTO audiobook_bookmarks
                    (id, profile_id, work_id, asset_id, chapter_index, chapter_title,
                     position_seconds, duration_seconds, end_seconds, label, note, description, is_global, created_at)
                VALUES
                    (@id, @profileId, @workId, @assetId, @chapterIndex, @chapterTitle,
                     @position, @duration, @end, @label, @note, @description, @isGlobal, @createdAt);
                """,
                new
                {
                    id = Guid.NewGuid(),
                    profileId = pending.ProfileId,
                    workId = video.WorkId,
                    assetId = video.AssetId,
                    chapterIndex = pending.ChapterIndex,
                    chapterTitle = placed.Chapter,
                    position = placed.Time,
                    duration = video.Identity.RunningTimeSeconds ?? pending.DurationSeconds,
                    end = placed.EndTime,
                    label = pending.Label,
                    note = placed.Note,
                    description = placed.Description,
                    isGlobal = pending.IsGlobal ? 1 : 0,
                    createdAt = pending.CreatedAt,
                },
                transaction);
            return true;
        }, ct);
    }

    private sealed class WaitingRow
    {
        public Guid Id { get; set; }

        public Guid ProfileId { get; set; }

        public int IsGlobal { get; set; }

        public string IdentityJson { get; set; } = string.Empty;

        public double PositionSeconds { get; set; }

        public double? EndSeconds { get; set; }

        public double? DurationSeconds { get; set; }

        public int? ChapterIndex { get; set; }

        public string? ChapterTitle { get; set; }

        public double? ChapterStartSeconds { get; set; }

        public string? Label { get; set; }

        public string? Note { get; set; }

        public string? Description { get; set; }

        public string CreatedAt { get; set; } = string.Empty;
    }
}

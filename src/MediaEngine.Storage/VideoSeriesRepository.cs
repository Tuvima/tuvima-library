using System.Globalization;
using System.Text;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

public sealed class VideoSeriesRepository : IVideoSeriesRepository
{
    private readonly IDatabaseConnection _db;

    public VideoSeriesRepository(IDatabaseConnection db) => _db = db;

    public Task<VideoSeriesMembership?> GetMembershipAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var self = conn.QuerySingleOrDefault<(Guid Id, Guid? ParentId, string MediaType)?>(
            "SELECT id AS Id, parent_work_id AS ParentId, media_type AS MediaType FROM works WHERE id = @workId;",
            new { workId });
        if (self is not { MediaType: "Videos" })
        {
            return Task.FromResult<VideoSeriesMembership?>(null);
        }

        var assetId = FirstAsset(conn, null, workId);
        string? title = null;
        if (self.Value.ParentId is { } parentId)
        {
            title = ChainTitle(conn, parentId);
        }

        return Task.FromResult<VideoSeriesMembership?>(new VideoSeriesMembership(workId, assetId, self.Value.ParentId, title));
    }

    public Task<IReadOnlyList<VideoSeriesTarget>> ListTargetsAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        // Only places in the video's own library: a series is never offered across libraries.
        var parents = conn.Query<Guid>(
            $"""
            SELECT p.id
            FROM works p
            WHERE p.media_type = 'Videos' AND p.work_kind = 'parent'
              AND NOT EXISTS (SELECT 1 FROM works s WHERE s.parent_work_id = p.id AND s.work_kind = 'parent')
              AND {SameLibrarySql("p.id")};
            """,
            new { workId }).ToList();

        var targets = parents
            .Select(id => new VideoSeriesTarget(
                id,
                ChainTitle(conn, id),
                conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE parent_work_id = @id AND work_kind = 'child';", new { id })))
            .OrderBy(target => target.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<VideoSeriesTarget>>(targets);
    }

    public async Task<VideoSeriesChange> PromoteAsync(Guid workId, Guid parentWorkId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // The write lock is taken before anything is read, and the works change commits or rolls back with its claims.
            return await _db.ExecuteWriteAsync(
                (conn, tx, _) => Promote(conn, tx, workId, parentWorkId, created: false, null),
                ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintError)
        {
            // Another writer took the position or the series name first; nothing was saved, so asking again works.
            return new VideoSeriesChange(VideoSeriesOutcome.Conflict);
        }
    }

    public async Task<VideoSeriesChange> PromoteToNewSeriesAsync(Guid workId, string seriesName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var name = string.Join(' ', (seriesName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
        {
            return new VideoSeriesChange(VideoSeriesOutcome.NameRequired);
        }

        try
        {
            return await _db.ExecuteWriteAsync(
                (conn, tx, _) => PromoteToNewSeries(conn, tx, workId, name),
                ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintError)
        {
            return new VideoSeriesChange(VideoSeriesOutcome.Conflict);
        }
    }

    public async Task<VideoSeriesChange> RemoveAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return await _db.ExecuteWriteAsync(
                (conn, tx, _) => Remove(conn, tx, workId),
                ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintError)
        {
            return new VideoSeriesChange(VideoSeriesOutcome.Conflict);
        }
    }

    private static VideoSeriesChange PromoteToNewSeries(SqliteConnection conn, SqliteTransaction tx, Guid workId, string name)
    {
        var kind = conn.QuerySingleOrDefault<(string Kind, string MediaType)?>(
            "SELECT work_kind AS Kind, media_type AS MediaType FROM works WHERE id = @workId;", new { workId }, tx);
        if (kind is not { MediaType: "Videos" })
        {
            return new VideoSeriesChange(VideoSeriesOutcome.NotAVideo);
        }

        if (kind.Value.Kind != "standalone")
        {
            return new VideoSeriesChange(VideoSeriesOutcome.AlreadyInSeries);
        }

        // The same key the scanner uses for a series folder, so a later folder of the same name joins this series.
        var key = "videos|" + NormalizeKey(name);
        var existing = conn.QueryFirstOrDefault<Guid?>(
            "SELECT id FROM works WHERE media_type = 'Videos' AND work_kind = 'parent' AND parent_key = @key LIMIT 1;", new { key }, tx);
        if (existing is { } existingId)
        {
            // A name that belongs to a series of another library is not joined from here.
            var sameLibrary = conn.ExecuteScalar<int>(
                $"SELECT CASE WHEN {SameLibrarySql("@existingId")} THEN 1 ELSE 0 END;", new { workId, existingId }, tx) == 1;
            return sameLibrary
                ? Promote(conn, tx, workId, existingId, created: false, null)
                : new VideoSeriesChange(VideoSeriesOutcome.TargetUnavailable);
        }

        var parentId = Guid.NewGuid();
        conn.Execute(
            """
            INSERT INTO works (id, collection_id, media_type, work_kind, parent_work_id, ordinal, is_catalog_only, parent_key, wikidata_status)
            VALUES (@parentId, NULL, 'Videos', 'parent', NULL, NULL, 0, @key, 'pending');
            """,
            new { parentId, key }, tx);
        return Promote(conn, tx, workId, parentId, created: true, name);
    }

    private static VideoSeriesChange Remove(SqliteConnection conn, SqliteTransaction tx, Guid workId)
    {
        var self = conn.QuerySingleOrDefault<(string Kind, Guid? ParentId, string MediaType)?>(
            "SELECT work_kind AS Kind, parent_work_id AS ParentId, media_type AS MediaType FROM works WHERE id = @workId;", new { workId }, tx);
        if (self is not { MediaType: "Videos" })
        {
            return new VideoSeriesChange(VideoSeriesOutcome.NotAVideo);
        }

        if (self.Value.Kind != "child" || self.Value.ParentId is null)
        {
            return new VideoSeriesChange(VideoSeriesOutcome.NotInSeries);
        }

        conn.Execute(
            "UPDATE works SET work_kind = 'standalone', parent_work_id = NULL, ordinal = NULL, ordinal_sort = NULL WHERE id = @workId;",
            new { workId }, tx);
        var assetId = FirstAsset(conn, tx, workId);
        if (assetId != Guid.Empty)
        {
            ClearSeriesFields(conn, tx, assetId);
        }

        return new VideoSeriesChange(VideoSeriesOutcome.Done, assetId);
    }

    /// <summary>
    /// Takes every series field off a video so it reads as standalone wherever it is shown. The locked claims written when it
    /// joined are deleted, claims a scan wrote for the same fields are marked superseded (so the next resolve cannot bring the
    /// old series back), and the resolved values are removed.
    /// </summary>
    private static void ClearSeriesFields(SqliteConnection conn, SqliteTransaction tx, Guid assetId)
    {
        conn.Execute(
            "DELETE FROM metadata_claims WHERE entity_id = @assetId AND is_user_locked = 1 AND claim_key IN @keys;",
            new { assetId, keys = SeriesFieldKeys }, tx);
        conn.Execute(
            """
            UPDATE metadata_claims SET is_current = 0, superseded_at = @now
            WHERE entity_id = @assetId AND is_current = 1 AND claim_key IN @keys;
            """,
            new { assetId, keys = SeriesFieldKeys, now = DateTimeOffset.UtcNow.ToString("O") }, tx);
        conn.Execute(
            "DELETE FROM canonical_values WHERE entity_id = @assetId AND key IN @keys;",
            new { assetId, keys = SeriesFieldKeys }, tx);
    }

    private static VideoSeriesChange Promote(
        SqliteConnection conn, SqliteTransaction tx, Guid workId, Guid parentWorkId, bool created, string? newSeriesName)
    {
        var self = conn.QuerySingleOrDefault<(string Kind, string MediaType)?>(
            "SELECT work_kind AS Kind, media_type AS MediaType FROM works WHERE id = @workId;", new { workId }, tx);
        if (self is not { MediaType: "Videos" })
        {
            return new VideoSeriesChange(VideoSeriesOutcome.NotAVideo);
        }

        if (self.Value.Kind != "standalone")
        {
            return new VideoSeriesChange(VideoSeriesOutcome.AlreadyInSeries);
        }

        // A place videos join holds videos directly: a series with sections takes them in a section, never beside the sections.
        var target = conn.QuerySingleOrDefault<(string Kind, string MediaType)?>(
            "SELECT work_kind AS Kind, media_type AS MediaType FROM works WHERE id = @parentWorkId;", new { parentWorkId }, tx);
        var hasSections = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM works WHERE parent_work_id = @parentWorkId AND work_kind = 'parent';", new { parentWorkId }, tx) > 0;
        if (target is not { Kind: "parent", MediaType: "Videos" } || hasSections)
        {
            return new VideoSeriesChange(VideoSeriesOutcome.TargetUnavailable);
        }

        // Never a series of another library, whatever id the caller sends.
        if (!created && conn.ExecuteScalar<int>(
                $"SELECT CASE WHEN {SameLibrarySql("@parentWorkId")} THEN 1 ELSE 0 END;", new { workId, parentWorkId }, tx) != 1)
        {
            return new VideoSeriesChange(VideoSeriesOutcome.TargetUnavailable);
        }

        var last = conn.ExecuteScalar<double?>(
            "SELECT MAX(ordinal_sort) FROM works WHERE parent_work_id = @parentWorkId AND work_kind = 'child';", new { parentWorkId }, tx) ?? 0d;
        var position = (int)Math.Floor(Math.Max(last, 0d)) + 1;
        conn.Execute(
            "UPDATE works SET work_kind = 'child', parent_work_id = @parentWorkId, ordinal = @position, ordinal_sort = @sort WHERE id = @workId;",
            new { parentWorkId, position, sort = (double)position, workId }, tx);

        var claims = new List<MetadataClaim>();
        var canonicals = new List<CanonicalValue>();
        var now = DateTimeOffset.UtcNow;
        void Lock(Guid entity, string key, string value)
        {
            claims.Add(new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = entity,
                ProviderId = WellKnownProviders.UserManual,
                ClaimKey = key,
                ClaimValue = value,
                ClaimedAt = now,
                Confidence = 1.0,
                IsUserLocked = true,
            });
            canonicals.Add(new CanonicalValue
            {
                EntityId = entity,
                Key = key,
                Value = value,
                LastScoredAt = now,
                WinningProviderId = WellKnownProviders.UserManual,
            });
        }

        if (created && !string.IsNullOrWhiteSpace(newSeriesName))
        {
            Lock(parentWorkId, MetadataFieldConstants.Title, newSeriesName);
            // The title is read back below, so it has to be there first.
            MetadataClaimRepository.InsertBatchInTransaction(conn, tx, claims);
            CanonicalValueRepository.UpsertBatchInTransaction(conn, tx, canonicals);
            claims.Clear();
            canonicals.Clear();
        }

        var seriesTitle = ChainTitle(conn, parentWorkId, tx);
        var assetId = FirstAsset(conn, tx, workId);
        if (assetId != Guid.Empty)
        {
            var text = position.ToString(CultureInfo.InvariantCulture);
            // The same names ingestion records, so Watch pages, Resume and Up Next treat the video as part of the series.
            Lock(assetId, MetadataFieldConstants.EpisodeNumber, text);
            Lock(assetId, MetadataFieldConstants.VideoEpisodeIndex, text);
            Lock(assetId, MetadataFieldConstants.VideoSeries, seriesTitle);
            Lock(assetId, MetadataFieldConstants.ShowName, seriesTitle);
            Lock(assetId, MetadataFieldConstants.SeasonNumber, "1");
            MetadataClaimRepository.InsertBatchInTransaction(conn, tx, claims);
            CanonicalValueRepository.UpsertBatchInTransaction(conn, tx, canonicals);
        }

        return new VideoSeriesChange(VideoSeriesOutcome.Done, assetId, parentWorkId, seriesTitle, position, created);
    }

    private const int SqliteConstraintError = 19;

    private static readonly string[] SeriesFieldKeys =
    [
        MetadataFieldConstants.VideoSeries,
        MetadataFieldConstants.VideoSeriesKey,
        MetadataFieldConstants.VideoSection,
        MetadataFieldConstants.VideoSectionKey,
        MetadataFieldConstants.VideoSectionIndex,
        MetadataFieldConstants.VideoEpisodeIndex,
        MetadataFieldConstants.ShowName,
        MetadataFieldConstants.SeasonNumber,
        MetadataFieldConstants.EpisodeNumber,
    ];

    /// <summary>
    /// A SQL condition (using <c>@workId</c>) that holds when the video's file is in the same library as the series at
    /// <paramref name="parentIdSql"/>. A series with no videos yet carries no library and can be joined.
    /// </summary>
    private static string SameLibrarySql(string parentIdSql) =>
        $"""
        (
            EXISTS (
                SELECT 1 FROM editions ve JOIN media_assets vma ON vma.edition_id = ve.id
                WHERE ve.work_id = @workId AND vma.status = 'Normal' AND vma.is_orphaned = 0)
            AND (
                NOT EXISTS (SELECT 1 FROM works kid WHERE kid.parent_work_id = {parentIdSql} AND kid.work_kind = 'child')
                OR EXISTS (
                    SELECT 1
                    FROM works kid
                    JOIN editions ke ON ke.work_id = kid.id
                    JOIN media_assets kma ON kma.edition_id = ke.id
                    JOIN editions ve ON ve.work_id = @workId
                    JOIN media_assets vma ON vma.edition_id = ve.id
                    WHERE kid.parent_work_id = {parentIdSql} AND kid.work_kind = 'child'
                      AND kma.status = 'Normal' AND kma.is_orphaned = 0
                      AND vma.status = 'Normal' AND vma.is_orphaned = 0
                      AND kma.library_id IS vma.library_id)
            )
        )
        """;

    private static Guid FirstAsset(System.Data.IDbConnection conn, System.Data.IDbTransaction? tx, Guid workId) =>
        conn.QueryFirstOrDefault<Guid?>(
            """
            SELECT MIN(ma.id) FROM editions e JOIN media_assets ma ON ma.edition_id = e.id
            WHERE e.work_id = @workId AND ma.status = 'Normal' AND ma.is_orphaned = 0;
            """,
            new { workId }, tx) ?? Guid.Empty;

    /// <summary>"Series" for a series, "Series: Section" for a section of one.</summary>
    private static string ChainTitle(System.Data.IDbConnection conn, Guid parentId, System.Data.IDbTransaction? tx = null)
    {
        string TitleOf(Guid id) => conn.QuerySingleOrDefault<string>(
            "SELECT value FROM canonical_values WHERE entity_id = @id AND key = 'title' LIMIT 1;", new { id }, tx) is { Length: > 0 } title
            ? title
            : "Videos";

        var grandparent = conn.QuerySingleOrDefault<Guid?>("SELECT parent_work_id FROM works WHERE id = @parentId;", new { parentId }, tx);
        return grandparent is { } series ? $"{TitleOf(series)}: {TitleOf(parentId)}" : TitleOf(parentId);
    }

    private static string NormalizeKey(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!previousSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousSpace = true;
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
            previousSpace = false;
        }

        return builder.ToString().TrimEnd();
    }
}

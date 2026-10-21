using System.Globalization;
using System.Text;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

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

    public Task<IReadOnlyList<VideoSeriesTarget>> ListTargetsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var parents = conn.Query<Guid>(
            """
            SELECT p.id
            FROM works p
            WHERE p.media_type = 'Videos' AND p.work_kind = 'parent'
              AND NOT EXISTS (SELECT 1 FROM works s WHERE s.parent_work_id = p.id AND s.work_kind = 'parent');
            """).ToList();

        var targets = parents
            .Select(id => new VideoSeriesTarget(
                id,
                ChainTitle(conn, id),
                conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE parent_work_id = @id AND work_kind = 'child';", new { id })))
            .OrderBy(target => target.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<VideoSeriesTarget>>(targets);
    }

    public Task<VideoSeriesChange> PromoteAsync(Guid workId, Guid parentWorkId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        var result = Promote(conn, tx, workId, parentWorkId, created: false);
        if (result.Outcome == VideoSeriesOutcome.Done)
        {
            tx.Commit();
        }

        return Task.FromResult(result);
    }

    public Task<VideoSeriesChange> PromoteToNewSeriesAsync(Guid workId, string seriesName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var name = string.Join(' ', (seriesName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
        {
            return Task.FromResult(new VideoSeriesChange(VideoSeriesOutcome.NameRequired));
        }

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        var kind = conn.QuerySingleOrDefault<(string Kind, string MediaType)?>(
            "SELECT work_kind AS Kind, media_type AS MediaType FROM works WHERE id = @workId;", new { workId }, tx);
        if (kind is not { MediaType: "Videos" })
        {
            return Task.FromResult(new VideoSeriesChange(VideoSeriesOutcome.NotAVideo));
        }

        if (kind.Value.Kind != "standalone")
        {
            return Task.FromResult(new VideoSeriesChange(VideoSeriesOutcome.AlreadyInSeries));
        }

        // The same key the scanner uses for a series folder, so a later folder of the same name joins this series.
        var key = "videos|" + NormalizeKey(name);
        var existing = conn.QueryFirstOrDefault<Guid?>(
            "SELECT id FROM works WHERE media_type = 'Videos' AND work_kind = 'parent' AND parent_key = @key LIMIT 1;", new { key }, tx);
        var created = existing is null;
        var parentId = existing ?? Guid.NewGuid();
        if (created)
        {
            conn.Execute(
                """
                INSERT INTO works (id, collection_id, media_type, work_kind, parent_work_id, ordinal, is_catalog_only, parent_key, wikidata_status)
                VALUES (@parentId, NULL, 'Videos', 'parent', NULL, NULL, 0, @key, 'pending');
                """,
                new { parentId, key }, tx);
        }

        var result = Promote(conn, tx, workId, parentId, created);
        if (result.Outcome == VideoSeriesOutcome.Done)
        {
            tx.Commit();
            return Task.FromResult(result with { SeriesTitle = created ? name : result.SeriesTitle });
        }

        return Task.FromResult(result);
    }

    public Task<VideoSeriesChange> RemoveAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        var self = conn.QuerySingleOrDefault<(string Kind, Guid? ParentId, string MediaType)?>(
            "SELECT work_kind AS Kind, parent_work_id AS ParentId, media_type AS MediaType FROM works WHERE id = @workId;", new { workId }, tx);
        if (self is not { MediaType: "Videos" })
        {
            return Task.FromResult(new VideoSeriesChange(VideoSeriesOutcome.NotAVideo));
        }

        if (self.Value.Kind != "child" || self.Value.ParentId is null)
        {
            return Task.FromResult(new VideoSeriesChange(VideoSeriesOutcome.NotInSeries));
        }

        conn.Execute(
            "UPDATE works SET work_kind = 'standalone', parent_work_id = NULL, ordinal = NULL, ordinal_sort = NULL WHERE id = @workId;",
            new { workId }, tx);
        var assetId = FirstAsset(conn, tx, workId);
        tx.Commit();
        return Task.FromResult(new VideoSeriesChange(VideoSeriesOutcome.Done, assetId));
    }

    private static VideoSeriesChange Promote(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, Guid workId, Guid parentWorkId, bool created)
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

        var last = conn.ExecuteScalar<double?>(
            "SELECT MAX(ordinal_sort) FROM works WHERE parent_work_id = @parentWorkId AND work_kind = 'child';", new { parentWorkId }, tx) ?? 0d;
        var position = (int)Math.Floor(Math.Max(last, 0d)) + 1;
        conn.Execute(
            "UPDATE works SET work_kind = 'child', parent_work_id = @parentWorkId, ordinal = @position, ordinal_sort = @sort WHERE id = @workId;",
            new { parentWorkId, position, sort = (double)position, workId }, tx);

        return new VideoSeriesChange(
            VideoSeriesOutcome.Done, FirstAsset(conn, tx, workId), parentWorkId, ChainTitle(conn, parentWorkId, tx), position, created);
    }

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

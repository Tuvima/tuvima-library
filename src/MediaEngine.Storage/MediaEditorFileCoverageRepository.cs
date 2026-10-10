using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public enum FileCoverageOutcome
{
    Ok,
    NotFound,
    Invalid,
    Conflict,
}

public sealed record FileCoverageEpisode(
    Guid WorkId,
    int? EpisodeNumber,
    string Title,
    bool IsHost,
    bool IsCovered,
    bool OwnedByOtherFile);

public sealed record FileCoverageView(
    Guid AssetId,
    Guid HostWorkId,
    Guid HostEditionId,
    int MaxEpisodes,
    IReadOnlyList<FileCoverageEpisode> Episodes);

public sealed record FileCoverageResult(FileCoverageOutcome Outcome, string? Message, FileCoverageView? View);

/// <summary>
/// Reads and sets which episodes one TV file covers, for the editor's "This file covers" picker.
/// The file stays attached to its host episode; coverage never leaves the host's season.
/// </summary>
public sealed class MediaEditorFileCoverageRepository(IDatabaseConnection database)
{
    public const int MaxEpisodes = EpisodeRangeParser.MaxEpisodesPerFile;

    public Task<FileCoverageResult> GetAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = database.CreateConnection();
        return Task.FromResult(Load(conn, null, assetId));
    }

    public Task<FileCoverageResult> ReplaceAsync(
        Guid assetId,
        IReadOnlyList<Guid> workIds,
        CancellationToken ct = default)
    {
        if (assetId == Guid.Empty)
        {
            return Task.FromResult(new FileCoverageResult(FileCoverageOutcome.Invalid, "Choose a file.", null));
        }

        var requested = workIds.Distinct().ToList();
        if (requested.Count != workIds.Count || requested.Contains(Guid.Empty))
        {
            return Task.FromResult(new FileCoverageResult(
                FileCoverageOutcome.Invalid, "List each episode once.", null));
        }

        if (requested.Count > MaxEpisodes)
        {
            return Task.FromResult(new FileCoverageResult(
                FileCoverageOutcome.Invalid, $"A file can cover at most {MaxEpisodes} episodes.", null));
        }

        return database.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var current = Load(conn, tx, assetId);
            if (current.View is not { } view)
            {
                return current;
            }

            if (!requested.Contains(view.HostWorkId))
            {
                return new FileCoverageResult(
                    FileCoverageOutcome.Invalid, "The file's own episode must stay in the list.", null);
            }

            var season = view.Episodes.ToDictionary(episode => episode.WorkId);
            foreach (var workId in requested)
            {
                if (!season.TryGetValue(workId, out var episode))
                {
                    return new FileCoverageResult(
                        FileCoverageOutcome.Invalid, "A file can only cover episodes of its own season.", null);
                }

                if (episode.OwnedByOtherFile)
                {
                    return new FileCoverageResult(
                        FileCoverageOutcome.Conflict,
                        $"{Label(episode)} already belongs to another file.",
                        null);
                }
            }

            var existing = conn.Query<TimingRow>(
                "SELECT work_id AS WorkId, start_seconds AS Start, end_seconds AS End FROM media_asset_coverage WHERE asset_id=@assetId",
                new { assetId }, tx).ToDictionary(row => row.WorkId);

            // Positions follow episode order; a lone host episode is a normal single-episode file.
            var ordered = view.Episodes.Where(episode => requested.Contains(episode.WorkId)).ToList();
            var rows = new List<MediaAssetCoverage>();
            if (requested.Count > 1)
            {
                for (var index = 0; index < ordered.Count; index++)
                {
                    existing.TryGetValue(ordered[index].WorkId, out var kept);
                    rows.Add(new MediaAssetCoverage(
                        assetId, ordered[index].WorkId, index + 1, kept?.Start, kept?.End,
                        MediaAssetCoverage.SourceManual));
                }
            }

            var unchanged = rows.Count == existing.Count
                && rows.All(row => existing.ContainsKey(row.WorkId));
            if (!unchanged)
            {
                conn.Execute("DELETE FROM media_asset_coverage WHERE asset_id=@assetId", new { assetId }, tx);
                foreach (var row in rows)
                {
                    conn.Execute("""
                        INSERT INTO media_asset_coverage (asset_id, work_id, position, start_seconds, end_seconds, source)
                        VALUES (@AssetId, @WorkId, @Position, @StartSeconds, @EndSeconds, @Source);
                        """, row, tx);
                }

                conn.Execute("""
                    INSERT INTO system_activity(action_type, entity_id, entity_type, detail, changes_json)
                    VALUES('MetadataUpdated', @assetId, 'MediaAsset', @detail, @changes);
                    """, new
                {
                    assetId,
                    detail = rows.Count == 0
                        ? "File now covers a single episode"
                        : $"File now covers {rows.Count} episodes",
                    changes = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        host_work_id = view.HostWorkId,
                        covered_work_ids = ordered.Select(episode => episode.WorkId),
                        source = MediaAssetCoverage.SourceManual,
                    }),
                }, tx);
            }

            // A person has now said exactly which episodes this file holds, so any
            // "combined media" review waiting on this file is answered.
            conn.Execute("""
                UPDATE review_queue
                SET status='Resolved', resolved_at=@now, resolved_by='manual:file-coverage'
                WHERE entity_id=@assetId AND status='Pending';
                """, new { assetId, now = DateTimeOffset.UtcNow.ToString("O") }, tx);

            return Load(conn, tx, assetId);
        }, ct);
    }

    private static string Label(FileCoverageEpisode episode) =>
        episode.EpisodeNumber is { } number ? $"Episode {number}" : episode.Title;

    private static FileCoverageResult Load(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction? tx,
        Guid assetId)
    {
        var host = conn.QuerySingleOrDefault<HostRow>("""
            SELECT ma.id AS AssetId, e.id AS EditionId, w.id AS WorkId, w.parent_work_id AS SeasonId
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE ma.id = @assetId AND ma.status = 'Normal' AND ma.is_orphaned = 0
              AND w.media_type = 'TV' AND w.work_kind = 'child' AND w.parent_work_id IS NOT NULL;
            """, new { assetId }, tx);
        if (host is null)
        {
            return new FileCoverageResult(
                FileCoverageOutcome.NotFound, "This file is not an episode that can cover others.", null);
        }

        var covered = conn.Query<Guid>(
            "SELECT work_id FROM media_asset_coverage WHERE asset_id=@assetId",
            new { assetId }, tx).ToHashSet();
        var episodes = conn.Query<EpisodeRow>("""
            SELECT w.id AS WorkId, w.ordinal AS Ordinal,
                   COALESCE((SELECT value FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'title' LIMIT 1), '') AS Title,
                   EXISTS(SELECT 1 FROM work_owned_assets woa
                          JOIN media_assets other ON other.id = woa.asset_id
                          WHERE woa.work_id = w.id AND woa.asset_id <> @assetId
                            AND other.status = 'Normal' AND other.is_orphaned = 0) AS OwnedElsewhere
            FROM works w
            WHERE w.parent_work_id = @seasonId AND w.media_type = 'TV' AND w.work_kind = 'child'
            ORDER BY w.ordinal_sort, w.ordinal;
            """, new { assetId, seasonId = host.SeasonId }, tx).ToList();

        var list = episodes.Select(row => new FileCoverageEpisode(
            row.WorkId,
            row.Ordinal,
            string.IsNullOrWhiteSpace(row.Title) ? (row.Ordinal is { } n ? $"Episode {n}" : "Episode") : row.Title,
            row.WorkId == host.WorkId,
            row.WorkId == host.WorkId || covered.Contains(row.WorkId),
            row.OwnedElsewhere != 0)).ToList();

        return new FileCoverageResult(
            FileCoverageOutcome.Ok,
            null,
            new FileCoverageView(assetId, host.WorkId, host.EditionId, MaxEpisodes, list));
    }

    private sealed class TimingRow
    {
        public Guid WorkId { get; set; }
        public double? Start { get; set; }
        public double? End { get; set; }
    }

    private sealed class HostRow
    {
        public Guid AssetId { get; set; }
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public Guid SeasonId { get; set; }
    }

    private sealed class EpisodeRow
    {
        public Guid WorkId { get; set; }
        public int? Ordinal { get; set; }
        public string Title { get; set; } = "";
        public long OwnedElsewhere { get; set; }
    }
}

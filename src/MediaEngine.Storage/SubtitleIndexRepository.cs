using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite storage for the subtitle text index (Universes U-WP14): the subtitle lines of a video file, the Universe entities
/// named in them, and the file's chapter times. It reads the Library's stored <c>text_tracks</c> whichever source supplied
/// them; no provider is named anywhere in this class.
/// </summary>
public sealed class SubtitleIndexRepository : ISubtitleIndexRepository
{
    /// <summary>A failed read or probe is tried this many times in all, then left alone until its source changes.</summary>
    public const int MaxAttempts = 5;

    private readonly IDatabaseConnection _db;

    public SubtitleIndexRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlyList<SubtitleTrackSource>> GetStoredTracksNeedingIndexAsync(int limit, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<StoredTrackRow>("""
            SELECT t.id AS TextTrackId,
                   t.asset_id AS AssetId,
                   t.language AS Language,
                   t.local_path AS LocalPath,
                   COALESCE(t.updated_at, t.created_at) || '|' || t.local_path AS SourceStamp
            FROM text_tracks t
            JOIN media_assets a ON a.id = t.asset_id
            LEFT JOIN subtitle_index_tracks s ON s.text_track_id = t.id
            WHERE t.kind = 'Subtitles'
              AND a.is_orphaned = 0
              AND (s.id IS NULL
                   OR s.source_stamp <> COALESCE(t.updated_at, t.created_at) || '|' || t.local_path
                   OR (s.state = 'Failed'
                       AND s.attempt_count < @maxAttempts
                       AND (s.next_retry_at IS NULL OR s.next_retry_at <= @now)))
            ORDER BY t.created_at, t.id
            LIMIT @limit;
            """, new { limit, now = Iso(now), maxAttempts = MaxAttempts }).AsList();

        IReadOnlyList<SubtitleTrackSource> result = rows
            .Select(row => new SubtitleTrackSource(
                SubtitleTrackIds.ForTextTrack(row.TextTrackId),
                row.AssetId,
                SubtitleSourceKind.TextTrack,
                row.TextTrackId,
                null,
                row.Language,
                row.SourceStamp,
                row.LocalPath))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SubtitleTrackToTag>> GetTracksNeedingTagsAsync(int limit, DateTimeOffset taggedBefore, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<TrackToTagRow>("""
            SELECT id AS TrackId, asset_id AS AssetId, entity_signature AS EntitySignature
            FROM subtitle_index_tracks
            WHERE state = 'Indexed'
              AND (tagged_at IS NULL OR tagged_at < @taggedBefore)
            ORDER BY COALESCE(tagged_at, ''), id
            LIMIT @limit;
            """, new { limit, taggedBefore = Iso(taggedBefore) }).AsList();

        IReadOnlyList<SubtitleTrackToTag> result = rows
            .Select(row => new SubtitleTrackToTag(row.TrackId, row.AssetId, row.EntitySignature))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SubtitleAssetToProbe>> GetAssetsAwaitingProbeAsync(int limit, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<AssetRow>("""
            SELECT a.id AS AssetId, a.file_path_root AS FilePath
            FROM media_assets a
            JOIN editions e ON e.id = a.edition_id
            JOIN works w ON w.id = e.work_id
            LEFT JOIN subtitle_index_assets s ON s.asset_id = a.id
            WHERE w.media_type IN ('Movies', 'TV')
              AND a.is_orphaned = 0
              AND a.rendition_purpose = 'Original'
              AND (s.asset_id IS NULL
                   OR (s.state = 'Failed'
                       AND s.attempt_count < @maxAttempts
                       AND (s.next_retry_at IS NULL OR s.next_retry_at <= @now)))
            ORDER BY a.id
            LIMIT @limit;
            """, new { limit, now = Iso(now), maxAttempts = MaxAttempts }).AsList();

        IReadOnlyList<SubtitleAssetToProbe> result = rows
            .Select(row => new SubtitleAssetToProbe(row.AssetId, row.FilePath))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlySet<string>> GetStoredTrackLanguagesAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var languages = conn.Query<string>("""
            SELECT DISTINCT language FROM text_tracks WHERE asset_id = @assetId AND kind = 'Subtitles';
            """, new { assetId });
        IReadOnlySet<string> result = new HashSet<string>(languages, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SubtitleEntityName>> GetEntityNamesForAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        // The file's Work and the Works above it (a show above its episode), by their Wikidata items; the entities linked to
        // those Works; and every entity of the Universe those Works sit in (the shelf's Universe, or the one the linked
        // entities name). A name is only ever matched against entities of the Universe the file belongs to.
        var rows = conn.Query<EntityRow>("""
            WITH RECURSIVE chain(id, parent_id, qid, collection_id) AS (
                SELECT w.id,
                       w.parent_work_id,
                       COALESCE(NULLIF(TRIM(w.wikidata_qid), ''),
                                (SELECT NULLIF(TRIM(cv.value), '')
                                 FROM canonical_values cv
                                 WHERE cv.entity_id = w.id AND cv.key = 'wikidata_qid'
                                 LIMIT 1)),
                       w.collection_id
                FROM media_assets a
                JOIN editions e ON e.id = a.edition_id
                JOIN works w ON w.id = e.work_id
                WHERE a.id = @assetId
                UNION
                SELECT p.id,
                       p.parent_work_id,
                       COALESCE(NULLIF(TRIM(p.wikidata_qid), ''),
                                (SELECT NULLIF(TRIM(cv.value), '')
                                 FROM canonical_values cv
                                 WHERE cv.entity_id = p.id AND cv.key = 'wikidata_qid'
                                 LIMIT 1)),
                       p.collection_id
                FROM works p
                JOIN chain c ON p.id = c.parent_id
            ),
            work_qids(qid) AS (
                SELECT DISTINCT qid FROM chain WHERE qid IS NOT NULL
                UNION
                SELECT DISTINCT TRIM(e.wikidata_qid)
                FROM media_assets a
                JOIN editions e ON e.id = a.edition_id
                WHERE a.id = @assetId AND e.wikidata_qid IS NOT NULL AND TRIM(e.wikidata_qid) <> ''
            ),
            linked(entity_id) AS (
                SELECT l.entity_id FROM fictional_entity_work_links l
                WHERE l.work_qid COLLATE NOCASE IN (SELECT qid FROM work_qids)
            ),
            universes(qid) AS (
                SELECT c.universe_qid FROM chain
                JOIN collections c ON c.id = chain.collection_id
                WHERE c.universe_qid IS NOT NULL
                UNION
                SELECT fe.fictional_universe_qid FROM fictional_entities fe
                WHERE fe.id IN (SELECT entity_id FROM linked) AND fe.fictional_universe_qid IS NOT NULL
            )
            SELECT fe.id AS EntityId,
                   fe.wikidata_qid AS Qid,
                   COALESCE(overrides.label, fe.label) AS Label,
                   fe.entity_sub_type AS EntitySubType
            FROM fictional_entities fe
            LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
            WHERE fe.id IN (SELECT entity_id FROM linked)
               OR fe.fictional_universe_qid IN (SELECT qid FROM universes)
            ORDER BY fe.wikidata_qid;
            """, new { assetId }).AsList();

        IReadOnlyList<SubtitleEntityName> result = rows
            .Select(row => new SubtitleEntityName(row.EntityId, row.Qid, row.Label, row.EntitySubType, []))
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveIndexedTrackAsync(
        SubtitleTrackSource source,
        IReadOnlyList<SubtitleLine> lines,
        IReadOnlyList<SubtitleNameTag> tags,
        string entitySignature,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tags);
        return _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            conn.Execute("""
                INSERT INTO subtitle_index_tracks
                    (id, asset_id, source_kind, text_track_id, stream_index, language, state, source_stamp,
                     line_count, tag_count, entity_signature, attempt_count, last_error, next_retry_at, indexed_at, tagged_at)
                VALUES
                    (@id, @assetId, @kind, @textTrackId, @streamIndex, @language, 'Indexed', @stamp,
                     @lineCount, @tagCount, @signature, 0, NULL, NULL, @now, @now)
                ON CONFLICT(id) DO UPDATE SET
                    language = excluded.language,
                    state = 'Indexed',
                    source_stamp = excluded.source_stamp,
                    line_count = excluded.line_count,
                    tag_count = excluded.tag_count,
                    entity_signature = excluded.entity_signature,
                    attempt_count = 0,
                    last_error = NULL,
                    next_retry_at = NULL,
                    indexed_at = excluded.indexed_at,
                    tagged_at = excluded.tagged_at;
                """,
                new
                {
                    id = source.TrackId,
                    assetId = source.AssetId,
                    kind = source.Kind.ToString(),
                    textTrackId = GuidSql.ToDb(source.TextTrackId),
                    streamIndex = source.StreamIndex,
                    language = string.IsNullOrWhiteSpace(source.Language) ? "und" : source.Language,
                    stamp = source.SourceStamp,
                    lineCount = lines.Count,
                    tagCount = tags.Count,
                    signature = entitySignature,
                    now = Iso(now),
                },
                tx);

            conn.Execute("DELETE FROM subtitle_lines WHERE track_id = @id;", new { id = source.TrackId }, tx);
            conn.Execute("DELETE FROM subtitle_name_tags WHERE track_id = @id;", new { id = source.TrackId }, tx);
            InsertLines(conn, tx, source.TrackId, lines);
            InsertTags(conn, tx, source.TrackId, tags);
        }, ct);
    }

    public Task SaveTagsAsync(
        Guid trackId,
        IReadOnlyList<SubtitleNameTag> tags,
        string entitySignature,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            conn.Execute("DELETE FROM subtitle_name_tags WHERE track_id = @trackId;", new { trackId }, tx);
            InsertTags(conn, tx, trackId, tags);
            conn.Execute("""
                UPDATE subtitle_index_tracks
                SET tag_count = @tagCount, entity_signature = @signature, tagged_at = @now
                WHERE id = @trackId;
                """, new { trackId, tagCount = tags.Count, signature = entitySignature, now = Iso(now) }, tx);
        }, ct);
    }

    public Task MarkTagsCheckedAsync(Guid trackId, DateTimeOffset now, CancellationToken ct = default)
    {
        return _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            conn.Execute("UPDATE subtitle_index_tracks SET tagged_at = @now WHERE id = @trackId;", new { trackId, now = Iso(now) }, tx);
        }, ct);
    }

    public Task MarkTrackFailedAsync(SubtitleTrackSource source, string error, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            conn.Execute("""
                INSERT INTO subtitle_index_tracks
                    (id, asset_id, source_kind, text_track_id, stream_index, language, state, source_stamp,
                     attempt_count, last_error, next_retry_at, indexed_at)
                VALUES
                    (@id, @assetId, @kind, @textTrackId, @streamIndex, @language, 'Failed', @stamp,
                     1, @error, @retry, @now)
                ON CONFLICT(id) DO UPDATE SET
                    state = 'Failed',
                    attempt_count = CASE WHEN subtitle_index_tracks.source_stamp = excluded.source_stamp
                                         THEN subtitle_index_tracks.attempt_count + 1 ELSE 1 END,
                    source_stamp = excluded.source_stamp,
                    last_error = excluded.last_error,
                    next_retry_at = excluded.next_retry_at,
                    indexed_at = excluded.indexed_at;
                """,
                new
                {
                    id = source.TrackId,
                    assetId = source.AssetId,
                    kind = source.Kind.ToString(),
                    textTrackId = GuidSql.ToDb(source.TextTrackId),
                    streamIndex = source.StreamIndex,
                    language = string.IsNullOrWhiteSpace(source.Language) ? "und" : source.Language,
                    stamp = source.SourceStamp,
                    error = Truncate(error),
                    retry = Iso(nextRetryAt),
                    now = Iso(now),
                },
                tx);
        }, ct);
    }

    public Task SaveProbedAssetAsync(
        Guid assetId,
        IReadOnlyList<MediaAssetChapter> chapters,
        int embeddedStreamCount,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        return _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            conn.Execute("DELETE FROM media_asset_chapters WHERE asset_id = @assetId;", new { assetId }, tx);
            foreach (var chapter in chapters)
            {
                conn.Execute("""
                    INSERT INTO media_asset_chapters (asset_id, chapter_index, start_ms, end_ms, title)
                    VALUES (@assetId, @index, @startMs, @endMs, @title);
                    """, new { assetId, index = chapter.Index, startMs = chapter.StartMs, endMs = chapter.EndMs, title = chapter.Title }, tx);
            }

            conn.Execute("""
                INSERT INTO subtitle_index_assets
                    (asset_id, state, chapter_count, embedded_stream_count, attempt_count, last_error, next_retry_at, probed_at)
                VALUES (@assetId, 'Done', @chapterCount, @streams, 0, NULL, NULL, @now)
                ON CONFLICT(asset_id) DO UPDATE SET
                    state = 'Done',
                    chapter_count = excluded.chapter_count,
                    embedded_stream_count = excluded.embedded_stream_count,
                    attempt_count = 0,
                    last_error = NULL,
                    next_retry_at = NULL,
                    probed_at = excluded.probed_at;
                """, new { assetId, chapterCount = chapters.Count, streams = embeddedStreamCount, now = Iso(now) }, tx);
        }, ct);
    }

    public Task MarkAssetProbeFailedAsync(Guid assetId, string error, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default)
    {
        return _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            conn.Execute("""
                INSERT INTO subtitle_index_assets (asset_id, state, attempt_count, last_error, next_retry_at, probed_at)
                VALUES (@assetId, 'Failed', 1, @error, @retry, @now)
                ON CONFLICT(asset_id) DO UPDATE SET
                    state = 'Failed',
                    attempt_count = subtitle_index_assets.attempt_count + 1,
                    last_error = excluded.last_error,
                    next_retry_at = excluded.next_retry_at,
                    probed_at = excluded.probed_at;
                """, new { assetId, error = Truncate(error), retry = Iso(nextRetryAt), now = Iso(now) }, tx);
        }, ct);
    }

    public Task<IReadOnlyList<SubtitleLine>> GetLinesAsync(Guid trackId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<SubtitleLineRow>("""
            SELECT line_index AS LineIndex, start_ms AS StartMs, end_ms AS EndMs, text AS Text
            FROM subtitle_lines
            WHERE track_id = @trackId
            ORDER BY line_index;
            """, new { trackId }).AsList();
        IReadOnlyList<SubtitleLine> result = rows.Select(row => new SubtitleLine(row.LineIndex, row.StartMs, row.EndMs, row.Text)).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SubtitleNameTag>> GetTagsAsync(Guid trackId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<SubtitleTagRow>("""
            SELECT entity_id AS EntityId, window_start_ms AS WindowStartMs, window_end_ms AS WindowEndMs,
                   first_line_index AS FirstLineIndex, last_line_index AS LastLineIndex,
                   mention_count AS MentionCount, matched_text AS MatchedText
            FROM subtitle_name_tags
            WHERE track_id = @trackId
            ORDER BY window_start_ms, entity_id;
            """, new { trackId }).AsList();
        IReadOnlyList<SubtitleNameTag> result = rows
            .Select(row => new SubtitleNameTag(row.EntityId, row.WindowStartMs, row.WindowEndMs, row.FirstLineIndex, row.LastLineIndex, row.MentionCount, row.MatchedText))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<MediaAssetChapter>> GetChaptersAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<ChapterRow>("""
            SELECT chapter_index AS ChapterIndex, start_ms AS StartMs, end_ms AS EndMs, title AS Title
            FROM media_asset_chapters
            WHERE asset_id = @assetId
            ORDER BY chapter_index;
            """, new { assetId }).AsList();
        IReadOnlyList<MediaAssetChapter> result = rows.Select(row => new MediaAssetChapter(row.ChapterIndex, row.StartMs, row.EndMs, row.Title)).ToList();
        return Task.FromResult(result);
    }

    private static void InsertLines(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx, Guid trackId, IReadOnlyList<SubtitleLine> lines)
    {
        foreach (var line in lines)
        {
            conn.Execute("""
                INSERT OR REPLACE INTO subtitle_lines (track_id, line_index, start_ms, end_ms, text)
                VALUES (@trackId, @index, @startMs, @endMs, @text);
                """, new { trackId, index = line.Index, startMs = line.StartMs, endMs = line.EndMs, text = line.Text }, tx);
        }
    }

    private static void InsertTags(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx, Guid trackId, IReadOnlyList<SubtitleNameTag> tags)
    {
        foreach (var tag in tags)
        {
            conn.Execute("""
                INSERT OR REPLACE INTO subtitle_name_tags
                    (track_id, entity_id, window_start_ms, window_end_ms, first_line_index, last_line_index, mention_count, matched_text)
                VALUES (@trackId, @entityId, @start, @end, @first, @last, @mentions, @matched);
                """,
                new
                {
                    trackId,
                    entityId = tag.EntityId,
                    start = tag.WindowStartMs,
                    end = tag.WindowEndMs,
                    first = tag.FirstLineIndex,
                    last = tag.LastLineIndex,
                    mentions = tag.MentionCount,
                    matched = tag.MatchedText,
                },
                tx);
        }
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500];

    private sealed class StoredTrackRow
    {
        public Guid TextTrackId { get; set; }
        public Guid AssetId { get; set; }
        public string Language { get; set; } = "und";
        public string? LocalPath { get; set; }
        public string SourceStamp { get; set; } = string.Empty;
    }

    private sealed class TrackToTagRow
    {
        public Guid TrackId { get; set; }
        public Guid AssetId { get; set; }
        public string? EntitySignature { get; set; }
    }

    private sealed class AssetRow
    {
        public Guid AssetId { get; set; }
        public string FilePath { get; set; } = string.Empty;
    }

    private sealed class EntityRow
    {
        public Guid EntityId { get; set; }
        public string Qid { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string EntitySubType { get; set; } = string.Empty;
    }

    private sealed class SubtitleLineRow
    {
        public int LineIndex { get; set; }
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private sealed class SubtitleTagRow
    {
        public Guid EntityId { get; set; }
        public long WindowStartMs { get; set; }
        public long WindowEndMs { get; set; }
        public int FirstLineIndex { get; set; }
        public int LastLineIndex { get; set; }
        public int MentionCount { get; set; }
        public string MatchedText { get; set; } = string.Empty;
    }

    private sealed class ChapterRow
    {
        public int ChapterIndex { get; set; }
        public long StartMs { get; set; }
        public long? EndMs { get; set; }
        public string? Title { get; set; }
    }
}

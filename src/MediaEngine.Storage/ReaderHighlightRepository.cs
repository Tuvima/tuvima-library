using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IReaderHighlightRepository"/>.
/// </summary>
public sealed class ReaderHighlightRepository : IReaderHighlightRepository
{
    private readonly IDatabaseConnection _db;

    public ReaderHighlightRepository(IDatabaseConnection db) => _db = db;

    public Task<IReadOnlyList<ReaderHighlight>> ListByAssetAsync(string userId, Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<HighlightRow>("""
            SELECT id            AS Id,
                   user_id       AS UserId,
                   asset_id      AS AssetId,
                   chapter_index AS ChapterIndex,
                   start_offset  AS StartOffset,
                   end_offset    AS EndOffset,
                   cfi_range     AS CfiRange,
                   selected_text AS SelectedText,
                   color         AS Color,
                   note_text     AS NoteText,
                   created_at    AS CreatedAt
            FROM   reader_highlights
            WHERE  user_id = @userId AND asset_id = @assetId
            ORDER BY chapter_index, start_offset, created_at, id;
            """, new { userId, assetId }).AsList();

        return Task.FromResult<IReadOnlyList<ReaderHighlight>>(rows.ConvertAll(MapRow));
    }

    public Task<ReaderHighlight?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<HighlightRow>("""
            SELECT id            AS Id,
                   user_id       AS UserId,
                   asset_id      AS AssetId,
                   chapter_index AS ChapterIndex,
                   start_offset  AS StartOffset,
                   end_offset    AS EndOffset,
                   cfi_range     AS CfiRange,
                   selected_text AS SelectedText,
                   color         AS Color,
                   note_text     AS NoteText,
                   created_at    AS CreatedAt
            FROM   reader_highlights
            WHERE  id = @id
            LIMIT  1;
            """, new { id });

        return Task.FromResult(row is null ? null : MapRow(row));
    }

    public Task InsertAsync(ReaderHighlight highlight, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO reader_highlights
                (id, user_id, asset_id, chapter_index, start_offset, end_offset, cfi_range,
                 selected_text, color, note_text, created_at)
            VALUES
                (@id, @userId, @assetId, @chapterIndex, @startOffset, @endOffset, @cfiRange,
                 @selectedText, @color, @noteText, @createdAt);
            """, new
        {
            id = highlight.Id,
            userId = highlight.UserId,
            assetId = highlight.AssetId,
            chapterIndex = highlight.ChapterIndex,
            startOffset = highlight.StartOffset,
            endOffset = highlight.EndOffset,
            cfiRange = highlight.CfiRange,
            selectedText = highlight.SelectedText,
            color = highlight.Color,
            noteText = highlight.NoteText,
            createdAt = highlight.CreatedAt.ToString("O"),
        });

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Guid id, string? color, string? noteText, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE reader_highlights
            SET    color     = COALESCE(@color, color),
                   note_text = @noteText
            WHERE  id = @id;
            """, new { id, color, noteText });

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute(
            "DELETE FROM reader_highlights WHERE id = @id;",
            new { id });

        return Task.CompletedTask;
    }

    // ── Private DTO + mapper ─────────────────────────────────────────────────
    // SQLite stores GUIDs as BLOBs and timestamps as TEXT. Dapper's registered
    // handlers map GUID columns directly; the timestamp remains a string here.

    private sealed class HighlightRow
    {
        public Guid Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public Guid AssetId { get; set; }
        public int ChapterIndex { get; set; }
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }
        public string? CfiRange { get; set; }
        public string SelectedText { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public string? NoteText { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
    }

    private static ReaderHighlight MapRow(HighlightRow r) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        AssetId = r.AssetId,
        ChapterIndex = r.ChapterIndex,
        StartOffset = r.StartOffset,
        EndOffset = r.EndOffset,
        CfiRange = r.CfiRange,
        SelectedText = r.SelectedText,
        Color = r.Color,
        NoteText = r.NoteText,
        CreatedAt = DateTime.Parse(r.CreatedAt),
    };
}

using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

public sealed class AudiobookChapterTitleOverrideRepository
{
    private readonly IDatabaseConnection _db;

    public AudiobookChapterTitleOverrideRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<AudiobookChapterTitleOverrideDto>> GetByAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<OverrideRow>(
            """
            SELECT work_id AS WorkId,
                   asset_id AS AssetId,
                   chapter_index AS ChapterIndex,
                   title AS Title,
                   title_source AS TitleSource,
                   updated_at AS UpdatedAt
            FROM audiobook_chapter_title_overrides
            WHERE asset_id = @assetId
            ORDER BY chapter_index;
            """,
            new { assetId }).ToList();

        return Task.FromResult<IReadOnlyList<AudiobookChapterTitleOverrideDto>>(rows.Select(ToDto).ToList());
    }

    public Task<IReadOnlyList<AudiobookChapterTitleOverrideDto>> GetByWorkAsync(Guid workId, Guid? assetId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<OverrideRow>(
            """
            SELECT work_id AS WorkId,
                   asset_id AS AssetId,
                   chapter_index AS ChapterIndex,
                   title AS Title,
                   title_source AS TitleSource,
                   updated_at AS UpdatedAt
            FROM audiobook_chapter_title_overrides
            WHERE work_id = @workId
              AND (@assetId IS NULL OR asset_id = @assetId)
            ORDER BY asset_id, chapter_index;
            """,
            new { workId, assetId }).ToList();

        return Task.FromResult<IReadOnlyList<AudiobookChapterTitleOverrideDto>>(rows.Select(ToDto).ToList());
    }

    public Task<AudiobookChapterTitleOverrideDto> UpsertAsync(
        Guid workId,
        UpsertAudiobookChapterTitleOverrideRequestDto request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.AssetId == Guid.Empty)
        {
            throw new ArgumentException("AssetId is required.", nameof(request));
        }

        if (request.ChapterIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "ChapterIndex must be zero or greater.");
        }

        var title = BlankToNull(request.Title)
            ?? throw new ArgumentException("A track title is required.", nameof(request));
        var now = DateTimeOffset.UtcNow;
        var row = new OverrideRow
        {
            WorkId = workId,
            AssetId = request.AssetId,
            ChapterIndex = request.ChapterIndex,
            Title = title,
            TitleSource = PlaybackChapterTitleSources.Override,
            UpdatedAt = now,
        };

        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO audiobook_chapter_title_overrides
                (work_id, asset_id, chapter_index, title, title_source, updated_at)
            VALUES
                (@WorkId, @AssetId, @ChapterIndex, @Title, @TitleSource, @UpdatedAt)
            ON CONFLICT(asset_id, chapter_index) DO UPDATE SET
                work_id = excluded.work_id,
                title = excluded.title,
                title_source = excluded.title_source,
                updated_at = excluded.updated_at;
            """,
            row);

        return Task.FromResult(ToDto(row));
    }

    public Task<bool> DeleteAsync(Guid workId, Guid assetId, int chapterIndex, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var affected = conn.Execute(
            """
            DELETE FROM audiobook_chapter_title_overrides
            WHERE work_id = @workId
              AND asset_id = @assetId
              AND chapter_index = @chapterIndex;
            """,
            new { workId, assetId, chapterIndex });

        return Task.FromResult(affected > 0);
    }

    private static AudiobookChapterTitleOverrideDto ToDto(OverrideRow row) => new()
    {
        WorkId = row.WorkId,
        AssetId = row.AssetId,
        ChapterIndex = row.ChapterIndex,
        Title = row.Title,
        TitleSource = PlaybackChapterTitleSources.Override,
        UpdatedAt = row.UpdatedAt,
    };

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record OverrideRow
    {
        public Guid WorkId { get; init; }
        public Guid AssetId { get; init; }
        public int ChapterIndex { get; init; }
        public string Title { get; init; } = string.Empty;
        public string TitleSource { get; init; } = PlaybackChapterTitleSources.Override;
        public DateTimeOffset UpdatedAt { get; init; }
    }
}

using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class TextTrackRepository : ITextTrackRepository
{
    private readonly IDatabaseConnection _db;

    private const string SelectColumns = """
        id AS Id,
        asset_id AS AssetId,
        kind AS Kind,
        language AS Language,
        provider AS Provider,
        confidence AS Confidence,
        source_id AS SourceId,
        source_url AS SourceUrl,
        source_format AS SourceFormat,
        normalized_format AS NormalizedFormat,
        local_path AS LocalPath,
        sidecar_path AS SidecarPath,
        timing_mode AS TimingMode,
        duration_match_score AS DurationMatchScore,
        is_hearing_impaired AS IsHearingImpaired,
        is_preferred AS IsPreferred,
        is_user_owned AS IsUserOwned,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt
        """;

    public TextTrackRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<TextTrack>> GetByAssetAsync(Guid assetId, TextTrackKind? kind = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();

        IEnumerable<TextTrack> rows = kind.HasValue
            ? conn.Query<TextTrack>($"""
                SELECT {SelectColumns}
                FROM text_tracks
                WHERE asset_id = @assetId AND kind = @kind
                ORDER BY is_preferred DESC, confidence DESC, language, provider;
                """, new { assetId, kind = kind.Value.ToString() })
            : conn.Query<TextTrack>($"""
                SELECT {SelectColumns}
                FROM text_tracks
                WHERE asset_id = @assetId
                ORDER BY kind, is_preferred DESC, confidence DESC, language, provider;
                """, new { assetId });

        return Task.FromResult<IReadOnlyList<TextTrack>>(rows.ToList());
    }

    public Task<IReadOnlyList<TextTrack>> ListPreferredSubtitlesWithoutSidecarInFolderAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<TextTrack>($"""
            SELECT {SelectColumns}
            FROM text_tracks
            WHERE kind = 'Subtitles' AND is_preferred = 1 AND is_user_owned = 0 AND sidecar_path IS NULL
              AND asset_id NOT IN (SELECT asset_id FROM unmatched_assets)
              AND asset_id IN (
                  SELECT a.id FROM media_assets a
                  WHERE substr(lower(replace(a.file_path_root, '\', '/')), 1, length(@pathKey) + 1) = @pathKey || '/');
            """, new { pathKey = MediaEngine.Domain.Models.FolderFirstCheckPaths.Key(folderPath) });
        return Task.FromResult<IReadOnlyList<TextTrack>>(rows.ToList());
    }

    public Task<TextTrack?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QuerySingleOrDefault<TextTrack>($"""
            SELECT {SelectColumns}
            FROM text_tracks
            WHERE id = @id
            LIMIT 1;
            """, new { id });
        return Task.FromResult(row);
    }

    public Task<TextTrack?> GetPreferredAsync(Guid assetId, TextTrackKind kind, string? language = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<TextTrack>($"""
            SELECT {SelectColumns}
            FROM text_tracks
            WHERE asset_id = @assetId
              AND kind = @kind
              AND (@language IS NULL OR language = @language)
            ORDER BY is_preferred DESC, confidence DESC, is_user_owned DESC, created_at
            LIMIT 1;
            """, new
        {
            assetId,
            kind = kind.ToString(),
            language = string.IsNullOrWhiteSpace(language) ? null : language,
        });
        return Task.FromResult(row);
    }

    public Task UpsertAsync(TextTrack track, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(track);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO text_tracks
                (id, asset_id, kind, language, provider, confidence, source_id, source_url,
                 source_format, normalized_format, local_path, sidecar_path, timing_mode,
                 duration_match_score, is_hearing_impaired, is_preferred, is_user_owned, created_at)
            VALUES
                (@Id, @AssetId, @Kind, @Language, @Provider, @Confidence, @SourceId, @SourceUrl,
                 @SourceFormat, @NormalizedFormat, @LocalPath, @SidecarPath, @TimingMode,
                 @DurationMatchScore, @IsHearingImpaired, @IsPreferred, @IsUserOwned, @CreatedAt)
            ON CONFLICT(id) DO UPDATE SET
                language = excluded.language,
                provider = excluded.provider,
                confidence = excluded.confidence,
                source_id = excluded.source_id,
                source_url = excluded.source_url,
                source_format = excluded.source_format,
                normalized_format = excluded.normalized_format,
                local_path = excluded.local_path,
                sidecar_path = excluded.sidecar_path,
                timing_mode = excluded.timing_mode,
                duration_match_score = excluded.duration_match_score,
                is_hearing_impaired = excluded.is_hearing_impaired,
                is_preferred = excluded.is_preferred,
                is_user_owned = excluded.is_user_owned,
                updated_at = datetime('now');
            """, new
        {
            track.Id,
            track.AssetId,
            Kind = track.Kind.ToString(),
            track.Language,
            track.Provider,
            track.Confidence,
            track.SourceId,
            track.SourceUrl,
            track.SourceFormat,
            track.NormalizedFormat,
            track.LocalPath,
            track.SidecarPath,
            track.TimingMode,
            track.DurationMatchScore,
            IsHearingImpaired = track.IsHearingImpaired ? 1 : 0,
            IsPreferred = track.IsPreferred ? 1 : 0,
            IsUserOwned = track.IsUserOwned ? 1 : 0,
            CreatedAt = track.CreatedAt.ToString("O"),
        });

        return Task.CompletedTask;
    }

    public Task SetPreferredAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            var target = conn.QuerySingleOrDefault<(Guid AssetId, string Kind, string Language)>("""
                SELECT asset_id AS AssetId, kind AS Kind, language AS Language
                FROM text_tracks
                WHERE id = @id;
                """, new { id }, tx);

            if (target == default)
            {
                return;
            }

            conn.Execute("""
                UPDATE text_tracks
                SET is_preferred = 0, updated_at = datetime('now')
                WHERE asset_id = @AssetId AND kind = @Kind AND language = @Language;
                """, new { target.AssetId, target.Kind, target.Language }, tx);

            conn.Execute("""
                UPDATE text_tracks
                SET is_preferred = 1, updated_at = datetime('now')
                WHERE id = @id;
                """, new { id }, tx);

        }, ct);
    }
}

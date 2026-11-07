using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>One video file that may need scrub previews.</summary>
public sealed record ScrubPreviewCandidate(Guid AssetId, string FilePath, string ContentHash);

/// <summary>
/// Lists the video files that scrub previews can be made for (Unified player v2 D9a): the files a person can already browse and play
/// (identified, so first shown), newest first, so a freshly added film is reached on the next pass and the older library is worked
/// through gradually behind it.
/// </summary>
public sealed class ScrubPreviewCandidateRepository(IDatabaseConnection database)
{
    /// <summary>
    /// A page of presented, in-place video files, newest first. <paramref name="videoExtensions"/> are lower-case with the dot
    /// (".mkv"); an audio, book or photo file never comes back.
    /// </summary>
    public Task<IReadOnlyList<ScrubPreviewCandidate>> ListVideoAssetsAsync(
        IReadOnlyCollection<string> videoExtensions,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (videoExtensions.Count == 0 || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<ScrubPreviewCandidate>>([]);
        }

        var clauses = new List<string>();
        var parameters = new DynamicParameters();
        var index = 0;
        foreach (var extension in videoExtensions)
        {
            var name = $"@ext{index++}";
            clauses.Add($"LOWER(ma.file_path_root) LIKE {name}");
            parameters.Add(name, "%" + extension.ToLowerInvariant());
        }

        parameters.Add("@offset", Math.Max(0, offset));
        parameters.Add("@limit", Math.Clamp(limit, 1, 1000));
        using var connection = database.CreateConnection();
        var rows = connection.Query<ScrubPreviewRow>(
            $"""
            SELECT ma.id AS AssetId, ma.file_path_root AS FilePath, ma.content_hash AS ContentHash
            FROM media_assets ma
            WHERE ma.status = 'Normal'
              AND ma.is_orphaned = 0
              AND ma.presented_at IS NOT NULL
              AND ma.rendition_purpose = 'Original'
              AND ({string.Join(" OR ", clauses)})
            ORDER BY ma.presented_at DESC, ma.id DESC
            LIMIT @limit OFFSET @offset;
            """,
            parameters);
        return Task.FromResult<IReadOnlyList<ScrubPreviewCandidate>>(
            rows.Select(row => new ScrubPreviewCandidate(row.AssetId, row.FilePath, row.ContentHash)).ToList());
    }

    private sealed class ScrubPreviewRow
    {
        public Guid AssetId { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
    }
}

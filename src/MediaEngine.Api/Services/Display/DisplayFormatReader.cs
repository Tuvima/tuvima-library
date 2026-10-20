using System.Globalization;
using Dapper;
using MediaEngine.Contracts.Display;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// Which file formats each Work is owned in, for the "View all" format filter: EPUB, PDF and comic files for reading,
/// 4K, HD and SD for watching. One query covers the whole library and is cached briefly. The answer is raw (not scoped to
/// a viewer) and is keyed by Work and library, because a Work can be owned in several libraries and a viewer may only be
/// granted some of them: callers keep only the (Work, library) pairs in libraries the viewer may see, never a Work as a whole.
/// </summary>
public sealed class DisplayFormatReader
{
    private const string CacheKey = "display:formats:by-library";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlySet<string> NoFormats = new HashSet<string>();

    private readonly IDatabaseConnection _db;
    private readonly IMemoryCache _cache;

    public DisplayFormatReader(IDatabaseConnection db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public Task<IReadOnlyDictionary<(Guid WorkId, Guid LibraryId), IReadOnlySet<string>>> LoadAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyDictionary<(Guid WorkId, Guid LibraryId), IReadOnlySet<string>>? cached) && cached is not null)
        {
            return Task.FromResult(cached);
        }

        ct.ThrowIfCancellationRequested();
        using var connection = _db.CreateConnection();
        var rows = connection.Query<FormatRow>(new CommandDefinition(
            $"""
            -- readiness: formats are only ever attached to works the authorized display list already returned, and that list applies the Ready rule
            SELECT woa.work_id AS WorkId,
                   ma.library_id AS LibraryId,
                   ma.file_path_root AS FilePath,
                   (SELECT CAST(cv.value AS TEXT) FROM canonical_values cv
                     WHERE cv.entity_id = ma.id AND cv.key = 'video_height' LIMIT 1) AS VideoHeight
            FROM work_owned_assets woa
            JOIN media_assets ma ON ma.id = woa.asset_id
            JOIN works w ON w.id = woa.work_id
            WHERE w.work_kind <> 'parent'
              AND ma.status = 'Normal' AND ma.is_orphaned = 0
              AND {HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root")};
            """,
            cancellationToken: ct)).AsList();

        var formats = new Dictionary<(Guid WorkId, Guid LibraryId), HashSet<string>>();
        foreach (var row in rows)
        {
            var token = FormatFor(row.FilePath, row.VideoHeight);
            if (token is null || !Guid.TryParse(row.LibraryId, out var libraryId))
            {
                continue;
            }

            var key = (row.WorkId, libraryId);
            if (!formats.TryGetValue(key, out var set))
            {
                formats[key] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(token);
        }

        IReadOnlyDictionary<(Guid WorkId, Guid LibraryId), IReadOnlySet<string>> result = formats.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlySet<string>)entry.Value);
        _cache.Set(CacheKey, result, CacheDuration);
        return Task.FromResult(result);
    }

    /// <summary>
    /// The formats each of the given Works is owned in, counting only files in libraries the viewer may see. The Works
    /// themselves must already be ones the viewer is allowed to see.
    /// </summary>
    public static IReadOnlyDictionary<Guid, IReadOnlySet<string>> ForViewer(
        IReadOnlyDictionary<(Guid WorkId, Guid LibraryId), IReadOnlySet<string>> formats,
        IReadOnlySet<Guid> workIds,
        DisplayLibraryScope scope)
    {
        var found = new Dictionary<Guid, HashSet<string>>();
        foreach (var ((workId, libraryId), tokens) in formats)
        {
            if (!workIds.Contains(workId) || !scope.Allows(libraryId))
            {
                continue;
            }

            if (!found.TryGetValue(workId, out var set))
            {
                found[workId] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.UnionWith(tokens);
        }

        return found.ToDictionary(entry => entry.Key, entry => (IReadOnlySet<string>)entry.Value);
    }

    /// <summary>The format token for one owned file, or null when the file has none the filter offers (audio, unknown video size).</summary>
    internal static string? FormatFor(string? filePath, string? videoHeight)
    {
        var extension = Path.GetExtension(filePath ?? string.Empty).ToLowerInvariant();
        switch (extension)
        {
            case ".epub":
                return DisplayGridValues.FormatEpub;
            case ".pdf":
                return DisplayGridValues.FormatPdf;
            case ".cbz" or ".cbr" or ".cb7":
                return DisplayGridValues.FormatComic;
        }

        if (!int.TryParse(videoHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) || height <= 0)
        {
            return null;
        }

        return height >= 2000 ? DisplayGridValues.Format4K
            : height >= 700 ? DisplayGridValues.FormatHd
            : DisplayGridValues.FormatSd;
    }

    private sealed class FormatRow
    {
        public Guid WorkId { get; init; }
        public string? LibraryId { get; init; }
        public string? FilePath { get; init; }
        public string? VideoHeight { get; init; }
    }
}

using System.Globalization;
using System.Text;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Services;

/// <summary>
/// Finds the standalone Books Work that already holds the same book in another file format
/// (the EPUB next to the AZW3, ...), so <see cref="HierarchyResolver"/> can attach the new file
/// as another Edition rather than create a duplicate Work.
///
/// Matching is deliberately narrow: a shared calibre UUID, or the same directory plus the same
/// normalised title and the same normalised primary author. A Work that already has a file of
/// the same format is never a match, and other media types are never considered.
/// </summary>
public sealed class BookFormatSiblingFinder : IBookFormatSiblingFinder
{
    // Ebook container formats that can legitimately be two formats of one book.
    private static readonly HashSet<string> EbookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".kepub", ".azw3", ".azw", ".mobi", ".pdf", ".fb2",
    };

    private readonly IDatabaseConnection _db;

    public BookFormatSiblingFinder(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<Guid?> FindSiblingWorkAsync(
        string sourceFilePath,
        string? title,
        string? author,
        string? calibreUuid,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(sourceFilePath);
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !EbookExtensions.Contains(extension))
        {
            return Task.FromResult<Guid?>(null);
        }

        var uuid = Guid.TryParse(calibreUuid, out var parsedUuid) && parsedUuid != Guid.Empty
            ? parsedUuid.ToString("D")
            : null;
        var titleKey = IdentityKey(title);
        var authorKey = PrimaryAuthorKey(author);
        var canMatchByFolder = titleKey.Length > 0 && authorKey.Length > 0;
        if (uuid is null && !canMatchByFolder)
        {
            return Task.FromResult<Guid?>(null);
        }

        var rawDirectory = RawDirectory(sourceFilePath);
        var directory = NormalizeDirectory(rawDirectory);

        using var conn = _db.CreateConnection();

        var calibreWorkIds = new HashSet<Guid>();
        if (uuid is not null)
        {
            foreach (var id in conn.Query<Guid>(
                         """
                         SELECT DISTINCT e.work_id
                         FROM canonical_values cv
                         INNER JOIN media_assets ma ON ma.id = cv.entity_id
                         INNER JOIN editions e ON e.id = ma.edition_id
                         WHERE cv.key = 'calibre_uuid'
                           AND lower(cv.value) = @uuid;
                         """,
                         new { uuid }))
            {
                calibreWorkIds.Add(id);
            }
        }

        var likePattern = canMatchByFolder && rawDirectory.Length > 0
            ? EscapeLike(rawDirectory) + "%"
            : null;

        var assets = conn.Query<WorkAssetRow>(
            """
            SELECT w.id AS WorkId, ma.id AS AssetId, ma.file_path_root AS Path
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE w.media_type = 'Books'
              AND w.work_kind = 'standalone'
              AND w.is_catalog_only = 0
              AND ma.status <> 'Orphaned'
              AND ma.is_orphaned = 0
              AND (
                  w.id IN (
                      SELECT e2.work_id
                      FROM editions e2
                      INNER JOIN media_assets ma2 ON ma2.edition_id = e2.id
                      WHERE @likePattern IS NOT NULL
                        AND ma2.file_path_root LIKE @likePattern ESCAPE '\')
                  OR w.id IN (
                      SELECT e3.work_id
                      FROM canonical_values cv3
                      INNER JOIN media_assets ma3 ON ma3.id = cv3.entity_id
                      INNER JOIN editions e3 ON e3.id = ma3.edition_id
                      WHERE @uuid IS NOT NULL
                        AND cv3.key = 'calibre_uuid'
                        AND lower(cv3.value) = @uuid))
            ORDER BY ma.file_path_root, w.id;
            """,
            new { likePattern, uuid }).AsList();

        // Never join a Work that already holds a file of this format.
        var eligible = assets
            .GroupBy(row => row.WorkId)
            .Where(group => !group.Any(row => string.Equals(
                Path.GetExtension(row.Path), extension, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(group => group.Key, group => group.ToList());
        if (eligible.Count == 0)
        {
            return Task.FromResult<Guid?>(null);
        }

        var calibreMatch = eligible.Keys.Where(calibreWorkIds.Contains).Select(id => (Guid?)id).FirstOrDefault();
        if (calibreMatch.HasValue)
        {
            return Task.FromResult(calibreMatch);
        }

        if (!canMatchByFolder)
        {
            return Task.FromResult<Guid?>(null);
        }

        var folderAssets = eligible
            .SelectMany(pair => pair.Value
                .Where(row => string.Equals(
                    NormalizeDirectory(RawDirectory(row.Path)), directory, StringComparison.Ordinal))
                .Select(row => (WorkId: pair.Key, row.AssetId)))
            .ToList();
        if (folderAssets.Count == 0)
        {
            return Task.FromResult<Guid?>(null);
        }

        var facts = conn.Query<AssetFactRow>(
            """
            SELECT entity_id AS AssetId, claim_key AS Key, claim_value AS Value
            FROM metadata_claims
            WHERE entity_id IN @ids AND claim_key IN ('title', 'author')
            UNION ALL
            SELECT entity_id, key, value
            FROM canonical_values
            WHERE entity_id IN @ids AND key = 'title';
            """,
            new { ids = folderAssets.Select(row => GuidSql.ToBlob(row.AssetId)).Distinct().ToArray() }).AsList();

        foreach (var workGroup in folderAssets.GroupBy(row => row.WorkId))
        {
            var assetIds = workGroup.Select(row => row.AssetId).ToHashSet();
            var rows = facts.Where(fact => assetIds.Contains(fact.AssetId)).ToList();
            var titleMatches = rows.Any(fact => fact.Key == "title" && IdentityKey(fact.Value) == titleKey);
            var authorMatches = rows.Any(fact => fact.Key == "author" && PrimaryAuthorKey(fact.Value) == authorKey);
            if (titleMatches && authorMatches)
            {
                return Task.FromResult<Guid?>(workGroup.Key);
            }
        }

        return Task.FromResult<Guid?>(null);
    }

    // The directory exactly as written in the path (separators preserved) for the SQL prefix filter.
    private static string RawDirectory(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        return index > 0 ? path[..(index + 1)] : string.Empty;
    }

    private static string NormalizeDirectory(string rawDirectory) =>
        rawDirectory.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>First author of a "A; B" / "A &amp; B" list, in "First Last" order, as an identity key.</summary>
    private static string PrimaryAuthorKey(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return string.Empty;
        }

        var primary = author.Split([';', '|', '&'], 2, StringSplitOptions.TrimEntries)[0];
        return IdentityKey(HierarchyResolver.NormalizePersonNameForKey(primary));
    }

    /// <summary>
    /// Lower-cased, diacritic-free letters and digits only, so "Title: Sub" and "Title_ Sub"
    /// (the same file name as written by a file system) compare equal.
    /// </summary>
    private static string IdentityKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private sealed class WorkAssetRow
    {
        public Guid WorkId { get; set; }
        public Guid AssetId { get; set; }
        public string Path { get; set; } = string.Empty;
    }

    private sealed class AssetFactRow
    {
        public Guid AssetId { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}

using Dapper;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Extras;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// The folder button's tree (universal editor v5 3a, section 5): the title's own folder and every folder below it, each file marked as the
/// title's media, a supporting file, a file Library never shows ("Not shown in the library", such as an .nfo or a subtitle), or one that is
/// not placed. A file Library could not place is listed first, ahead of the folders. One folder at a time, paged, so a long season folder costs no more than a short one. A read only: it only looks at
/// the disk, and it never leaves the title's folder: a shortcut (a symbolic link or a junction) that points outside it is not followed, and
/// one that cannot be read is left out of the list.
/// </summary>
public sealed class EditorFolderTreeService(
    EditorHeaderReadService header,
    IItemExtraRepository extras,
    ILibraryFolderResolver libraries,
    IMediaTypeExtensionCatalog extensions,
    IEditorConfigurationSource editorConfig,
    IDatabaseConnection db,
    IExtrasConfigurationSource? rules = null,
    ILogger<EditorFolderTreeService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<EditorFolderTreeService>.Instance;

    /// <summary>
    /// One page of one folder. <paramref name="folder"/> is relative to the title's folder with <c>/</c> between parts; empty or null is the
    /// title's folder. Null when the item has no title folder, or the folder is not inside it (including any attempt to climb out of it).
    /// </summary>
    public async Task<EditorFolderTreeDto?> ReadAsync(Guid workId, string? folder, int offset, CancellationToken ct = default)
    {
        var itemFolder = await header.GetItemFolderAsync(workId, ct).ConfigureAwait(false);
        if (itemFolder is null)
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(itemFolder));
        var relative = NormalizeRelative(folder);
        if (relative is null)
        {
            return null;
        }

        var directory = relative.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(root, directory) || !Directory.Exists(directory) || !StaysInside(root, relative))
        {
            return null;
        }

        var resolved = libraries.ResolveSourceForPath(root);
        var context = resolved is null ? null : BuildContext(resolved);
        var pageSize = editorConfig.Current.FolderTreePageSize;

        // Files Library could not place first, then folders, then the other files, each group in natural name order.
        List<DirectoryInfo> folders;
        List<FileInfo> files;
        try
        {
            folders = Directory.EnumerateDirectories(directory).Select(path => new DirectoryInfo(path))
                .Where(info => StaysInside(root, info)).OrderBy(info => info.Name, NaturalOrder.Instance).ToList();
            files = Directory.EnumerateFiles(directory).Select(path => new FileInfo(path))
                .Where(info => StaysInside(root, info)).OrderBy(info => info.Name, NaturalOrder.Instance).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The folder {Folder} of item {ItemId} could not be listed", relative, workId);
            return null;
        }
        var total = folders.Count + files.Count;
        offset = Math.Clamp(offset, 0, total);

        // The state of every file of the folder is needed to put the unplaced ones first, so the records are read for all of them (one query per 400).
        var main = ReadMainFiles([.. files.Select(info => info.FullName)]);
        var supporting = files.Count == 0
            ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            : (await extras.ListAllInItemFolderAsync(itemFolder, ct).ConfigureAwait(false))
                .GroupBy(extra => Path.GetFullPath(extra.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

        var unplaced = files.Where(info => StateOf(info.FullName, main, supporting, context) == EditorFolderEntryStates.Unsorted).ToList();
        var placed = files.Except(unplaced).ToList();
        var page = unplaced.Select(info => (Entry: (FileSystemInfo)info, IsFolder: false))
            .Concat(folders.Select(info => (Entry: (FileSystemInfo)info, IsFolder: true)))
            .Concat(placed.Select(info => (Entry: (FileSystemInfo)info, IsFolder: false)))
            .Skip(offset).Take(pageSize).ToList();

        var entries = new List<EditorFolderEntryDto>(page.Count);
        foreach (var (entry, isFolder) in page)
        {
            var path = RelativeTo(root, entry.FullName);
            if (isFolder)
            {
                entries.Add(new EditorFolderEntryDto { Name = entry.Name, Path = path, IsFolder = true });
                continue;
            }

            var full = entry.FullName;
            main.TryGetValue(full, out var record);
            supporting.TryGetValue(Path.GetFullPath(full), out var extraId);
            var state = StateOf(full, main, supporting, context);
            var info = (FileInfo)entry;
            long size;
            try
            {
                size = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The file {File} of item {ItemId} could not be read and is left out of the folder list", path, workId);
                continue;
            }

            entries.Add(new EditorFolderEntryDto
            {
                Name = entry.Name,
                Path = path,
                IsFolder = false,
                State = state,
                SizeBytes = size,
                FileId = record is not null && state is EditorFolderEntryStates.Main or EditorFolderEntryStates.Unsorted ? record.AssetId : null,
                ItemId = record is not null && state == EditorFolderEntryStates.Main ? record.WorkId : null,
                SupportingFileId = extraId != Guid.Empty ? extraId : null,
            });
        }

        return new EditorFolderTreeDto
        {
            RootName = Path.GetFileName(root),
            Folder = relative,
            ParentFolder = relative.Length == 0 ? null : relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : string.Empty,
            ReadOnly = resolved is null || !resolved.Source.IsWritable,
            Total = total,
            Offset = offset,
            HasMore = offset + page.Count < total,
            Entries = entries,
        };
    }

    private ExtrasLibraryContext BuildContext(ResolvedLibrarySource resolved)
    {
        var main = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in resolved.Library.MediaTypes)
        {
            main.UnionWith(extensions.GetExtensionsFor(type));
        }

        return new ExtrasLibraryContext(resolved.Library.Id, resolved.Library.MediaTypes, resolved.Source.Path, main, rules?.Current);
    }

    // Hidden: the extras rule never lists it, or it is a kind of file Library cannot open at all. A file Library could show but has not
    // placed is "unsorted" instead.
    private bool IsHidden(ExtrasLibraryContext? context, string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (context is not null && ExtrasRule.IsHidden(context, filePath))
        {
            return true;
        }

        return !extensions.IsKnownMediaExtension(extension) && !ExtrasRule.IsExtraKindExtension(extension);
    }

    // What Library makes of one file: one of its own media files, a supporting file, one it never shows, or one it could not place. A media file
    // Needs Review still holds (Unmatched, not kept as it is) is one Library could not place.
    private string StateOf(
        string full, Dictionary<string, MediaFileRecord> main, Dictionary<string, Guid> supporting, ExtrasLibraryContext? context)
    {
        if (main.TryGetValue(full, out var record))
        {
            return record.Unplaced ? EditorFolderEntryStates.Unsorted : EditorFolderEntryStates.Main;
        }

        if (supporting.ContainsKey(Path.GetFullPath(full)))
        {
            return EditorFolderEntryStates.Supporting;
        }

        return IsHidden(context, full) ? EditorFolderEntryStates.Hidden : EditorFolderEntryStates.Unsorted;
    }

    /// <summary>The media record of a file: its asset, the item it plays as, and whether Needs Review still holds it as one Library could not place.</summary>
    private sealed record MediaFileRecord(Guid AssetId, Guid WorkId, bool Unplaced);

    private sealed class MediaFileRow
    {
        public string Path { get; set; } = string.Empty;

        public Guid AssetId { get; set; }

        public Guid WorkId { get; set; }

        public bool Unplaced { get; set; }
    }

    private const int PathsPerQuery = 400;

    // The title's media files among these paths, with the asset and item each one is.
    private Dictionary<string, MediaFileRecord> ReadMainFiles(IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, MediaFileRecord>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return result;
        }

        using var connection = db.CreateConnection();
        foreach (var chunk in paths.Chunk(PathsPerQuery))
        {
            foreach (var row in connection.Query<MediaFileRow>("""
                SELECT ma.file_path_root AS Path, ma.id AS AssetId, e.work_id AS WorkId,
                       CASE WHEN ua.asset_id IS NOT NULL AND ua.kept_as_is = 0 THEN 1 ELSE 0 END AS Unplaced
                FROM media_assets ma
                INNER JOIN editions e ON e.id = ma.edition_id
                LEFT JOIN unmatched_assets ua ON ua.asset_id = ma.id
                WHERE ma.file_path_root IN @chunk;
                """, new { chunk }))
            {
                result[Path.GetFullPath(row.Path)] = new MediaFileRecord(row.AssetId, row.WorkId, row.Unplaced);
            }
        }

        return result;
    }

    // "Season 1/Extras" form with forward slashes; null when the text could climb out of the title's folder.
    private static string? NormalizeRelative(string? folder)
    {
        var text = (folder ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part is "." or ".." || part.Contains(':') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            ? null
            : string.Join('/', parts);
    }

    // A shortcut (symbolic link, junction) is followed only when it ends inside the title's folder; one that points outside it (another
    // title, the Vault, anywhere else), or that is broken, is left out. A real file or folder always stays.
    private bool StaysInside(string root, FileSystemInfo info)
    {
        try
        {
            if (info.LinkTarget is null && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !target.Exists)
            {
                _logger.LogWarning("The shortcut {Name} in a title folder points nowhere and is left out of the folder list", info.Name);
                return false;
            }

            var resolved = Path.TrimEndingDirectorySeparator(target.FullName);
            var realRoot = Path.TrimEndingDirectorySeparator(new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? root);
            return IsInside(root, resolved) || IsInside(realRoot, resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The shortcut {Name} in a title folder could not be read and is left out of the folder list", info.Name);
            return false;
        }
    }

    // Every part of a folder path a person asks for must stay inside the title's folder too, not just the last one.
    private bool StaysInside(string root, string relative)
    {
        var current = root;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!StaysInside(root, new DirectoryInfo(current)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInside(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static string RelativeTo(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>Compares names the way a person reads them: "Episode 2" before "Episode 10".</summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= string.Empty;
            y ??= string.Empty;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var startX = i;
                    var startY = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i]))
                    {
                        i++;
                    }

                    while (j < y.Length && char.IsAsciiDigit(y[j]))
                    {
                        j++;
                    }

                    var a = x[startX..i].TrimStart('0');
                    var b = y[startY..j].TrimStart('0');
                    var byLength = a.Length.CompareTo(b.Length);
                    if (byLength != 0)
                    {
                        return byLength;
                    }

                    var byDigits = string.CompareOrdinal(a, b);
                    if (byDigits != 0)
                    {
                        return byDigits;
                    }

                    continue;
                }

                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0)
                {
                    return byChar;
                }

                i++;
                j++;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}

using Dapper;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Extras;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// The folder button's tree (universal editor v5 3a, section 5): the title's own folder and every folder below it, each file marked as the
/// title's media, a supporting file, a file Library never shows ("Not shown in the library", such as an .nfo or a subtitle), or one that is
/// not sorted yet. One folder at a time, paged, so a long season folder costs no more than a short one. A read only: it only looks at
/// the disk, and it never leaves the title's folder.
/// </summary>
public sealed class EditorFolderTreeService(
    EditorHeaderReadService header,
    IItemExtraRepository extras,
    ILibraryFolderResolver libraries,
    IMediaTypeExtensionCatalog extensions,
    IEditorConfigurationSource editorConfig,
    IDatabaseConnection db,
    IExtrasConfigurationSource? rules = null)
{
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
        if (!IsInside(root, directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var resolved = libraries.ResolveSourceForPath(root);
        var context = resolved is null ? null : BuildContext(resolved);
        var pageSize = editorConfig.Current.FolderTreePageSize;

        // Folders first, then files, each in natural name order.
        var folders = Directory.EnumerateDirectories(directory).Select(path => new DirectoryInfo(path)).OrderBy(info => info.Name, NaturalOrder.Instance).ToList();
        var files = Directory.EnumerateFiles(directory).Select(path => new FileInfo(path)).OrderBy(info => info.Name, NaturalOrder.Instance).ToList();
        var total = folders.Count + files.Count;
        offset = Math.Clamp(offset, 0, total);

        var page = folders.Select(info => (Entry: (FileSystemInfo)info, IsFolder: true))
            .Concat(files.Select(info => (Entry: (FileSystemInfo)info, IsFolder: false)))
            .Skip(offset).Take(pageSize).ToList();

        var pageFiles = page.Where(item => !item.IsFolder).Select(item => item.Entry.FullName).ToList();
        var main = ReadMainFiles(pageFiles);
        var supporting = pageFiles.Count == 0
            ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            : (await extras.ListAllInItemFolderAsync(itemFolder, ct).ConfigureAwait(false))
                .GroupBy(extra => Path.GetFullPath(extra.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

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
            main.TryGetValue(full, out var itemId);
            supporting.TryGetValue(Path.GetFullPath(full), out var extraId);
            var info = (FileInfo)entry;
            entries.Add(new EditorFolderEntryDto
            {
                Name = entry.Name,
                Path = path,
                IsFolder = false,
                State = itemId != Guid.Empty
                    ? EditorFolderEntryStates.Main
                    : extraId != Guid.Empty
                        ? EditorFolderEntryStates.Supporting
                        : IsHidden(context, full) ? EditorFolderEntryStates.Hidden : EditorFolderEntryStates.Unsorted,
                SizeBytes = info.Length,
                ItemId = itemId != Guid.Empty ? itemId : null,
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

    // The title's media files among these paths, with the item each one is.
    private Dictionary<string, Guid> ReadMainFiles(IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return result;
        }

        using var connection = db.CreateConnection();
        foreach (var row in connection.Query<(string Path, Guid WorkId)>("""
            SELECT ma.file_path_root AS Path, e.work_id AS WorkId
            FROM media_assets ma
            INNER JOIN editions e ON e.id = ma.edition_id
            WHERE ma.file_path_root IN @paths;
            """, new { paths }))
        {
            result[Path.GetFullPath(row.Path)] = row.WorkId;
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

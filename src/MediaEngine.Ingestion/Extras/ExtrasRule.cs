using System.Text.RegularExpressions;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Ingestion.Extras;

/// <summary>Where a library is and what its own files are, as the Extras rule needs them.</summary>
/// <param name="LibraryId">The logical library the file belongs to.</param>
/// <param name="Types">The media types the library holds.</param>
/// <param name="SourceRoot">The source folder the file lives under.</param>
/// <param name="MainExtensions">The file extensions this library reads as its own titles (episodes, films, tracks, books, issues).</param>
public sealed record ExtrasLibraryContext(
    string LibraryId,
    IReadOnlyList<MediaType> Types,
    string SourceRoot,
    IReadOnlySet<string> MainExtensions);

/// <summary>One file the rule decided is an Extra.</summary>
public sealed record ExtraCandidate(
    string FilePath,
    string ItemFolder,
    string Kind,
    string Title,
    string? GroupKey,
    long SizeBytes);

/// <summary>
/// The one rule that decides what is an Extra (Extras spec section 4, D1). There is no per-title code: the same rule reads every
/// show, film, album, book, comic series and Videos course. A file is an Extra when it sits inside the folder of a title, is not one
/// of that title's own files (an episode, film, track, book or issue), and is a document, a picture or a clip Library can open.
/// Folder names such as Extras or Featurettes are hints, never requirements; a clip named like a numbered lesson stays an episode.
/// </summary>
public static partial class ExtrasRule
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".epub",
    };

    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif", ".heic", ".tif", ".tiff",
    };

    private static readonly HashSet<string> VideoClipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpeg", ".mpg", ".m2ts",
    };

    private static readonly HashSet<string> AudioClipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".flac", ".ogg", ".wav", ".opus", ".aac", ".wma", ".m4b",
    };

    /// <summary>Folder names that hint "these are extras". Matched on the whole folder name, ignoring case.</summary>
    private static readonly HashSet<string> HintFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "extra", "featurettes", "behind the scenes", "bonus", "bonus features", "bonus material", "trailers",
        "samples", "sample", "deleted scenes", "interviews", "shorts", "making of",
    };

    /// <summary>Folders Library or other tools keep beside a title for their own use; never Extras.</summary>
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "@eaDir", "metadata", "extrafanart", "extrathumbs", "trickplay", "$RECYCLE.BIN", "System Volume Information", "lost+found",
    };

    /// <summary>File names that are artwork for a title or episode, which Library already uses as artwork (D12).</summary>
    [GeneratedRegex(
        @"^(?:.*[-_. ])?(?:poster|folder|cover|fanart|banner|logo|clearlogo|clearart|landscape|thumb|backdrop|background|disc|discart|cdart|front|back|albumart(?:small|large)?|show|season(?:[-_. ]?(?:\d+|specials|all))?(?:[-_. ](?:poster|banner|landscape|fanart))?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtworkNameRegex();

    /// <summary>Names of video files that say "extra" in the Plex and Jellyfin way: <c>Title-trailer.mkv</c>, <c>behindthescenes.mkv</c>.</summary>
    [GeneratedRegex(
        @"(?:^|[-_.])(?:trailer|featurette|behindthescenes|deleted|deletedscene|interview|scene|short|other|sample|makingof|bonus)\d*$|(?:^|[-_. ])(?:behind the scenes|making of|deleted scenes?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtraSuffixRegex();

    /// <summary>Words in a document name that say it belongs beside a title rather than being a book or an issue itself.</summary>
    [GeneratedRegex(
        @"\b(?:workbook|guide|guidebook|handbook|manual|booklet|bonus|extras?|companion|artbook|art book|sketchbook|worksheet|cheat ?sheet|liner notes|lyrics|script|readme|notes|making of|preview)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentHintRegex();

    /// <summary>
    /// A name that carries an episode or lesson number: <c>S01E02</c>, <c>1x02</c>, <c>Episode 4</c>, <c>Lesson 07</c>, <c>07 - Title</c>,
    /// <c>2024-03-05</c>. Such a clip is an episode, never an Extra.
    /// </summary>
    [GeneratedRegex(
        @"(?:\bS\d{1,3}\s*E\d{1,4}\b|\b\d{1,2}x\d{2,3}\b|\b(?:episode|ep|e|lesson|chapter|part|session|module|class|day|video)[-_. ]*\d{1,4}\b|^\d{1,3}\s*[-_. )]|\b\d{4}[-_.]\d{2}[-_.]\d{2}\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeMarkerRegex();

    [GeneratedRegex(@"^(?:season|series|staffel|saison|temporada)[-_. ]*\d{1,3}$|^specials?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonFolderRegex();

    public static bool IsExtraKindExtension(string extension) =>
        DocumentExtensions.Contains(extension)
        || PictureExtensions.Contains(extension)
        || VideoClipExtensions.Contains(extension)
        || AudioClipExtensions.Contains(extension);

    public static string KindOf(string extension) =>
        DocumentExtensions.Contains(extension) ? ItemExtraKinds.Document
        : PictureExtensions.Contains(extension) ? ItemExtraKinds.Picture
        : ItemExtraKinds.Clip;

    /// <summary>True for the folders whose items are shows or courses: the title is the top folder, however deep its seasons go.</summary>
    private static bool IsShowLike(ExtrasLibraryContext context) =>
        context.Types.Contains(MediaType.TV) || context.Types.Contains(MediaType.Videos);

    /// <summary>
    /// The folder of the title this file sits in, or null when the file is not inside a title's folder (a loose file in the library
    /// folder, or a folder that holds none of the library's own files).
    /// </summary>
    public static string? ResolveItemFolder(ExtrasLibraryContext context, string filePath)
    {
        var root = TrimSeparators(context.SourceRoot);
        var relative = RelativeParts(root, filePath);
        if (relative is null || relative.Length < 2 || relative.Take(relative.Length - 1).Any(IsIgnoredFolder))
        {
            return null;
        }

        if (IsShowLike(context))
        {
            return Path.Combine(root, relative[0]);
        }

        // A film, album, book or comic series: the nearest folder above the file that directly holds one of the library's own files.
        // An Extras-style folder never counts as the title's folder, so a clip in "Film/Extras" belongs to "Film".
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (!string.IsNullOrEmpty(directory) && IsBelow(root, directory))
        {
            var name = Path.GetFileName(directory);
            if (!HintFolders.Contains(name) && HoldsMainFile(context, directory))
            {
                return HoldsOneTitle(context, directory) ? directory : null;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>
    /// Decides whether one file is an Extra of its title. Returns null for a file that is the title's own, artwork, ignored, or not
    /// inside a title's folder.
    /// </summary>
    public static ExtraCandidate? Classify(ExtrasLibraryContext context, string filePath)
    {
        var extension = Path.GetExtension(filePath);
        if (!IsExtraKindExtension(extension) || IsTransient(filePath))
        {
            return null;
        }

        var itemFolder = ResolveItemFolder(context, filePath);
        if (itemFolder is null)
        {
            return null;
        }

        var kind = KindOf(extension);
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var inHintFolder = HasHintFolder(itemFolder, filePath);

        if (kind == ItemExtraKinds.Picture)
        {
            if (!inHintFolder && IsArtworkName(stem))
            {
                return null;
            }

            if (IsSidecarOfMainFile(context, filePath))
            {
                return null;
            }
        }
        else if (IsOwnFormat(context, extension) && !inHintFolder)
        {
            // The title's own kind of file. It is an Extra only when its name or place says so.
            if (!IsExtraByNameOrPlace(context, itemFolder, filePath, kind))
            {
                return null;
            }
        }

        string? groupKey = null;
        if (kind == ItemExtraKinds.Picture)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? itemFolder;
            groupKey = string.Equals(TrimSeparators(folder), TrimSeparators(itemFolder), StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : Path.GetRelativePath(itemFolder, folder).Replace('\\', '/');
        }

        long size = 0;
        try
        {
            size = new FileInfo(filePath).Length;
        }
        catch (IOException)
        {
            // A file that cannot be measured is still listed; its size is simply not shown.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: the card does not need the size.
        }

        return new ExtraCandidate(Path.GetFullPath(filePath), itemFolder, kind, TitleOf(stem), groupKey, size);
    }

    /// <summary>
    /// True when the folder still holds one of the title's own files (an episode, film, track, book or issue) outside an Extras-style
    /// folder. A folder with none is not a title any more, so it has no Extras: when a show is deleted its Extras go with it.
    /// </summary>
    public static bool HoldsTitle(ExtrasLibraryContext context, string itemFolder) =>
        EnumerateFiles(itemFolder).Any(file =>
            !IsTransient(file)
            && IsOwnFormat(context, Path.GetExtension(file))
            && !HasHintFolder(itemFolder, file)
            && Classify(context, file) is null);

    /// <summary>Every Extra in one title's folder, in natural order. An unreadable folder gives an empty list.</summary>
    public static IReadOnlyList<ExtraCandidate> ScanItemFolder(ExtrasLibraryContext context, string itemFolder)
    {
        var found = new List<ExtraCandidate>();
        foreach (var path in EnumerateFiles(itemFolder))
        {
            if (Classify(context, path) is { } candidate
                && string.Equals(TrimSeparators(candidate.ItemFolder), TrimSeparators(itemFolder), StringComparison.OrdinalIgnoreCase))
            {
                found.Add(candidate);
            }
        }

        found.Sort((left, right) => NaturalComparer.Instance.Compare(left.FilePath, right.FilePath));
        return found;
    }

    /// <summary>The title folders under a source folder that may hold Extras: its top folders for shows and courses, else every folder holding a main file.</summary>
    public static IEnumerable<string> EnumerateItemFolders(ExtrasLibraryContext context)
    {
        var root = TrimSeparators(context.SourceRoot);
        if (!Directory.Exists(root))
        {
            yield break;
        }

        if (IsShowLike(context))
        {
            foreach (var folder in SafeDirectories(root).Where(folder => !IsIgnoredFolder(Path.GetFileName(folder))))
            {
                yield return folder;
            }

            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in SafeDirectories(current))
            {
                var name = Path.GetFileName(child);
                if (IsIgnoredFolder(name))
                {
                    continue;
                }

                stack.Push(child);
                if (!HintFolders.Contains(name) && HoldsMainFile(context, child))
                {
                    yield return child;
                }
            }
        }
    }

    public static bool IsHintFolder(string name) => HintFolders.Contains(name);

    /// <summary>
    /// Whether the library reads this kind of file as a title of its own. A PDF counts for books and comics even though the
    /// extension list does not name it, because Library reads a PDF in those libraries as a book or an issue.
    /// </summary>
    private static bool IsOwnFormat(ExtrasLibraryContext context, string extension) =>
        context.MainExtensions.Contains(extension)
        || (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            && (context.Types.Contains(MediaType.Books) || context.Types.Contains(MediaType.Comics)));

    private static bool IsExtraByNameOrPlace(ExtrasLibraryContext context, string itemFolder, string filePath, string kind)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (kind == ItemExtraKinds.Document)
        {
            // A PDF is a book or a comic issue itself in those libraries; only a name that says "guide", "bonus" and so on makes it an Extra.
            return DocumentHintRegex().IsMatch(stem);
        }

        if (kind != ItemExtraKinds.Clip)
        {
            return false;
        }

        var isVideo = VideoClipExtensions.Contains(Path.GetExtension(filePath));
        if (!isVideo)
        {
            // A song or an audiobook part is the title's own file; only an Extras-style folder (handled by the caller) changes that.
            return false;
        }

        var hasFilmsOrShows = context.Types.Contains(MediaType.Movies) || context.Types.Contains(MediaType.TV);
        if (!hasFilmsOrShows)
        {
            // A Videos course: every video in the folder is a lesson, never an Extra (outside an Extras-style folder).
            return false;
        }

        if (ExtraSuffixRegex().IsMatch(stem))
        {
            return true;
        }

        if (!context.Types.Contains(MediaType.TV))
        {
            // A film folder holding two films, or parts of one, keeps both as the title's own files.
            return false;
        }

        // A show: a clip with an episode or lesson number is an episode.
        if (EpisodeMarkerRegex().IsMatch(stem))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? itemFolder;
        var siblingVideos = SafeFiles(directory)
            .Where(sibling => !string.Equals(sibling, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)
                && VideoClipExtensions.Contains(Path.GetExtension(sibling)))
            .ToList();
        if (siblingVideos.Any(sibling => EpisodeMarkerRegex().IsMatch(Path.GetFileNameWithoutExtension(sibling))))
        {
            return true;
        }

        // Beside the seasons ("Show/Making of.mkv" with "Show/Season 1"), or the only unnumbered video among seasons.
        return SafeDirectories(directory).Any(child => SeasonFolderRegex().IsMatch(Path.GetFileName(child)));
    }

    private static bool HoldsMainFile(ExtrasLibraryContext context, string directory) =>
        SafeFiles(directory).Any(file => context.MainExtensions.Contains(Path.GetExtension(file)) && !IsTransient(file));

    /// <summary>
    /// A film or book folder that holds many files is a pile of titles, so an extra file in it cannot be told apart as belonging to one
    /// of them. A series, album or audiobook folder legitimately holds many files, so it is not limited.
    /// </summary>
    private static bool HoldsOneTitle(ExtrasLibraryContext context, string directory)
    {
        var manyFilesAreOneTitle = context.Types.Any(type => type is MediaType.Comics or MediaType.Music or MediaType.Audiobooks);
        if (manyFilesAreOneTitle)
        {
            return true;
        }

        return SafeFiles(directory).Count(file => context.MainExtensions.Contains(Path.GetExtension(file)) && !IsTransient(file)) <= 3;
    }

    private static bool IsSidecarOfMainFile(ExtrasLibraryContext context, string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (directory is null)
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(filePath);
        return SafeFiles(directory).Any(sibling =>
        {
            if (!context.MainExtensions.Contains(Path.GetExtension(sibling)))
            {
                return false;
            }

            var siblingStem = Path.GetFileNameWithoutExtension(sibling);
            return stem.Equals(siblingStem, StringComparison.OrdinalIgnoreCase)
                || (stem.Length > siblingStem.Length
                    && stem.StartsWith(siblingStem, StringComparison.OrdinalIgnoreCase)
                    && "-_. ".Contains(stem[siblingStem.Length]));
        });
    }

    private static bool IsArtworkName(string stem) => ArtworkNameRegex().IsMatch(stem);

    private static bool HasHintFolder(string itemFolder, string filePath)
    {
        var relative = RelativeParts(TrimSeparators(itemFolder), filePath);
        return relative is { Length: > 1 } && relative.Take(relative.Length - 1).Any(HintFolders.Contains);
    }

    private static bool IsIgnoredFolder(string name) =>
        name.StartsWith('.') || IgnoredFolders.Contains(name);

    private static bool IsTransient(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.')
            || name.StartsWith("~$", StringComparison.Ordinal)
            || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The card title for a file name without its extension.</summary>
    internal static string TitleFor(string stem) => TitleOf(stem);

    private static string TitleOf(string stem)
    {
        var cleaned = stem.Replace('_', ' ').Trim();
        return cleaned.Length == 0 ? stem : cleaned;
    }

    private static string TrimSeparators(string path) =>
        path.Length > 1 ? Path.TrimEndingDirectorySeparator(path) : path;

    private static bool IsBelow(string root, string directory) =>
        directory.Length > root.Length
        && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (directory[root.Length] == Path.DirectorySeparatorChar || directory[root.Length] == Path.AltDirectorySeparatorChar);

    private static string[]? RelativeParts(string root, string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var normalisedRoot = Path.GetFullPath(root);
        if (!IsBelow(TrimSeparators(normalisedRoot), full))
        {
            return null;
        }

        return Path.GetRelativePath(normalisedRoot, full)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<string> EnumerateFiles(string folder)
    {
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var file in SafeFiles(current))
            {
                yield return file;
            }

            foreach (var child in SafeDirectories(current))
            {
                if (!IsIgnoredFolder(Path.GetFileName(child)))
                {
                    stack.Push(child);
                }
            }
        }
    }

    private static string[] SafeFiles(string directory)
    {
        try
        {
            return Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Compares names the way people count: "2" before "10".</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static NaturalComparer Instance { get; } = new();

        public int Compare(string? x, string? y) => CompareNatural(x ?? string.Empty, y ?? string.Empty);

        private static int CompareNatural(string x, string y)
        {
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) { i++; }
                    while (j < y.Length && char.IsDigit(y[j])) { j++; }
                    var left = x[si..i].TrimStart('0');
                    var right = y[sj..j].TrimStart('0');
                    var byLength = left.Length.CompareTo(right.Length);
                    if (byLength != 0) { return byLength; }
                    var byDigits = string.CompareOrdinal(left, right);
                    if (byDigits != 0) { return byDigits; }
                }
                else
                {
                    var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (byChar != 0) { return byChar; }
                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}

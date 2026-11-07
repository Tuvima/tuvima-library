using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Ingestion.Extras;

/// <summary>Where a library is and what its own files are, as the Extras rule needs them.</summary>
/// <param name="LibraryId">The logical library the file belongs to.</param>
/// <param name="Types">The media types the library holds.</param>
/// <param name="SourceRoot">The source folder the file lives under.</param>
/// <param name="MainExtensions">The file extensions this library reads as its own titles (episodes, films, tracks, books, issues).</param>
/// <param name="Rules">The supporting-files rules (<c>config/extras.json</c>); null means the rules in force (<see cref="ExtrasConfigurationProvider.Active"/>).</param>
public sealed record ExtrasLibraryContext(
    string LibraryId,
    IReadOnlyList<MediaType> Types,
    string SourceRoot,
    IReadOnlySet<string> MainExtensions,
    ExtrasConfiguration? Rules = null);

/// <summary>One file the rule decided is an Extra, with what the five checks of the placement rule read for it.</summary>
/// <param name="Role">What the file is (an <see cref="ExtraRoles"/> id).</param>
/// <param name="Group">The group a named sub-folder gives, or null.</param>
/// <param name="Scope">What the file belongs to (the title, a season or section, an episode or lesson).</param>
/// <param name="Alongside">The episode or lesson the file is shown with, or null.</param>
public sealed record ExtraCandidate(
    string FilePath,
    string ItemFolder,
    string Kind,
    string Title,
    string? GroupKey,
    long SizeBytes,
    string Role = "",
    string? Group = null,
    ExtraScope Scope = default,
    ExtraScope? Alongside = null);

/// <summary>
/// The one rule that decides what is an Extra (Extras spec section 4, D1; supporting files, editor v5 D16 and 3b). There is no per-title
/// code: the same rule reads every show, film, album, book, comic series and Videos course. A file is an Extra when it sits inside the
/// folder of a title, is not one of that title's own files (an episode, film, track, book or issue), is a document, a picture or a clip
/// Library can open, and is not hidden (an archive, a metadata sidecar, a subtitle file, artwork named for a media file).
/// Folder names such as Extras or Featurettes are hints, never requirements; a clip named like a numbered lesson stays an episode.
/// Every word list lives in <c>config/extras.json</c>.
/// </summary>
public static class ExtrasRule
{
    public static bool IsExtraKindExtension(string extension) => ExtrasRuleBook.For(ExtrasConfigurationProvider.Active).IsAccepted(extension);

    public static string KindOf(string extension) => ExtrasRuleBook.For(ExtrasConfigurationProvider.Active).KindOf(extension);

    /// <summary>True for a file type that is never shown as a supporting file: an archive, a metadata sidecar, a subtitle file.</summary>
    public static bool IsHiddenType(string extension) =>
        ExtrasRuleBook.For(ExtrasConfigurationProvider.Active).HiddenExtensions.Contains(extension);

    /// <summary>
    /// True for a file the rule never lists: transient, an archive, a metadata sidecar, a subtitle file, or artwork named for a media file
    /// (poster, fanart, thumb). The browser's Folders and files view lists these greyed as "Not shown in the library".
    /// </summary>
    public static bool IsHidden(ExtrasLibraryContext context, string filePath)
    {
        ArgumentNullException.ThrowIfNull(context);
        var book = BookOf(context);
        var extension = Path.GetExtension(filePath);
        if (IsTransient(book, filePath) || book.HiddenExtensions.Contains(extension))
        {
            return true;
        }

        if (!book.Pictures.Contains(extension))
        {
            return false;
        }

        var itemFolder = ResolveItemFolder(context, filePath);
        var inHintFolder = itemFolder is not null && HasHintFolder(book, itemFolder, filePath);
        return (!inHintFolder && IsArtworkName(book, Path.GetFileNameWithoutExtension(filePath))) || IsSidecarOfMainFile(context, filePath);
    }

    /// <summary>True for the folders whose items are shows or courses: the title is the top folder, however deep its seasons go.</summary>
    private static bool IsShowLike(ExtrasLibraryContext context) =>
        context.Types.Contains(MediaType.TV) || context.Types.Contains(MediaType.Videos);

    /// <summary>
    /// The folder of the title this file sits in, or null when the file is not inside a title's folder (a loose file in the library
    /// folder, or a folder that holds none of the library's own files).
    /// </summary>
    public static string? ResolveItemFolder(ExtrasLibraryContext context, string filePath)
    {
        var book = BookOf(context);
        var root = TrimSeparators(context.SourceRoot);
        var relative = RelativeParts(root, filePath);
        if (relative is null || relative.Length < 2 || relative.Take(relative.Length - 1).Any(name => IsIgnoredFolder(book, name)))
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
            if (!book.HintFolders.Contains(name) && HoldsMainFile(book, context, directory))
            {
                return HoldsOneTitle(book, context, directory) ? directory : null;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>
    /// Decides whether one file is an Extra of its title and reads where it belongs, what it is and how it is grouped (section 3b).
    /// Returns null for a file that is the title's own, hidden, ignored, or not inside a title's folder.
    /// </summary>
    public static ExtraCandidate? Classify(ExtrasLibraryContext context, string filePath) => Classify(context, filePath, null);

    private static ExtraCandidate? Classify(ExtrasLibraryContext context, string filePath, MainFileIndex? index)
    {
        var book = BookOf(context);
        var extension = Path.GetExtension(filePath);
        if (!book.IsAccepted(extension) || book.HiddenExtensions.Contains(extension) || IsTransient(book, filePath))
        {
            return null;
        }

        var itemFolder = ResolveItemFolder(context, filePath);
        if (itemFolder is null)
        {
            return null;
        }

        var kind = book.KindOf(extension);
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var inHintFolder = HasHintFolder(book, itemFolder, filePath);

        if (kind == ItemExtraKinds.Picture)
        {
            if (!inHintFolder && IsArtworkName(book, stem))
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
            if (!IsExtraByNameOrPlace(book, context, itemFolder, filePath, kind))
            {
                return null;
            }
        }

        string? groupKey = null;
        if (kind == ItemExtraKinds.Picture)
        {
            // A picture folder stays one item (D17): its pictures share the folder as their key.
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

        var placed = ExtraPlacementInference.Infer(
            context, book, index ?? ExtraPlacementInference.BuildIndex(context, book, itemFolder), itemFolder, filePath, kind);
        return new ExtraCandidate(
            Path.GetFullPath(filePath), itemFolder, kind, placed.Title, groupKey, size, placed.Role, placed.Group, placed.Scope, placed.Alongside);
    }

    /// <summary>
    /// True when the folder still holds one of the title's own files (an episode, film, track, book or issue) outside an Extras-style
    /// folder. A folder with none is not a title any more, so it has no Extras: when a show is deleted its Extras go with it.
    /// </summary>
    public static bool HoldsTitle(ExtrasLibraryContext context, string itemFolder)
    {
        var book = BookOf(context);
        return EnumerateFiles(book, itemFolder).Any(file =>
            !IsTransient(book, file)
            && IsOwnFormat(context, Path.GetExtension(file))
            && !HasHintFolder(book, itemFolder, file)
            && Classify(context, file) is null);
    }

    /// <summary>
    /// The folder a supporting file sits in when it belongs to <paramref name="scope"/> (universal editor v5 D30): the title's own folder,
    /// the season's folder, the folder of the episode's or lesson's own file (its own folder when it has one), or the section's folder.
    /// Null when the title has no such place on disk (a season with no folder of its own, an album track, an episode that is not there).
    /// </summary>
    public static string? ResolveScopeFolder(ExtrasLibraryContext context, string itemFolder, ExtraScope scope)
    {
        var book = BookOf(context);
        var root = TrimSeparators(Path.GetFullPath(itemFolder));
        if (!Directory.Exists(root))
        {
            return null;
        }

        switch (scope.Level)
        {
            case ExtraScopeLevel.Root:
                return root;

            case ExtraScopeLevel.Season:
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(directory);
                    var match = book.SeasonFolder.Match(name);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var number = match.Groups["specials"].Success
                        ? 0
                        : int.Parse(match.Groups["season"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (number == scope.Season)
                    {
                        return TrimSeparators(directory);
                    }
                }

                return null;

            case ExtraScopeLevel.Folder:
                var section = TrimSeparators(Path.GetFullPath(Path.Combine(root, scope.Path ?? string.Empty)));
                return IsBelow(root, section) && Directory.Exists(section) ? section : null;

            case ExtraScopeLevel.Episode:
            case ExtraScopeLevel.File:
                var entry = ExtraPlacementInference.BuildIndex(context, book, root).Entries.FirstOrDefault(item => item.Scope == scope);
                return entry is null ? null : TrimSeparators(entry.Directory);

            default:
                return null;
        }
    }

    /// <summary>
    /// The places a supporting file of this title can belong to (universal editor v5 D30): the title itself, every season (a season folder,
    /// or a season one of the title's own files names), and every episode, lesson or section one of the title's own files sits in. Empty
    /// when the title folder is not there.
    /// </summary>
    public static IReadOnlyList<ExtraScope> ListPlaces(ExtrasLibraryContext context, string itemFolder)
    {
        var book = BookOf(context);
        var root = TrimSeparators(Path.GetFullPath(itemFolder));
        if (!Directory.Exists(root))
        {
            return [];
        }

        var places = new HashSet<ExtraScope> { ExtraScope.Root };
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var match = book.SeasonFolder.Match(Path.GetFileName(directory));
            if (match.Success)
            {
                places.Add(ExtraScope.ForSeason(match.Groups["specials"].Success
                    ? 0
                    : int.Parse(match.Groups["season"].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        foreach (var entry in ExtraPlacementInference.BuildIndex(context, book, root).Entries)
        {
            if (entry.Scope.Level != ExtraScopeLevel.Root && entry.Scope.Level != ExtraScopeLevel.None)
            {
                places.Add(entry.Scope);
            }

            if (entry.Scope is { Level: ExtraScopeLevel.Episode, Season: { } season })
            {
                places.Add(ExtraScope.ForSeason(season));
            }
        }

        return [.. places];
    }

    /// <summary>Every Extra in one title's folder, in natural order. An unreadable folder gives an empty list.</summary>
    public static IReadOnlyList<ExtraCandidate> ScanItemFolder(ExtrasLibraryContext context, string itemFolder)
    {
        var book = BookOf(context);
        var found = new List<ExtraCandidate>();
        MainFileIndex? index = null;
        foreach (var path in EnumerateFiles(book, itemFolder))
        {
            // The list of the title's own files is only needed once there is a file to place against it.
            if (!book.IsAccepted(Path.GetExtension(path)))
            {
                continue;
            }

            index ??= ExtraPlacementInference.BuildIndex(context, book, itemFolder);
            if (Classify(context, path, index) is { } candidate
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
        var book = BookOf(context);
        var root = TrimSeparators(context.SourceRoot);
        if (!Directory.Exists(root))
        {
            yield break;
        }

        if (IsShowLike(context))
        {
            foreach (var folder in SafeDirectories(root).Where(folder => !IsIgnoredFolder(book, Path.GetFileName(folder))))
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
                if (IsIgnoredFolder(book, name))
                {
                    continue;
                }

                stack.Push(child);
                if (!book.HintFolders.Contains(name) && HoldsMainFile(book, context, child))
                {
                    yield return child;
                }
            }
        }
    }

    public static bool IsHintFolder(string name) => ExtrasRuleBook.For(ExtrasConfigurationProvider.Active).HintFolders.Contains(name);

    /// <summary>
    /// Whether the library reads this kind of file as a title of its own. A PDF counts for books and comics even though the
    /// extension list does not name it, because Library reads a PDF in those libraries as a book or an issue.
    /// </summary>
    private static bool IsOwnFormat(ExtrasLibraryContext context, string extension) =>
        context.MainExtensions.Contains(extension)
        || (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            && (context.Types.Contains(MediaType.Books) || context.Types.Contains(MediaType.Comics)));

    private static bool IsExtraByNameOrPlace(ExtrasRuleBook book, ExtrasLibraryContext context, string itemFolder, string filePath, string kind)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (kind == ItemExtraKinds.Document)
        {
            // A PDF is a book or a comic issue itself in those libraries; only a name that says "guide", "bonus" and so on makes it an Extra.
            return book.DocumentHint.IsMatch(stem);
        }

        if (kind != ItemExtraKinds.Clip)
        {
            return false;
        }

        var isVideo = book.VideoClips.Contains(Path.GetExtension(filePath));
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

        if (book.ClipSuffix.Match(stem) is { Success: true } suffix)
        {
            // "Title-trailer.mkv" (Plex and Jellyfin style) is always an Extra. A word after a space ("Season 1 trailer.mkv") is too,
            // unless the name carries an episode or lesson number: "S01E02 The Interview" is an episode that happens to end in a word.
            var afterSpace = suffix.Groups["sep"].Value.Length > 0 && char.IsWhiteSpace(suffix.Groups["sep"].Value[0]);
            if (!afterSpace || !book.EpisodeMarker.IsMatch(stem))
            {
                return true;
            }
        }

        if (!context.Types.Contains(MediaType.TV))
        {
            // A film folder holding two films, or parts of one, keeps both as the title's own files.
            return false;
        }

        // A show: a clip with an episode or lesson number is an episode.
        if (book.EpisodeMarker.IsMatch(stem))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? itemFolder;
        var siblingVideos = SafeFiles(directory)
            .Where(sibling => !string.Equals(sibling, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)
                && book.VideoClips.Contains(Path.GetExtension(sibling)))
            .ToList();
        if (siblingVideos.Any(sibling => book.EpisodeMarker.IsMatch(Path.GetFileNameWithoutExtension(sibling))))
        {
            return true;
        }

        // Beside the seasons ("Show/Making of.mkv" with "Show/Season 1"), or the only unnumbered video among seasons.
        return SafeDirectories(directory).Any(child => book.SeasonFolder.IsMatch(Path.GetFileName(child)));
    }

    private static bool HoldsMainFile(ExtrasRuleBook book, ExtrasLibraryContext context, string directory) =>
        SafeFiles(directory).Any(file => context.MainExtensions.Contains(Path.GetExtension(file)) && !IsTransient(book, file));

    /// <summary>
    /// A film or book folder that holds many files is a pile of titles, so an extra file in it cannot be told apart as belonging to one
    /// of them. A series, album or audiobook folder legitimately holds many files, so it is not limited.
    /// </summary>
    private static bool HoldsOneTitle(ExtrasRuleBook book, ExtrasLibraryContext context, string directory)
    {
        var manyFilesAreOneTitle = context.Types.Any(type => type is MediaType.Comics or MediaType.Music or MediaType.Audiobooks);
        if (manyFilesAreOneTitle)
        {
            return true;
        }

        return SafeFiles(directory).Count(file => context.MainExtensions.Contains(Path.GetExtension(file)) && !IsTransient(book, file)) <= 3;
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

    private static bool IsArtworkName(ExtrasRuleBook book, string stem) => book.PictureSidecarName?.IsMatch(stem) == true;

    private static bool HasHintFolder(ExtrasRuleBook book, string itemFolder, string filePath)
    {
        var relative = RelativeParts(TrimSeparators(itemFolder), filePath);
        return relative is { Length: > 1 } && relative.Take(relative.Length - 1).Any(book.HintFolders.Contains);
    }

    private static bool IsIgnoredFolder(ExtrasRuleBook book, string name) =>
        name.StartsWith('.') || book.IgnoredFolders.Contains(name);

    internal static bool IsTransient(ExtrasRuleBook book, string path)
    {
        var name = Path.GetFileName(path);
        var transient = book.Config.TransientFiles;
        return transient.NamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
            || transient.NameSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            || book.TransientNames.Contains(name);
    }

    private static ExtrasRuleBook BookOf(ExtrasLibraryContext context) =>
        ExtrasRuleBook.For(context.Rules ?? ExtrasConfigurationProvider.Active);

    /// <summary>The card title for a file name without its extension.</summary>
    internal static string TitleFor(string stem) => TitleOf(ExtrasRuleBook.For(ExtrasConfigurationProvider.Active), stem);

    /// <summary>
    /// Step 5: the file name without its extension, separators turned into spaces, runs of spaces collapsed and the ends trimmed.
    /// Leading numbers stay ("02-component-exercises" is "02 component exercises"); noise words are removed by the name-cleaning rules, not here.
    /// </summary>
    internal static string TitleOf(ExtrasRuleBook book, string stem)
    {
        var spaced = book.TitleSeparators.Aggregate(stem, (text, separator) => text.Replace(separator, ' '));
        var cleaned = string.Join(' ', spaced.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length == 0 ? stem : cleaned;
    }

    internal static string TrimSeparators(string path) =>
        path.Length > 1 ? Path.TrimEndingDirectorySeparator(path) : path;

    internal static bool IsBelow(string root, string directory) =>
        directory.Length > root.Length
        && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (directory[root.Length] == Path.DirectorySeparatorChar || directory[root.Length] == Path.AltDirectorySeparatorChar);

    internal static string[]? RelativeParts(string root, string filePath)
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

    internal static IEnumerable<string> EnumerateFiles(ExtrasRuleBook book, string folder)
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
                if (!IsIgnoredFolder(book, Path.GetFileName(child)))
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

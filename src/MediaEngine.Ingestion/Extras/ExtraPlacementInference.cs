using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Ingestion.Extras;

/// <summary>What the five checks of the placement rule read for one supporting file (editor v5 spec section 3b).</summary>
/// <param name="Role">Step 3: what the file is (an <see cref="ExtraRoles"/> id).</param>
/// <param name="Group">Step 4: the group a named sub-folder gives, or null.</param>
/// <param name="Scope">Step 1: what the file belongs to.</param>
/// <param name="Alongside">Step 2: the episode or lesson it is shown with, or null.</param>
/// <param name="Title">Step 5: the cleaned file name.</param>
internal sealed record ExtraInference(string Role, string? Group, ExtraScope Scope, ExtraScope? Alongside, string Title);

/// <summary>One of the title's own files (an episode, lesson or track) that a supporting file can belong to or sit beside.</summary>
internal sealed record MainFileEntry(string FullPath, string Relative, string Directory, string Stem, ExtraScope Scope);

/// <summary>The title's own files, listed once so every supporting file in a pass is placed against the same list.</summary>
internal sealed class MainFileIndex
{
    public MainFileIndex(IReadOnlyList<MainFileEntry> entries) => Entries = entries;

    public IReadOnlyList<MainFileEntry> Entries { get; }

    public IEnumerable<MainFileEntry> InDirectory(string directory) =>
        Entries.Where(entry => string.Equals(ExtrasRule.TrimSeparators(entry.Directory), ExtrasRule.TrimSeparators(directory), StringComparison.OrdinalIgnoreCase));

    public IEnumerable<MainFileEntry> Under(string directory)
    {
        var root = ExtrasRule.TrimSeparators(directory);
        return Entries.Where(entry => string.Equals(ExtrasRule.TrimSeparators(entry.Directory), root, StringComparison.OrdinalIgnoreCase)
            || ExtrasRule.IsBelow(root, entry.Directory));
    }
}

/// <summary>
/// The five checks of the placement rule (editor v5 section 3b), in order, first match wins, every result a proposal a person can
/// override: owner (Belongs to), shown alongside, role, group, title. There is no per-title code: a matched show with a few files beside
/// it and a course with a worksheet in every lesson folder go through the same checks. All words come from <c>config/extras.json</c>.
/// </summary>
internal static partial class ExtraPlacementInference
{
    [GeneratedRegex(@"\bS(?<season>\d{1,2})\s*E(?<episode>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeCode();

    /// <summary>Builds the list of the title's own files (outside Extras-style folders) a supporting file is placed against.</summary>
    public static MainFileIndex BuildIndex(ExtrasLibraryContext context, ExtrasRuleBook book, string itemFolder)
    {
        var root = ExtrasRule.TrimSeparators(Path.GetFullPath(itemFolder));
        var entries = new List<MainFileEntry>();
        foreach (var path in ExtrasRule.EnumerateFiles(book, root))
        {
            if (!context.MainExtensions.Contains(Path.GetExtension(path)) || ExtrasRule.IsTransient(book, path))
            {
                continue;
            }

            var parts = ExtrasRule.RelativeParts(root, path);
            if (parts is null || parts.Take(parts.Length - 1).Any(book.HintFolders.Contains))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(path);
            if (book.ClipSuffix.IsMatch(stem) && !book.EpisodeMarker.IsMatch(stem))
            {
                // A trailer or featurette beside the episodes is an Extra itself, never somewhere a file can belong.
                continue;
            }

            entries.Add(new MainFileEntry(Path.GetFullPath(path), string.Join('/', parts), Path.GetDirectoryName(Path.GetFullPath(path))!, stem, ScopeOfMainFile(context, book, parts, stem)));
        }

        return new MainFileIndex(entries);
    }

    public static ExtraInference Infer(
        ExtrasLibraryContext context, ExtrasRuleBook book, MainFileIndex index, string itemFolder, string filePath, string fileKind)
    {
        var full = Path.GetFullPath(filePath);
        var root = ExtrasRule.TrimSeparators(Path.GetFullPath(itemFolder));
        var parts = ExtrasRule.RelativeParts(root, full) ?? [Path.GetFileName(full)];
        var dirs = parts.Length > 1 ? parts[..^1] : [];
        var stem = Path.GetFileNameWithoutExtension(full);

        var episodic = context.Types.Any(type => type is MediaType.TV or MediaType.Videos or MediaType.Music);
        var (scope, ownerDirectory) = episodic ? FindOwner(context, book, index, root, full, stem) : (ExtraScope.Root, root);
        var alongside = episodic ? FindAlongside(context, book, index, scope, ownerDirectory, stem) : null;

        return new ExtraInference(
            FindRole(book, fileKind, dirs, stem),
            FindGroup(book, dirs),
            scope,
            alongside,
            ExtrasRule.TitleOf(book, stem));
    }

    // ── Step 1: owner ───────────────────────────────────────────────────────────

    private static (ExtraScope Scope, string Directory) FindOwner(
        ExtrasLibraryContext context, ExtrasRuleBook book, MainFileIndex index, string root, string full, string stem)
    {
        var fileDirectory = Path.GetDirectoryName(full)!;
        var isTv = context.Types.Contains(MediaType.TV);

        // A file named after one of the title's own files, with a suffix: "S02E03.mkv" and "S02E03-notes.pdf".
        var named = index.InDirectory(fileDirectory)
            .Where(entry => entry.Stem.Length >= 2 && IsNamedAfter(stem, entry.Stem))
            .OrderByDescending(entry => entry.Stem.Length)
            .FirstOrDefault();
        if (named is not null)
        {
            return (named.Scope, fileDirectory);
        }

        var current = fileDirectory;
        while (ExtrasRule.IsBelow(root, current))
        {
            var name = Path.GetFileName(current);
            var parent = Path.GetDirectoryName(current)!;

            // Extras, Documents, Materials and the like are hints; they never change who a file belongs to.
            if (!book.HintFolders.Contains(name))
            {
                // A folder named after an episode or lesson file that sits beside it: "S02E03/notes.pdf" with "S02E03.mkv".
                var folderNamed = index.InDirectory(parent).FirstOrDefault(entry => entry.Stem.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (folderNamed is not null)
                {
                    return (folderNamed.Scope, parent);
                }

                var seasonMatch = book.SeasonFolder.Match(name);
                if (seasonMatch.Success)
                {
                    return isTv
                        ? (ExtraScope.ForSeason(seasonMatch.Groups["specials"].Success ? 0 : Number(seasonMatch.Groups["season"]) ?? 0), current)
                        : (ExtraScope.ForFolder(RelativePath(root, current)), current);
                }

                var held = index.InDirectory(current).ToList();
                if (held.Count > 0)
                {
                    // A show's episode may sit in a folder of its own with its notes; any other folder of titles' own files is a section.
                    return isTv && held.Count == 1
                        ? (held[0].Scope, current)
                        : (ExtraScope.ForFolder(RelativePath(root, current)), current);
                }
            }

            current = parent;
        }

        return (ExtraScope.Root, root);
    }

    private static ExtraScope ScopeOfMainFile(ExtrasLibraryContext context, ExtrasRuleBook book, string[] relativeParts, string stem)
    {
        if (context.Types.Contains(MediaType.TV))
        {
            if (SeasonEpisodeCode().Match(stem) is { Success: true } code)
            {
                return ExtraScope.ForEpisode(Number(code.Groups["season"]) ?? 0, Number(code.Groups["episode"]) ?? 0);
            }

            var season = relativeParts.Take(relativeParts.Length - 1)
                .Select(folder => book.SeasonFolder.Match(folder))
                .FirstOrDefault(match => match.Success);
            var episode = EpisodeNumber(book, stem);
            if (season is not null && episode is not null)
            {
                return ExtraScope.ForEpisode(season.Groups["specials"].Success ? 0 : Number(season.Groups["season"]) ?? 0, episode.Value);
            }
        }

        return ExtraScope.ForFile(string.Join('/', relativeParts));
    }

    private static bool IsNamedAfter(string fileStem, string mainStem) =>
        fileStem.Equals(mainStem, StringComparison.OrdinalIgnoreCase)
        || (fileStem.Length > mainStem.Length
            && fileStem.StartsWith(mainStem, StringComparison.OrdinalIgnoreCase)
            && "-_. ".Contains(fileStem[mainStem.Length]));

    // ── Step 2: shown alongside ─────────────────────────────────────────────────

    private static ExtraScope? FindAlongside(
        ExtrasLibraryContext context, ExtrasRuleBook book, MainFileIndex index, ExtraScope owner, string ownerDirectory, string stem)
    {
        if (owner.Level is ExtraScopeLevel.Episode or ExtraScopeLevel.File or ExtraScopeLevel.Track)
        {
            return null;
        }

        if (context.Types.Contains(MediaType.TV))
        {
            var candidates = index.Under(ownerDirectory).ToList();
            ExtraScope? coded = null;
            if (SeasonEpisodeCode().Match(stem) is { Success: true } code)
            {
                coded = ExtraScope.ForEpisode(Number(code.Groups["season"]) ?? 0, Number(code.Groups["episode"]) ?? 0);
            }
            else if (owner.Level == ExtraScopeLevel.Season && EpisodeNumber(book, stem) is { } number)
            {
                coded = ExtraScope.ForEpisode(owner.Season ?? 0, number);
            }

            if (coded is { } found && candidates.Any(entry => entry.Scope == found))
            {
                return found;
            }

            return ByWords(book, stem, candidates)?.Scope;
        }

        if (context.Types.Contains(MediaType.Videos))
        {
            // The lessons of the section (or of the course, for a file that sits at its top level).
            var lessons = index.InDirectory(ownerDirectory).ToList();
            if (LeadingNumber(book, stem) is { } number)
            {
                var sameNumber = lessons.Where(entry => LeadingNumber(book, entry.Stem) == number).ToList();
                if (sameNumber.Count == 1)
                {
                    return sameNumber[0].Scope;
                }
            }

            return ByWords(book, stem, lessons)?.Scope;
        }

        return null;
    }

    /// <summary>The one episode or lesson whose title shares at least two words with the file name; null when none or more than one does.</summary>
    private static MainFileEntry? ByWords(ExtrasRuleBook book, string stem, IReadOnlyList<MainFileEntry> candidates)
    {
        var words = Tokens(book, stem);
        if (words.Count < 2)
        {
            return null;
        }

        var scored = candidates
            .Select(entry => (Entry: entry, Shared: Tokens(book, entry.Stem).Count(words.Contains)))
            .Where(item => item.Shared >= 2)
            .OrderByDescending(item => item.Shared)
            .ToList();
        return scored.Count == 1 || (scored.Count > 1 && scored[0].Shared > scored[1].Shared) ? scored[0].Entry : null;
    }

    /// <summary>The words of a name worth comparing: four letters or more, and not one of the words that only say what a file is.</summary>
    private static HashSet<string> Tokens(ExtrasRuleBook book, string name)
    {
        var skip = new HashSet<string>(
            book.Config.RoleWords.SelectMany(entry => entry.Words)
                .Concat(book.Config.DocumentHintWords)
                .SelectMany(word => word.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(
            Regex.Split(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(token => token.Length >= 4 && !skip.Contains(token) && !token.All(char.IsDigit)),
            StringComparer.Ordinal);
    }

    // ── Step 3: role ────────────────────────────────────────────────────────────

    private static string FindRole(ExtrasRuleBook book, string fileKind, string[] dirs, string stem)
    {
        if (book.RoleByWords(ExtrasRuleBook.Spaced(stem), fileKind) is { } named)
        {
            return named;
        }

        // Then the folders the file sits in, nearest first.
        foreach (var directory in dirs.Reverse())
        {
            if (book.FolderRoles.TryGetValue(directory, out var folderRole) && book.Config.RoleFits(fileKind, folderRole))
            {
                return folderRole;
            }

            if (book.RoleByWords(ExtrasRuleBook.Spaced(directory), fileKind) is { } byWords)
            {
                return byWords;
            }
        }

        return book.Config.DefaultRoleFor(fileKind);
    }

    // ── Step 4: group ───────────────────────────────────────────────────────────

    private static string? FindGroup(ExtrasRuleBook book, string[] dirs)
    {
        for (var index = 0; index < dirs.Length - 1; index++)
        {
            if (!book.GroupParentFolders.Contains(dirs[index]))
            {
                continue;
            }

            var name = dirs[index + 1];
            if (book.FolderRoles.ContainsKey(name)
                || book.HintFolders.Contains(name)
                || book.GroupParentFolders.Contains(name)
                || book.SeasonFolder.IsMatch(name))
            {
                return null;
            }

            var cleaned = ExtrasRule.TitleOf(book, name);
            return cleaned.Length == 0 ? null : cleaned;
        }

        return null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The number at the start of a name: "02 Components" and "Lesson 2 - Components" are both 2.</summary>
    private static int? LeadingNumber(ExtrasRuleBook book, string stem) =>
        Number(book.LeadingNumber.Match(stem).Groups["n"]);

    /// <summary>An episode number a name carries: "Episode 3", "E03", or the number it starts with.</summary>
    private static int? EpisodeNumber(ExtrasRuleBook book, string stem) =>
        book.EpisodeWord.Match(stem) is { Success: true } named ? Number(named.Groups["episode"]) : LeadingNumber(book, stem);

    private static string RelativePath(string root, string directory) =>
        Path.GetRelativePath(root, directory).Replace('\\', '/');

    private static int? Number(Group group) =>
        group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}

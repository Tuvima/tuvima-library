namespace MediaEngine.Ingestion.Organise;

/// <summary>
/// Finds the files that travel with a media file: subtitles (keeping a language suffix such as <c>.en.srt</c>), notes
/// (<c>.nfo</c>), artwork, cue sheets, lyrics and playlists that share its base name. Anything else in the folder is left
/// exactly where it is.
/// </summary>
public static class SidecarFiles
{
    /// <summary>The extensions a sidecar may have. A media file is never a sidecar.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup",
        ".nfo",
        ".jpg", ".jpeg", ".png", ".webp",
        ".cue", ".lrc",
        ".m3u", ".m3u8",
    };

    private const int MaxSuffixTokens = 3;
    private const int MaxSuffixTokenLength = 12;

    /// <summary>One file that moves with a media file.</summary>
    /// <param name="Path">Where the sidecar is now.</param>
    /// <param name="Remainder">Everything after the media file's base name, for example <c>.en.forced.srt</c>.</param>
    /// <param name="KeepName">True for a file that keeps its own name in the new folder (a folder-level cover or poster).</param>
    public sealed record Sidecar(string Path, string Remainder, bool KeepName = false)
    {
        /// <summary>True for a playlist whose lines may name the media file.</summary>
        public bool IsPlaylist => Remainder.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
            || Remainder.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lists the sidecars that belong to <paramref name="mediaPath"/>: files in the same folder whose name is the media
    /// file's base name followed by an optional language/flag suffix and a sidecar extension. When another media file in
    /// the folder has a longer base name that also fits, the sidecar belongs to that one instead.
    /// </summary>
    /// <param name="mediaExtensions">Every extension that counts as a media file, so a neighbour's sidecars are not taken.</param>
    public static IReadOnlyList<Sidecar> Find(string mediaPath, IReadOnlySet<string> mediaExtensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentNullException.ThrowIfNull(mediaExtensions);

        var folder = System.IO.Path.GetDirectoryName(mediaPath);
        var stem = System.IO.Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(stem) || !Directory.Exists(folder))
        {
            return [];
        }

        string[] siblings;
        try
        {
            siblings = Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed has no sidecars to move.
            _ = ex;
            return [];
        }

        var otherStems = siblings
            .Where(path => mediaExtensions.Contains(System.IO.Path.GetExtension(path))
                && !PathsEqual(path, mediaPath))
            .Select(path => System.IO.Path.GetFileNameWithoutExtension(path))
            .Where(other => other.Length > stem.Length
                && other.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                && other[stem.Length] == '.')
            .ToList();

        var found = new List<Sidecar>();
        foreach (var path in siblings.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(path);
            if (PathsEqual(path, mediaPath)
                || name.Length <= stem.Length
                || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                || name[stem.Length] != '.')
            {
                continue;
            }

            var remainder = name[stem.Length..];
            if (!IsSidecarRemainder(remainder))
            {
                continue;
            }

            // A longer neighbour (Movie.2021.mkv beside Movie.mkv) owns the files that start with its own name.
            if (otherStems.Any(other => name.StartsWith(other, StringComparison.OrdinalIgnoreCase)
                && name.Length > other.Length
                && name[other.Length] == '.'
                && IsSidecarRemainder(name[other.Length..])))
            {
                continue;
            }

            found.Add(new Sidecar(path, remainder));
        }

        return found;
    }

    private static readonly string[] ArtKinds = ["poster", "fanart", "logo"];

    private static readonly IReadOnlySet<string> ArtworkExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp",
    };

    /// <summary>File names that describe a whole folder rather than one media file.</summary>
    private static readonly IReadOnlySet<string> FolderLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "poster.jpg", "poster.png", "poster-thumb.jpg", "fanart.jpg", "fanart.png", "logo.png", "logo.jpg",
        "cover.jpg", "cover.jpeg", "cover.png", "metadata.json", "metadata.opf",
    };

    /// <summary>
    /// Artwork that belongs to a media file but is not named like a sidecar: <c>Name-poster.jpg</c>, <c>Name-fanart-1.jpg</c>,
    /// <c>Name-logo.png</c>. When <paramref name="includeFolderLevel"/> is true (nothing else is left in the folder) the files
    /// that describe the whole folder (<c>poster.jpg</c>, <c>cover.jpg</c>, <c>metadata.json</c>...) come too, keeping their names.
    /// </summary>
    public static IReadOnlyList<Sidecar> FindArtwork(string mediaPath, IReadOnlySet<string> mediaExtensions, bool includeFolderLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentNullException.ThrowIfNull(mediaExtensions);

        var folder = System.IO.Path.GetDirectoryName(mediaPath);
        var stem = System.IO.Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(stem) || !Directory.Exists(folder))
        {
            return [];
        }

        string[] siblings;
        try
        {
            siblings = Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed has nothing to move.
            _ = ex;
            return [];
        }

        var longerStems = siblings
            .Where(path => mediaExtensions.Contains(System.IO.Path.GetExtension(path)) && !PathsEqual(path, mediaPath))
            .Select(path => System.IO.Path.GetFileNameWithoutExtension(path))
            .Where(other => other.Length > stem.Length
                && other.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                && (other[stem.Length] is '.' or '-' or ' '))
            .ToList();

        var found = new List<Sidecar>();
        foreach (var path in siblings.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(path);
            if (includeFolderLevel && FolderLevelNames.Contains(name))
            {
                found.Add(new Sidecar(path, name, KeepName: true));
                continue;
            }

            var prefix = stem + "-";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !ArtworkExtensions.Contains(System.IO.Path.GetExtension(name)))
            {
                continue;
            }

            var rest = name[prefix.Length..];
            if (!ArtKinds.Any(kind => rest.StartsWith(kind, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (longerStems.Any(other => name.StartsWith(other + "-", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            found.Add(new Sidecar(path, name[stem.Length..]));
        }

        return found;
    }

    /// <summary>Where a sidecar goes once its media file has moved: the new base name plus the same remainder.</summary>
    public static string DestinationFor(Sidecar sidecar, string newMediaPath)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentException.ThrowIfNullOrWhiteSpace(newMediaPath);

        var folder = System.IO.Path.GetDirectoryName(newMediaPath) ?? string.Empty;
        if (sidecar.KeepName)
        {
            return System.IO.Path.Combine(folder, System.IO.Path.GetFileName(sidecar.Path));
        }

        var newStem = System.IO.Path.GetFileNameWithoutExtension(newMediaPath);
        return System.IO.Path.Combine(folder, newStem + sidecar.Remainder);
    }

    /// <summary>
    /// Rewrites the lines of a playlist that name <paramref name="oldMediaFileName"/> so they name the new file, and leaves
    /// every other line as it was. Returns <see langword="null"/> when no line needed to change.
    /// </summary>
    public static string? RewritePlaylist(string playlistText, string oldMediaFileName, string newMediaFileName)
    {
        ArgumentNullException.ThrowIfNull(playlistText);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldMediaFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newMediaFileName);

        var changed = false;
        var lines = playlistText.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
            var fileName = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
            if (!string.Equals(fileName, oldMediaFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prefix = lastSeparator >= 0 ? trimmed[..(lastSeparator + 1)] : string.Empty;
            lines[index] = prefix + newMediaFileName + line[trimmed.Length..];
            changed = true;
        }

        return changed ? string.Join('\n', lines) : null;
    }

    private static bool IsSidecarRemainder(string remainder)
    {
        // remainder looks like ".en.forced.srt": zero to three short language/flag tokens, then the extension.
        var tokens = remainder.TrimStart('.').Split('.');
        if (tokens.Length == 0 || !Extensions.Contains("." + tokens[^1]))
        {
            return false;
        }

        if (tokens.Length - 1 > MaxSuffixTokens)
        {
            return false;
        }

        return tokens[..^1].All(token => token.Length is > 0 and <= MaxSuffixTokenLength
            && token.All(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

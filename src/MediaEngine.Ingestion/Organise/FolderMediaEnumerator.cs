namespace MediaEngine.Ingestion.Organise;

/// <summary>A fast, read-only walk of a folder that lists its media files in a stable order. It never opens a file.</summary>
public static class FolderMediaEnumerator
{
    /// <summary>Used when the media extension catalogue is not available (tests); the Engine passes its own catalogue.</summary>
    public static readonly IReadOnlySet<string> DefaultMediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".ts", ".m2ts", ".mpg", ".mpeg",
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".opus", ".wav", ".aac", ".wma",
        ".epub", ".pdf", ".mobi", ".azw3", ".cbz", ".cbr", ".cb7",
    };

    /// <summary>
    /// Lists the media files under <paramref name="root"/> as full paths, in path order. Folders whose name starts with a
    /// dot (Tuvima's own working folders, hidden folders) are skipped.
    /// </summary>
    /// <param name="stopAfter">Stop once this many files are found (for a quick count of a very large folder).</param>
    /// <param name="skipLinks">Leave out shortcuts (links and junctions): the walk never follows one, so it cannot loop or leave the folder.</param>
    public static List<string> Enumerate(
        string root,
        bool includeSubdirectories,
        IReadOnlySet<string> mediaExtensions,
        int stopAfter = int.MaxValue,
        CancellationToken ct = default,
        bool skipLinks = false) =>
        EnumerateCountingLinks(root, includeSubdirectories, mediaExtensions, stopAfter, ct, skipLinks).Files;

    /// <summary>
    /// The same walk as <see cref="Enumerate"/>, also reporting how many shortcuts it passed over (media files that are
    /// links, and folders that are links), so a preview can say that some were left alone.
    /// </summary>
    public static (List<string> Files, int SkippedLinks) EnumerateCountingLinks(
        string root,
        bool includeSubdirectories,
        IReadOnlySet<string> mediaExtensions,
        int stopAfter = int.MaxValue,
        CancellationToken ct = default,
        bool skipLinks = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(mediaExtensions);

        var found = new List<string>();
        var skippedLinks = 0;
        if (!Directory.Exists(root))
        {
            return (found, skippedLinks);
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0 && found.Count < stopAfter)
        {
            ct.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (!mediaExtensions.Contains(Path.GetExtension(file)))
                    {
                        continue;
                    }

                    if (skipLinks && IsLink(file))
                    {
                        skippedLinks++;
                        continue;
                    }

                    found.Add(file);
                    if (found.Count >= stopAfter)
                    {
                        break;
                    }
                }

                if (includeSubdirectories)
                {
                    foreach (var child in Directory.EnumerateDirectories(folder))
                    {
                        if (Path.GetFileName(child).StartsWith('.'))
                        {
                            continue;
                        }

                        if (skipLinks && IsLink(child))
                        {
                            skippedLinks++;
                            continue;
                        }

                        pending.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed is skipped; the rest of the library is still organised.
                _ = ex;
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return (found, skippedLinks);
    }

    /// <summary>True for a shortcut: a symbolic link or a Windows junction. The merge never follows these, so it can never reach a file through a second name.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            // Cloud placeholders (OneDrive, Dropbox) and deduplicated files are reparse points too, but they are ordinary files
            // that live where they appear; only a symbolic link or junction has a target.
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing there, so nothing is being reached through a shortcut.
            _ = ex;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something that cannot even be inspected is treated as a link, so it is left alone.
            _ = ex;
            return true;
        }
    }

    /// <summary>
    /// True when the file, or any folder between it and <paramref name="root"/>, is a shortcut. With
    /// <paramref name="includeRoot"/> false the root itself is not tested: a folder that is configured through a shortcut is
    /// still the folder the person chose, and only shortcuts inside it are a surprise.
    /// </summary>
    public static bool IsReachedThroughLink(string path, string root, bool includeRoot = true)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (IsLink(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                return includeRoot && IsLink(root);
            }

            current = parent;
        }

        return false;
    }
}

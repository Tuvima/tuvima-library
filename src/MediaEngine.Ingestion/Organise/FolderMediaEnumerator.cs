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
    public static List<string> Enumerate(
        string root,
        bool includeSubdirectories,
        IReadOnlySet<string> mediaExtensions,
        int stopAfter = int.MaxValue,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(mediaExtensions);

        var found = new List<string>();
        if (!Directory.Exists(root))
        {
            return found;
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
                    if (mediaExtensions.Contains(Path.GetExtension(file)))
                    {
                        found.Add(file);
                        if (found.Count >= stopAfter)
                        {
                            break;
                        }
                    }
                }

                if (includeSubdirectories)
                {
                    foreach (var child in Directory.EnumerateDirectories(folder))
                    {
                        if (!Path.GetFileName(child).StartsWith('.'))
                        {
                            pending.Push(child);
                        }
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
        return found;
    }
}

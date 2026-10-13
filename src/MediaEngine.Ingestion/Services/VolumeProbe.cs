namespace MediaEngine.Ingestion.Services;

/// <summary>Finds the drive or mount a path lives on, so free space and "same drive" can be answered without moving anything.</summary>
internal static class VolumeProbe
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>The drive or mount whose root is the longest match for <paramref name="path"/>, or <see langword="null"/> when none is known (for example a network share).</summary>
    internal static DriveInfo? Find(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        DriveInfo? best = null;
        var bestLength = -1;
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                root = drive.RootDirectory.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that cannot report its root (an unplugged removable drive) cannot hold this path.
                _ = ex;
                continue;
            }

            if (root.Length > bestLength && IsUnder(full, root))
            {
                best = drive;
                bestLength = root.Length;
            }
        }

        return best;
    }

    /// <summary>True when both paths are known to live on the same drive or mount. Unknown counts as different, which is the safe answer for a move.</summary>
    internal static bool SameVolume(string first, string second)
    {
        var a = Find(first);
        var b = Find(second);
        return a is not null
            && b is not null
            && string.Equals(a.RootDirectory.FullName, b.RootDirectory.FullName, PathComparison);
    }

    /// <summary>Bytes free on the drive that holds <paramref name="path"/>, or <see langword="null"/> when that cannot be read.</summary>
    internal static long? AvailableBytes(string path)
    {
        try
        {
            var drive = Find(path);
            return drive is { IsReady: true } ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Free space is unknown for this location (a network share); the copy reports a full disk itself.
            _ = ex;
            return null;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        if (path.Equals(root, PathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }
}

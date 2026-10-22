namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// Tells whether a file is in use by anything else (being played, or held by another program) before Library files it on its own
/// (Library setup D63). It opens the file exclusively and lets go at once, so the file itself is never changed.
/// </summary>
internal static class FileInUseProbe
{
    /// <summary>
    /// True when the file exists but cannot be opened exclusively because something else holds it. A file that is not there, or one
    /// that cannot be opened for another reason such as permissions, is not "in use": those are left to the move to deal with.
    /// </summary>
    public static bool IsInUse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException or DriveNotFoundException)
        {
            // The file went away between the check and the open: it is not held by anything.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Not a sharing problem: the move itself will report what it cannot do.
            return false;
        }
        catch (IOException)
        {
            // A sharing or lock violation: another handle has the file open.
            return true;
        }
    }
}

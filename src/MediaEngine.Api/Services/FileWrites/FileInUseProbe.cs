namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// Tells whether a file is in use by anything else (being played, or held by another program) before Library files it on its own
/// (Library setup D63). It opens the file exclusively and lets go at once, so the file itself is never changed.
/// </summary>
internal static class FileInUseProbe
{
    // The error numbers .NET reports for a lock conflict: Windows ERROR_SHARING_VIOLATION (0x20) and ERROR_LOCK_VIOLATION (0x21) in the
    // low word of the HResult; Linux EWOULDBLOCK (11) and macOS EWOULDBLOCK (35), which .NET raises when it cannot take its file lock.
    private const int SharingViolation = 0x20;
    private const int LockViolation = 0x21;
    private const int LinuxWouldBlock = 11;
    private const int MacWouldBlock = 35;

    /// <summary>
    /// True when the file exists but cannot be opened exclusively because something else holds it (a sharing or lock violation). A file
    /// that is not there, one that cannot be opened for another reason such as permissions, and any other disk error are not "in use":
    /// those are left to the move, where they count as a real failed attempt.
    /// </summary>
    /// <remarks>
    /// On Linux and macOS the system does not stop one program opening a file another has open, so the probe only sees programs that
    /// take a lock on it (as .NET itself does). A player that merely reads the file is invisible to it there; on Windows any open
    /// handle that does not share the file is seen.
    /// </remarks>
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
        catch (IOException ex) when (IsLockConflict(ex))
        {
            // A sharing or lock violation: another handle has the file open.
            return true;
        }
        catch (IOException)
        {
            // Any other disk error is not "in use": the move meets it too and counts it as a real failed attempt.
            return false;
        }
    }

    internal static bool IsLockConflict(IOException ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xFFFF) is SharingViolation or LockViolation
        : ex.HResult is LinuxWouldBlock or MacWouldBlock;
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaEngine.Ingestion.Organise;

/// <summary>What a check of two paths could establish.</summary>
public enum FileIdentityResult
{
    /// <summary>Proven to be two different files (or one of the paths holds no file).</summary>
    Different,

    /// <summary>Proven to be one file under two names.</summary>
    Same,

    /// <summary>
    /// Could not be told: the drive does not give files an identity Tuvima can read (for example a network share mounted
    /// without server inode numbers and without byte-range locks), or a file could not be opened to compare.
    /// </summary>
    Unproven,
}

/// <summary>
/// Tells whether two paths are the same file on disk. Two names are not "a copy" of each other when they lead to one file:
/// removing one of them would remove the only file. This is true for a letter-case-only rename on a drive that ignores case
/// (SMB and CIFS shares, exFAT, macOS), for a hard link, and for a folder reached by two routes.
/// </summary>
public static class FileIdentity
{
    /// <summary>
    /// True when <paramref name="first"/> and <paramref name="second"/> are the same file, or when that cannot be told for
    /// two files that both exist. The answer errs towards "same" on purpose: callers only use it to refuse a delete, so a
    /// doubt keeps both names. A path that does not exist is a different file (there is nothing there to be the same).
    /// </summary>
    public static bool AreSameFile(string first, string second) => Compare(first, second) != FileIdentityResult.Different;

    /// <summary>
    /// The same check as <see cref="AreSameFile"/>, but a doubt is reported as <see cref="FileIdentityResult.Unproven"/>
    /// instead of "same". A caller that would act on a "same" answer (rename a file whose folder differs only in letter case)
    /// uses this so it can leave the file alone when identity cannot be proven.
    /// </summary>
    public static FileIdentityResult Compare(string first, string second)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentException.ThrowIfNullOrWhiteSpace(second);

        var left = Path.GetFullPath(first);
        var right = Path.GetFullPath(second);
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return FileIdentityResult.Same;
        }

        if (!File.Exists(left) || !File.Exists(right))
        {
            return FileIdentityResult.Different;
        }

        // File names that differ only in letter case, in one folder, are one file unless the drive keeps them apart and holds
        // both. The folder listing shows the names exactly as they are stored, so two exact entries mean two files. When the
        // file names are the same and only a folder's letter case differs, the listing cannot tell, so the file itself is
        // compared below.
        if (string.Equals(Path.GetDirectoryName(left), Path.GetDirectoryName(right), StringComparison.Ordinal)
            && !string.Equals(Path.GetFileName(left), Path.GetFileName(right), StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(left), Path.GetFileName(right), StringComparison.OrdinalIgnoreCase))
        {
            return BothStoredSeparately(left, right) ? FileIdentityResult.Different : FileIdentityResult.Same;
        }

        try
        {
            return OperatingSystem.IsWindows() ? SameOnWindows(left, right) : SameOnUnix(left, right);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be opened to be compared is not proven different.
            _ = ex;
            return FileIdentityResult.Unproven;
        }
    }

    private static bool BothStoredSeparately(string left, string right)
    {
        var folder = Path.GetDirectoryName(left);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        var leftName = Path.GetFileName(left);
        var rightName = Path.GetFileName(right);
        var foundLeft = false;
        var foundRight = false;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(entry);
                foundLeft |= string.Equals(name, leftName, StringComparison.Ordinal);
                foundRight |= string.Equals(name, rightName, StringComparison.Ordinal);
                if (foundLeft && foundRight)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed cannot show two stored names; the caller then compares the file itself.
            _ = ex;
        }

        return false;
    }

    private static FileIdentityResult SameOnWindows(string left, string right)
    {
        using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!TryGetWindowsId(a.SafeFileHandle, out var idA) || !TryGetWindowsId(b.SafeFileHandle, out var idB))
        {
            return FileIdentityResult.Unproven;
        }

        return idA == idB ? FileIdentityResult.Same : FileIdentityResult.Different;
    }

    private static bool TryGetWindowsId(SafeFileHandle handle, out (uint Volume, uint High, uint Low) id)
    {
        if (GetFileInformationByHandle(handle, out var info))
        {
            id = (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
            return true;
        }

        id = default;
        return false;
    }

    // On Linux and macOS .NET takes an exclusive advisory lock on a file opened with FileShare.None, and that lock belongs to
    // the file, not to the path. A second open of the same file by another name therefore fails; a different file opens. A
    // file that is busy for another reason also fails, which is the safe side of the answer.
    private static FileIdentityResult SameOnUnix(string left, string right)
    {
        using var first = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.None);

        // The probe only counts when locking really works on this drive: the same file opened twice must be refused. If it
        // is not (locking switched off, or a network drive that does not lock), the answer is unproven; a caller that only
        // refuses deletes treats that as "same".
        try
        {
            using var again = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.None);
            return FileIdentityResult.Unproven;
        }
        catch (IOException)
        {
            // Refused, as it should be: locking works here.
        }

        try
        {
            using var second = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.None);
            return FileIdentityResult.Different;
        }
        catch (IOException)
        {
            return FileIdentityResult.Same;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}

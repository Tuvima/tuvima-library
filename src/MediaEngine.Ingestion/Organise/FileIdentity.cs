using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaEngine.Ingestion.Organise;

/// <summary>
/// Tells whether two paths are the same file on disk. Two names are not "a copy" of each other when they lead to one file:
/// removing one of them would remove the only file. This is true for a letter-case-only rename on a drive that ignores case
/// (SMB and CIFS shares, exFAT, macOS), for a hard link, and for a folder reached by two routes.
/// </summary>
public static class FileIdentity
{
    /// <summary>
    /// True when <paramref name="first"/> and <paramref name="second"/> are the same file, or when that cannot be told. The
    /// answer errs towards "same" on purpose: callers only use it to refuse a delete, so a doubt keeps both names.
    /// </summary>
    public static bool AreSameFile(string first, string second)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentException.ThrowIfNullOrWhiteSpace(second);

        var left = Path.GetFullPath(first);
        var right = Path.GetFullPath(second);
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        if (!File.Exists(left) || !File.Exists(right))
        {
            return false;
        }

        // Names that differ only in letter case are one file unless the drive keeps them apart and holds both. The folder
        // listing shows the names exactly as they are stored, so two exact entries mean two files.
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return !BothStoredSeparately(left, right);
        }

        try
        {
            return OperatingSystem.IsWindows() ? SameOnWindows(left, right) : SameOnUnix(left, right);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be opened to be compared is not proven different.
            _ = ex;
            return true;
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
            _ = ex;
        }

        return false;
    }

    private static bool SameOnWindows(string left, string right)
    {
        using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!TryGetWindowsId(a.SafeFileHandle, out var idA) || !TryGetWindowsId(b.SafeFileHandle, out var idB))
        {
            return true;
        }

        return idA == idB;
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
    private static bool SameOnUnix(string left, string right)
    {
        using var first = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            using var second = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
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

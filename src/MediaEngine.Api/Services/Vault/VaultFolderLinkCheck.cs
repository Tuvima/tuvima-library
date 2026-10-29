namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// Finds links (symbolic links and Windows junctions) inside a Vault folder (Vault sections D29, security review leak 8). A link that points
/// outside the folder could pull files from a normal library into the Vault, or leave Vault files reachable from a normal library, so a folder
/// that holds one is refused when it is added and a destination that holds one is refused before a move. Links are never followed.
/// </summary>
internal static class VaultFolderLinkCheck
{
    /// <summary>The most entries looked at in one folder; a folder bigger than this is refused rather than half-checked.</summary>
    internal const int MaxEntries = 500_000;

    /// <summary>
    /// True when <paramref name="folder"/>, or anything beneath it, is a link, or when the folder is too large to check or cannot be read.
    /// Fails closed: "cannot tell" counts as a link.
    /// </summary>
    internal static bool HoldsLink(string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        var seen = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                var directory = new DirectoryInfo(current);
                if (IsLink(directory))
                {
                    return true;
                }

                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if (++seen > MaxEntries || IsLink(entry))
                    {
                        return true;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child.FullName);
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLink(FileSystemInfo entry) =>
        entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}

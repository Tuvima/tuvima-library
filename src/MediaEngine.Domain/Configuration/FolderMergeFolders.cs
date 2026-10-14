namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The holding folder a folder merge puts files in when they cannot be named yet. The Engine's merge organiser and the Review
/// queue's "From merge" badge both use this one name, so the badge always follows the folder.
/// </summary>
public static class FolderMergeFolders
{
    /// <summary>Folder, inside the merge target, where files that cannot be named yet wait until a person confirms them.</summary>
    public const string HoldingFolderName = "From merge";

    /// <summary>True when <paramref name="path"/> is anywhere inside the From merge folder of the managed folder <paramref name="managedRoot"/>.</summary>
    public static bool IsInsideHoldingFolder(string managedRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(managedRoot) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var holding = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot)), HoldingFolderName);
            return ImportFolderRules.IsInside(holding, path) && !PathEqualsFolder(holding, path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A saved path that is not a valid path cannot hold anything.
            _ = ex;
            return false;
        }
    }

    private static bool PathEqualsFolder(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), folder, comparison);
    }
}

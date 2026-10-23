namespace MediaEngine.Ingestion.Services.ImportUploads;

/// <summary>
/// Real paths for the checks that keep an upload inside the Import folder. A folder or file inside the Import folder can be a symbolic
/// link or a junction pointing somewhere else, and a path that merely starts with the Import folder's name would then lead outside it;
/// the checks therefore compare the paths links resolve to.
/// </summary>
internal static class ImportUploadPaths
{
    // A chain of links longer than this is treated as a loop and left unresolved, which the containment check then refuses.
    private const int MaxLinkDepth = 32;

    /// <summary>
    /// <paramref name="path"/> made absolute with every link and junction on the way followed. Parts that do not exist yet are kept as
    /// written, since nothing can link from a place that is not there.
    /// </summary>
    public static string Resolve(string path) => Resolve(path, 0);

    /// <summary>True when the real path of <paramref name="path"/> is the real path of <paramref name="root"/>, or inside it.</summary>
    public static bool IsInside(string path, string root) =>
        PathSafety.IsContainedBy(Path.TrimEndingDirectorySeparator(Resolve(path)), Path.TrimEndingDirectorySeparator(Resolve(root)));

    private static string Resolve(string path, int depth)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        var parts = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
            if (info is null)
            {
                // Nothing exists from here on, so nothing can be a link.
                return Path.Combine(next, Path.Combine(parts[(i + 1)..]));
            }

            if (info.LinkTarget is null)
            {
                current = next;
                continue;
            }

            if (depth >= MaxLinkDepth)
            {
                return next;
            }

            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException)
            {
                // A link that loops or cannot be read leads nowhere real: keep it as it is, so it is judged by where it sits, and
                // the file operation that follows fails on its own if it truly is unusable.
                return next;
            }

            current = target is null ? next : Resolve(target.FullName, depth + 1);
        }

        return current;
    }
}

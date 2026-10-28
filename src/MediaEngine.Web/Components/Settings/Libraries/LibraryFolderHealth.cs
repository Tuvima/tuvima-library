using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Components.Settings;

/// <summary>One state for a set of folders: healthy, or a warning with the words for its tooltip.</summary>
public sealed record LibraryHealth(bool Healthy, string? Text)
{
    public static LibraryHealth Ok { get; } = new(true, null);
}

/// <summary>
/// The live read/write check of library folders, shared by the folders panel (a status line under each path) and the
/// Libraries overview and library pages (one status per library).
/// </summary>
public static class LibraryFolderHealth
{
    public const string Unavailable = "Folder unavailable";
    public const string NoAccess = "No access";

    /// <summary>
    /// Checks every distinct folder path of <paramref name="configuration"/> (at most four at once) and returns path to issue.
    /// A folder that does not exist is "Folder unavailable"; one that cannot be read, or (when Managed) written, is "No access".
    /// A folder whose check could not be made is left out, not reported as a problem.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> CheckAsync(
        LibrariesConfigurationDto configuration,
        Func<string, Task<PathTestResultDto?>> testPathAsync)
    {
        var folders = configuration.Libraries.SelectMany(library => library.Sources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Path))
            .GroupBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        using var gate = new SemaphoreSlim(4);
        var checks = folders.Select(async source =>
        {
            await gate.WaitAsync();
            try { return (Source: source, Result: await testPathAsync(source.Path)); }
            finally { gate.Release(); }
        }).ToList();
        var issues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, result) in await Task.WhenAll(checks))
        {
            if (result is null)
            {
                continue;
            }

            if (!result.Exists)
            {
                issues[source.Path] = Unavailable;
            }
            else if (!result.HasRead || (IsManaged(source) && !result.HasWrite))
            {
                issues[source.Path] = NoAccess;
            }
        }

        return issues;
    }

    /// <summary>The worst state among <paramref name="sources"/>: healthy, or a warning such as "1 folder unavailable" or "2 folders need access".</summary>
    public static LibraryHealth Worst(IEnumerable<LibrarySourceDto> sources, IReadOnlyDictionary<string, string> issues)
    {
        var unavailable = 0;
        var noAccess = 0;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Path) || !issues.TryGetValue(source.Path, out var issue))
            {
                continue;
            }

            if (issue == Unavailable)
            {
                unavailable++;
            }
            else
            {
                noAccess++;
            }
        }

        if (unavailable + noAccess == 0)
        {
            return LibraryHealth.Ok;
        }

        var parts = new List<string>();
        if (unavailable > 0)
        {
            parts.Add($"{Folders(unavailable)} unavailable");
        }

        if (noAccess > 0)
        {
            parts.Add($"{Folders(noAccess)} {(noAccess == 1 ? "needs" : "need")} access");
        }

        return new LibraryHealth(false, string.Join(", ", parts));
    }

    private static string Folders(int count) => count == 1 ? "1 folder" : $"{count} folders";

    private static bool IsManaged(LibrarySourceDto source) => source.ManagementMode == "managed_by_tuvima";
}

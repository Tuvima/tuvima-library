using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;

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

    /// <summary>The folder's files are held because nothing that can identify them works yet (Plugins screens v4 D38).</summary>
    public const string WaitingForSource = "Waiting for a source";

    /// <summary>
    /// The kinds whose import is held right now: Movies, TV Shows and Comics while no source that can identify them is switched on and
    /// has its key. Read from the same plugin list the Plugins page shows, with the rule the Engine uses.
    /// </summary>
    public static IReadOnlySet<string> HeldKinds(IEnumerable<PluginSummaryResponse> plugins)
    {
        var list = plugins.ToList();
        if (!list.Any(plugin => plugin.Provider is not null))
        {
            // No source was listed at all, which means the list could not be read; nothing is claimed held.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var working = list
            .Where(plugin => plugin.Provider is not null && plugin.Enabled && string.IsNullOrWhiteSpace(plugin.LoadError)
                && (!plugin.Provider.RequiresKey || plugin.Provider.KeyPresent))
            .Select(plugin => plugin.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new[] { "movies", "tv", "comics" }
            .Where(kind => IdentificationSourceRules.IsHeld(kind, working.Contains))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Marks the folders of every library whose kind is held as Waiting for a source, unless the folder already has a worse problem
    /// (offline, no access), which stays what is shown.
    /// </summary>
    public static IReadOnlyDictionary<string, string> WithHeldKinds(
        IReadOnlyDictionary<string, string> issues,
        LibrariesConfigurationDto configuration,
        IReadOnlySet<string> heldKinds)
    {
        if (heldKinds.Count == 0)
        {
            return issues;
        }

        var merged = new Dictionary<string, string>(issues, StringComparer.OrdinalIgnoreCase);
        foreach (var library in configuration.Libraries.Where(library => heldKinds.Contains(library.KindKey)))
        {
            foreach (var source in library.Sources.Where(source => !string.IsNullOrWhiteSpace(source.Path)))
            {
                merged.TryAdd(source.Path, WaitingForSource);
            }
        }

        return merged;
    }

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
        var waiting = 0;
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
            else if (issue == WaitingForSource)
            {
                waiting++;
            }
            else
            {
                noAccess++;
            }
        }

        if (unavailable + noAccess + waiting == 0)
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

        if (waiting > 0)
        {
            parts.Add(WaitingForSource);
        }

        return new LibraryHealth(false, string.Join(", ", parts));
    }

    private static string Folders(int count) => count == 1 ? "1 folder" : $"{count} folders";

    private static bool IsManaged(LibrarySourceDto source) => source.ManagementMode == "managed_by_tuvima";
}

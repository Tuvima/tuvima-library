using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>Whose library a file in the Import area is filed into: a person's own space, or the Shared Library.</summary>
public sealed record IntakeDestination(string ScopeKind, Guid? OwnerProfileId)
{
    public const string Personal = "personal";
    public const string Shared = "shared";

    public static IntakeDestination Person(Guid profileId) => new(Personal, profileId);

    public static IntakeDestination SharedLibrary { get; } = new(Shared, null);
}

/// <summary>
/// Where the Import and Rejected areas are, and whose library a file in them belongs to (Photo and video filing spec D1, D3, D18).
/// Both areas sit outside every library folder, so a library folder such as <c>E:\Photos</c> only ever holds photos and can be backed up
/// on its own. The Import area is the one global Import folder when it is set; otherwise a hidden folder beside the View root keeps
/// arrivals out of the library folder too.
/// </summary>
public sealed class ViewIntakeAreas(
    IConfigurationLoader configuration,
    IProfileRepository profiles)
{
    /// <summary>The folder, inside a section, whose files are filed into the Shared Library.</summary>
    public const string SharedFolderName = "Shared";

    /// <summary>The hidden folder, beside the View root, that holds the sections when no Import folder is set.</summary>
    public const string FallbackFolderName = ".tuvima-intake";

    private const string RejectedFolderName = "rejected";
    private const string UploadsFolderName = "uploads";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>The folder that holds the Photos and Personal videos sections, or null when there is nowhere to put them.</summary>
    public string? ImportRoot()
    {
        var libraries = configuration.LoadLibraries();
        if (ImportFolderRules.Find(libraries) is { } location)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.Path));
        }

        var storage = libraries.StorageLocations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, libraries.ViewStorage.StorageLocationId, StringComparison.OrdinalIgnoreCase));
        return storage is null || string.IsNullOrWhiteSpace(storage.Path)
            ? null
            : Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(storage.Path)), FallbackFolderName);
    }

    /// <summary>The section folder for a kind (<c>photos</c> or <c>personal_videos</c>).</summary>
    public string? SectionPath(string contentKind) =>
        ImportRoot() is { } root ? Path.Combine(root, SectionName(contentKind)) : null;

    /// <summary>The Rejected area for a kind, outside the library folders and never read by the catalogue pipeline.</summary>
    public string? RejectedPath(string contentKind) =>
        ImportRoot() is { } root ? Path.Combine(root, ImportFolderRules.WorkingFolderName, RejectedFolderName, SectionName(contentKind)) : null;

    /// <summary>Where uploads are written while they arrive, so a half-received file is never mistaken for a waiting one.</summary>
    public string? UploadWorkingPath() =>
        ImportRoot() is { } root ? Path.Combine(root, ImportFolderRules.WorkingFolderName, UploadsFolderName) : null;

    public static string SectionName(string contentKind) =>
        contentKind == ViewContentKinds.PersonalVideos ? ImportFolderRules.PersonalVideosSectionName : ImportFolderRules.PhotosSectionName;

    /// <summary>
    /// Creates the sections and the Rejected area. A configured Import folder that is not there (a drive that is offline) is never
    /// replaced by an empty local folder; the fallback folder is created because it is ours.
    /// </summary>
    public bool EnsureFolders()
    {
        var root = ImportRoot();
        if (root is null)
        {
            return false;
        }

        var usingFallback = ImportFolderRules.Find(configuration.LoadLibraries()) is null;
        if (!usingFallback && !Directory.Exists(root))
        {
            return false;
        }

        foreach (var kind in new[] { ViewContentKinds.Photos, ViewContentKinds.PersonalVideos })
        {
            Directory.CreateDirectory(SectionPath(kind)!);
            Directory.CreateDirectory(RejectedPath(kind)!);
        }

        Directory.CreateDirectory(UploadWorkingPath()!);
        return true;
    }

    /// <summary>The folder name, inside a section, that sends files to a person's own space.</summary>
    public static string PersonFolderName(Profile profile) => ViewStorageNames.FromDisplayName(profile.DisplayName);

    /// <summary>
    /// Whose library a file in the Import area belongs to, from where it sits (Photo and video filing spec D3). A file inside a folder
    /// named <c>Shared</c> goes to the Shared Library; inside a folder named like a person it goes to that person's own space; anywhere
    /// else it goes to the household's first server administrator's own space. A private space is the safe default: nothing a person
    /// copies in by hand becomes visible to the household unless they choose it.
    /// </summary>
    public async Task<IntakeDestination?> ResolveDestinationAsync(string path, CancellationToken ct = default)
    {
        var all = await profiles.GetAllAsync(ct).ConfigureAwait(false);
        var firstFolder = FirstFolderBelowSection(path);
        if (firstFolder is not null && string.Equals(firstFolder, SharedFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return IntakeDestination.SharedLibrary;
        }

        if (firstFolder is not null)
        {
            var named = all.FirstOrDefault(profile =>
                string.Equals(PersonFolderName(profile), firstFolder, StringComparison.OrdinalIgnoreCase)
                || string.Equals(profile.DisplayName, firstFolder, StringComparison.OrdinalIgnoreCase));
            if (named is not null)
            {
                return IntakeDestination.Person(named.Id);
            }
        }

        var owner = all.Where(profile => profile.IsServerAdministrator).OrderBy(profile => profile.CreatedAt).FirstOrDefault()
            ?? all.OrderBy(profile => profile.CreatedAt).FirstOrDefault();
        return owner is null ? null : IntakeDestination.Person(owner.Id);
    }

    /// <summary>
    /// The folder, inside the section, a file for this destination is placed in (<c>Photos/Shared</c>, <c>Photos/Mia</c>), or the
    /// section itself for the default owner.
    /// </summary>
    public async Task<string?> DestinationFolderAsync(string contentKind, IntakeDestination destination, CancellationToken ct = default)
    {
        var section = SectionPath(contentKind);
        if (section is null)
        {
            return null;
        }

        if (destination.ScopeKind == IntakeDestination.Shared)
        {
            return Path.Combine(section, SharedFolderName);
        }

        var all = await profiles.GetAllAsync(ct).ConfigureAwait(false);
        var profile = all.FirstOrDefault(candidate => candidate.Id == destination.OwnerProfileId);
        return profile is null ? null : Path.Combine(section, PersonFolderName(profile));
    }

    /// <summary>The section kind a path is in, or null when the path is not in a section.</summary>
    public string? KindOfPath(string path)
    {
        foreach (var kind in new[] { ViewContentKinds.Photos, ViewContentKinds.PersonalVideos })
        {
            if (SectionPath(kind) is { } section && IsInside(section, path))
            {
                return kind;
            }
        }

        return null;
    }

    public bool IsInRejectedArea(string path) =>
        ImportRoot() is { } root && IsInside(Path.Combine(root, ImportFolderRules.WorkingFolderName, RejectedFolderName), path);

    private string? FirstFolderBelowSection(string path)
    {
        foreach (var kind in new[] { ViewContentKinds.Photos, ViewContentKinds.PersonalVideos })
        {
            if (SectionPath(kind) is not { } section || !IsInside(section, path))
            {
                continue;
            }

            var segments = Path.GetRelativePath(section, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            // A file straight in the section has one segment (its own name); the folder is the first of several.
            return segments.Length >= 2 ? segments[0] : null;
        }

        return null;
    }

    internal static bool IsInside(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison)
            || string.Equals(normalizedPath, normalizedRoot, PathComparison);
    }
}

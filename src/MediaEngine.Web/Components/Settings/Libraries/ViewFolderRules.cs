using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Components.Settings;

/// <summary>One folder in a folder list (the "N folders" button): a library's folder or a View folder, kept neutral so one list part serves both (View folders spec D5).</summary>
/// <param name="Id">The folder's identity; only used to key the row.</param>
/// <param name="Path">The folder's path as the Engine sees it.</param>
/// <param name="Issue">Why it can't be reached (offline, no access); null when it is online or not yet checked.</param>
/// <param name="Name">The folder's friendly name, shown above its path when the library has two or more folders; null for none.</param>
/// <param name="Managed">True for a Managed folder, false for a Read-only one; null where the list does not say (View folders).</param>
public sealed record FolderListItem(string Id, string Path, string? Issue = null, string? Name = null, bool? Managed = null);

/// <summary>A View folder with who added it: a null owner is a Shared Library folder, otherwise it sits in that profile's personal space (its edits go through that profile).</summary>
public sealed record ViewFolderEntry(ViewSourceAdminDto Source, Guid? OwnerProfileId);

/// <summary>A folder the Add folder window has collected but not saved (the Edit window saves it with Save changes).</summary>
/// <param name="Name">The typed friendly name; empty means "use the folder's own name".</param>
/// <param name="Managed">True for a Managed folder (Library creates it in View's storage); false for a Read-only folder read where it is.</param>
/// <param name="Path">The chosen folder, for a Read-only folder.</param>
public sealed record ViewFolderPlan(string Kind, string Name, bool Managed, string? Path);

/// <summary>
/// The plain rules of View folders in the Libraries table and the Edit window (View folders spec D2, D4a, D4c): which folders belong to a kind, how a folder is
/// handled (Read-only or Managed), how a missing name is filled, and whether the kind can take uploads yet.
/// </summary>
public static class ViewFolderRules
{
    /// <summary>The Engine's storage mode of a folder Library owns (uploads and phone backups are filed into it).</summary>
    public const string ManagedMode = "managed";

    public const string LinkedMode = "linked";

    public static bool IsManaged(ViewSourceAdminDto source) => string.Equals(source.StorageMode, ManagedMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>The built-in intake source (browser uploads) is Library's own plumbing, not a folder a person added, so it is never listed or counted.</summary>
    public static bool IsIntake(ViewSourceAdminDto source) =>
        source.AcceptsPhotosAndVideos || string.Equals(source.SourceType, "browser_upload", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folders a person added to a View kind.</summary>
    public static IReadOnlyList<ViewSourceAdminDto> OfKind(IEnumerable<ViewSourceAdminDto> sources, string kind) =>
        [.. sources.Where(source => !IsIntake(source) && !string.IsNullOrWhiteSpace(source.Location)
            && string.Equals(source.ContentKind, kind, StringComparison.OrdinalIgnoreCase))];

    /// <summary>True when the kind has a Managed folder, so uploads can be filed somewhere (View folders spec D4c).</summary>
    public static bool HasManagedFolder(IEnumerable<ViewSourceAdminDto> sources, string kind) =>
        OfKind(sources, kind).Any(source => IsManaged(source) && source.Enabled);

    /// <summary>The path to show for a folder: where it lives, or (for a Managed folder with no outside path) its name.</summary>
    public static string PathOf(ViewSourceAdminDto source) => string.IsNullOrWhiteSpace(source.Location) ? source.Name : source.Location;

    /// <summary>
    /// The content kinds one View row holds (Setup and Libraries fixes D10): the "Photos and videos" row (the Photos kind) holds Photos and Personal videos folders
    /// together; every other row holds its own kind.
    /// </summary>
    public static IReadOnlyList<string> ContentKindsOf(string rowKey) =>
        string.Equals(rowKey, LibraryTypeCatalog.PhotosKey, StringComparison.OrdinalIgnoreCase)
            ? [LibraryTypeCatalog.PhotosKey, LibraryTypeCatalog.PersonalVideosKey]
            : [rowKey];

    /// <summary>The folders a person added to one View row: every content kind the row holds (see <see cref="ContentKindsOf"/>).</summary>
    public static IReadOnlyList<ViewSourceAdminDto> OfRow(IEnumerable<ViewSourceAdminDto> sources, string rowKey) =>
        [.. ContentKindsOf(rowKey).SelectMany(kind => OfKind(sources, kind))];

    /// <summary>The folder-list items of a View row's folders.</summary>
    public static IReadOnlyList<FolderListItem> ListItems(IEnumerable<ViewSourceAdminDto> sources, string rowKey) =>
        [.. OfRow(sources, rowKey).Select(source => new FolderListItem(source.Id.ToString("D"), PathOf(source), source.FolderOffline ? "Offline" : null))];

    /// <summary>
    /// The friendly name to save: the typed one, else the folder's own name; a name already used by another folder of the same kind gets " (2)", " (3)" and so on.
    /// The result never passes <see cref="FolderHandlingWords.MaxFolderNameLength"/>.
    /// </summary>
    public static string NameFor(string? typed, string? path, IEnumerable<string> taken)
    {
        var wanted = (string.IsNullOrWhiteSpace(typed) ? FolderHandlingWords.NameFromPath(path) : typed).Trim();
        if (wanted.Length == 0)
        {
            wanted = "Folder";
        }

        var used = new HashSet<string>(taken.Select(name => name.Trim()), StringComparer.OrdinalIgnoreCase);
        var candidate = Fit(wanted, string.Empty);
        for (var number = 2; used.Contains(candidate); number++)
        {
            candidate = Fit(wanted, $" ({number})");
        }

        return candidate;
    }

    private static string Fit(string name, string suffix)
    {
        var room = FolderHandlingWords.MaxFolderNameLength - suffix.Length;
        return (name.Length > room ? name[..room].TrimEnd() : name) + suffix;
    }

    /// <summary>What to ask the Engine for a folder the person added: a Read-only one is linked where it is; a Managed one is created in View's storage under its name.</summary>
    public static CreateViewSourceRequest RequestFor(ViewFolderPlan plan, string name) => plan.Managed
        ? new CreateViewSourceRequest(name, ManagedMode, null, true, plan.Kind != ViewContentKinds.Documents, plan.Kind)
        : new CreateViewSourceRequest(name, LinkedMode, plan.Path, true, plan.Kind != ViewContentKinds.Documents, plan.Kind);

    /// <summary>
    /// Every folder a kind holds that the Engine can list: the Shared Library's, and (when <paramref name="includeProfiles"/>) each profile's own space, so the count,
    /// the folder list and Edit all read the same set. A folder reached through several profiles is listed once. Null when the Engine could not be read (a count is
    /// then unknown, never a guessed zero).
    /// </summary>
    public static async Task<IReadOnlyList<ViewFolderEntry>?> LoadAsync(IEngineApiClient api, bool includeProfiles, CancellationToken ct = default)
    {
        var shared = await api.GetViewSharedSourcesAsync(ct);
        // Settings still reads each profile's space when the Shared Library's list is not given; setup has nothing else to read.
        if (shared is null && !includeProfiles)
        {
            return null;
        }

        var entries = (shared ?? []).Select(source => new ViewFolderEntry(source, null)).ToList();
        if (!includeProfiles)
        {
            return entries;
        }

        var profiles = await api.GetProfilesAsync(ct);
        // The client answers an empty list on failure, so an empty list only counts as "no profiles" when no failure was recorded.
        if (profiles.Count == 0 && !string.IsNullOrWhiteSpace(api.LastError))
        {
            return null;
        }

        var reviews = await Task.WhenAll(profiles.Select(profile => api.GetViewProfileSourcesAsync(profile.Id, ct)));
        if (reviews.Any(review => review is null))
        {
            return null;
        }

        for (var index = 0; index < profiles.Count; index++)
        {
            foreach (var source in reviews[index]!.Sources.Where(source => !string.IsNullOrWhiteSpace(source.Location)))
            {
                if (!entries.Any(entry => entry.Source.Id == source.Id || string.Equals(entry.Source.Location, source.Location, StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(new ViewFolderEntry(source, profiles[index].Id));
                }
            }
        }

        return entries;
    }

    /// <summary>The noun for a kind inside a sentence ("photos and videos", "personal videos", "documents").</summary>
    public static string Noun(string kindKey, string pluralLabel) =>
        kindKey == ViewContentKinds.Photos ? "photos and videos" : pluralLabel.Length == 0 ? pluralLabel : char.ToLowerInvariant(pluralLabel[0]) + pluralLabel[1..];

    /// <summary>"You can't upload photos and videos until you add a Managed folder."</summary>
    public static string UploadsNeedManagedLine(IStringLocalizer<SharedStrings> strings, string noun) => strings["ViewFolderRules_UploadsNeedManagedLine", noun].Value;
}

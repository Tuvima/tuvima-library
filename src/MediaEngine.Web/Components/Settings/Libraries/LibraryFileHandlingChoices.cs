using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The File handling tab's choices and rules (Library pages D6), kept out of the Razor file because Razor components never import the
/// configuration namespace. The values are the ones the libraries file already stores.
/// </summary>
internal static class LibraryFileHandlingChoices
{
    public const string PreserveNames = LibraryOrganizationModes.KeepOriginalNames;
    public const string LibraryStandard = LibraryOrganizationModes.TuvimaStandard;
    public const string Custom = LibraryOrganizationModes.Custom;

    public const string KeepOne = LibraryDuplicatePolicies.KeepOne;
    public const string KeepBoth = LibraryDuplicatePolicies.KeepBoth;

    /// <summary>The duplicate "value" that means the library follows the server-wide default (stored as no value).</summary>
    public const string ServerDefault = "server_default";

    public const string ManagedMode = LibrarySourceManagementModes.ManagedByTuvima;
    public const string AsIsMode = LibrarySourceManagementModes.ExistingLibrary;
    public const string WritableAccess = LibrarySourceAccessModes.Writable;
    public const string ReadOnlyAccess = LibrarySourceAccessModes.ReadOnly;
    public const string NotConverting = LibrarySourceConversionStates.None;

    public const string VaultAccess = LibraryAccessLevels.Vault;

    /// <summary>The folders of a library that Library may be allowed to change: every folder that is not a finished merge source.</summary>
    public static IReadOnlyList<LibrarySourceDto> ChangeableFolders(LibraryFolderDto library) =>
        [.. library.Sources.Where(source => source.MergeState != LibrarySourceMergeStates.Merged)];

    public static bool IsManaged(LibrarySourceDto source) => source.ManagementMode == ManagedMode;

    /// <summary>
    /// Whether the library's files may be renamed, moved or tagged at all: not for a Vault library (its files follow the Vault's own
    /// rules) and not for a kind that has no naming pattern (the View kinds).
    /// </summary>
    public static bool CanChangeFiles(LibraryFolderDto library, LibraryTypeOption? kind)
    {
        if (string.Equals(library.Access, VaultAccess, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (kind is null)
        {
            return library.MediaTypes.Count > 0;
        }

        return !string.IsNullOrWhiteSpace(kind.NamingTemplate) && kind.MediaTypes.Length > 0;
    }

    /// <summary>The naming tokens a custom pattern may use for a kind's naming template.</summary>
    public static string TokensFor(string? templateKey) => templateKey?.ToLowerInvariant() switch
    {
        "movies" => "{Title} {Year} {Ext}",
        "tv" => "{Series} {Year} {Season} {Episode} {EpisodeCode} {EpisodeTitle} {Ext}",
        "music" => "{AlbumArtist} {Album} {Year} {TrackNumber} {Title} {Ext}",
        "books" => "{Author} {Title} {Year} {Ext}",
        "audiobooks" => "{Author} {BookTitle} {Year} {TrackNumber} {TrackTitle} {Ext}",
        "comics" => "{Series} {IssueNumber} {Year} {Ext}",
        _ => "{Title} {Year} {Ext}",
    };

    /// <summary>A sample path for a kind, used when the Engine's own example cannot be read.</summary>
    public static string FallbackExample(string? templateKey) => templateKey?.ToLowerInvariant() switch
    {
        "movies" => "Dune (2021)/Dune (2021).mkv",
        "tv" => "Severance (2022)/Season 01/Severance - s01e01 - Good News About Hell.mkv",
        "music" => "Radiohead/OK Computer (1997)/01 - Airbag.flac",
        "books" => "Frank Herbert/Dune (1965)/Dune (1965).epub",
        "audiobooks" => "Frank Herbert/Dune (1965)/01 Chapter 1.m4b",
        "comics" => "Saga/Saga - 001 (2012).cbz",
        "videos" => "Python Course/01 - Introduction.mp4",
        _ => "Library/Organised Item.ext",
    };

    /// <summary>The saved duplicate choice as the tab shows it: <see cref="KeepOne"/>, <see cref="KeepBoth"/> or <see cref="ServerDefault"/>.</summary>
    public static string DuplicateChoice(LibraryFolderDto library) =>
        LibraryDuplicatePolicies.IsValid(library.FileHandling.DuplicatePolicy) ? library.FileHandling.DuplicatePolicy! : ServerDefault;

    /// <summary>The server-wide duplicate rule, or Keep one copy when it holds nothing valid.</summary>
    public static string ServerDuplicate(string? serverPolicy) => LibraryDuplicatePolicies.IsValid(serverPolicy) ? serverPolicy! : KeepOne;

    /// <summary>The words for a duplicate rule.</summary>
    public static string DuplicateText(string policy) => policy == KeepBoth ? "Keep both" : "Keep one copy";
}

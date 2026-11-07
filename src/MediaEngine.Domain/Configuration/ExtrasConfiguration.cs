using System.Text.Json.Serialization;
using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The rules for supporting files (Extras), loaded from <c>config/extras.json</c> (universal editor v5 D16, D34 and 3b): which files
/// Library can open, which are never shown, which folder and file-name words hint a role, the fixed role list per kind of file, and how
/// long a missing file's record is kept. The shipped file is also built into the Engine, so a config folder without it still works.
/// A new list or word is a change to the file, not to code.
/// </summary>
public sealed class ExtrasConfiguration
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1";

    [JsonPropertyName("accepted_types")]
    public ExtrasAcceptedTypes AcceptedTypes { get; set; } = new();

    [JsonPropertyName("hidden_types")]
    public ExtrasHiddenTypes HiddenTypes { get; set; } = new();

    /// <summary>Folders other tools keep beside a title for their own use; never searched.</summary>
    [JsonPropertyName("ignored_folders")]
    public List<string> IgnoredFolders { get; set; } = [];

    [JsonPropertyName("transient_files")]
    public ExtrasTransientFiles TransientFiles { get; set; } = new();

    /// <summary>Folder names that hint the files inside are supporting files. They never change who a file belongs to.</summary>
    [JsonPropertyName("hint_folders")]
    public List<string> HintFolders { get; set; } = [];

    /// <summary>Folders whose named sub-folders become groups.</summary>
    [JsonPropertyName("group_parent_folders")]
    public List<string> GroupParentFolders { get; set; } = [];

    /// <summary>Folder name to the role the files inside are guessed to have.</summary>
    [JsonPropertyName("folder_roles")]
    public Dictionary<string, string> FolderRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File-name words per role, in the order they are tried.</summary>
    [JsonPropertyName("role_words")]
    public List<ExtrasRoleWords> RoleWords { get; set; } = [];

    /// <summary>The fixed role list per kind of file ("document", "clip", "picture").</summary>
    [JsonPropertyName("roles")]
    public Dictionary<string, List<ExtrasRoleEntry>> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("default_roles")]
    public Dictionary<string, string> DefaultRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How a title with many supporting files and no groups is grouped by default, per kind of file.</summary>
    [JsonPropertyName("role_families")]
    public Dictionary<string, ExtrasRoleFamily> RoleFamilies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("document_hint_words")]
    public List<string> DocumentHintWords { get; set; } = [];

    [JsonPropertyName("clip_suffix_words")]
    public List<string> ClipSuffixWords { get; set; } = [];

    [JsonPropertyName("season_words")]
    public List<string> SeasonWords { get; set; } = [];

    [JsonPropertyName("specials_words")]
    public List<string> SpecialsWords { get; set; } = [];

    [JsonPropertyName("episode_words")]
    public List<string> EpisodeWords { get; set; } = [];

    [JsonPropertyName("title_separators")]
    public List<string> TitleSeparators { get; set; } = [];

    /// <summary>How many days the record of a file that left the disk is kept, shown as "File missing" (D20).</summary>
    [JsonPropertyName("missing_file_keep_days")]
    public int MissingFileKeepDays { get; set; } = 30;

    /// <summary>The roles a person may pick for a file of this kind (an <see cref="ItemExtraKinds"/> value), in picker order.</summary>
    public IReadOnlyList<ExtrasRoleEntry> RolesFor(string fileKind) =>
        Roles.TryGetValue(KindKey(fileKind), out var roles) ? roles : [];

    /// <summary>True when <paramref name="role"/> is in the role list of this kind of file.</summary>
    public bool RoleFits(string fileKind, string? role) =>
        !string.IsNullOrWhiteSpace(role) && RolesFor(fileKind).Any(entry => entry.Id.Equals(role, StringComparison.Ordinal));

    /// <summary>The role a file of this kind has when no name says otherwise.</summary>
    public string DefaultRoleFor(string fileKind) =>
        DefaultRoles.TryGetValue(KindKey(fileKind), out var role) && !string.IsNullOrWhiteSpace(role) ? role : ExtraRoles.DefaultFor(fileKind);

    /// <summary>The family a file of this kind falls into for the default grouping ("Documents", "Videos", "Pictures").</summary>
    public ExtrasRoleFamily FamilyFor(string fileKind) =>
        RoleFamilies.TryGetValue(KindKey(fileKind), out var family) ? family : new ExtrasRoleFamily { Id = KindKey(fileKind), Label = KindKey(fileKind) };

    /// <summary>The words shown for a role of this kind of file.</summary>
    public string LabelFor(string fileKind, string? role) =>
        RolesFor(fileKind).FirstOrDefault(entry => entry.Id.Equals(role, StringComparison.Ordinal))?.Label ?? ExtraRoles.Label(role);

    /// <summary>The config key for a kind of file: documents are "document", pictures "picture", everything that plays "clip".</summary>
    public static string KindKey(string fileKind) => fileKind switch
    {
        ItemExtraKinds.Document => "document",
        ItemExtraKinds.Picture => "picture",
        _ => "clip",
    };
}

public sealed class ExtrasAcceptedTypes
{
    [JsonPropertyName("documents")]
    public List<string> Documents { get; set; } = [];

    [JsonPropertyName("pictures")]
    public List<string> Pictures { get; set; } = [];

    [JsonPropertyName("videos")]
    public List<string> Videos { get; set; } = [];

    [JsonPropertyName("audio")]
    public List<string> Audio { get; set; } = [];
}

public sealed class ExtrasHiddenTypes
{
    /// <summary>Archives, metadata sidecars and subtitle files, which never become supporting files.</summary>
    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = [];

    /// <summary>Case-insensitive regular expression for picture names that are artwork for a media file (poster, fanart, thumb...).</summary>
    [JsonPropertyName("picture_sidecar_name_pattern")]
    public string PictureSidecarNamePattern { get; set; } = string.Empty;
}

public sealed class ExtrasTransientFiles
{
    [JsonPropertyName("name_prefixes")]
    public List<string> NamePrefixes { get; set; } = [];

    [JsonPropertyName("name_suffixes")]
    public List<string> NameSuffixes { get; set; } = [];

    [JsonPropertyName("names")]
    public List<string> Names { get; set; } = [];
}

public sealed class ExtrasRoleWords
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("words")]
    public List<string> Words { get; set; } = [];
}

public sealed class ExtrasRoleEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

public sealed class ExtrasRoleFamily
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

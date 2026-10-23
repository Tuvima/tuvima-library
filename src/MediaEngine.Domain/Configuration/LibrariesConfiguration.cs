using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>Root model for the clean, pre-beta <c>libraries.json</c> contract.</summary>
public sealed class LibrariesConfiguration
{
    /// <summary>
    /// The only supported file version. 8.0 moved "Suitable for limited profiles" from the library to each folder and removed
    /// the unused sharing fields (7.0 had replaced the per-folder role, intake and write-back fields with two folder states,
    /// one destination per library, and global duplicate handling). Older files are refused.
    /// </summary>
    public const string CurrentSchemaVersion = "8.0";

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("storage_locations")]
    public List<ServerStorageLocationConfig> StorageLocations { get; set; } = [];

    [JsonPropertyName("view_storage")]
    public ViewStorageConfig ViewStorage { get; set; } = new();

    [JsonPropertyName("libraries")]
    public List<LibraryFolderConfig> Libraries { get; set; } = [];

    [JsonPropertyName("personal_library_policy")]
    public PersonalLibraryPolicyConfig PersonalLibraryPolicy { get; set; } = new();

    [JsonPropertyName("file_handling")]
    public FileHandlingConfig FileHandling { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>Server-wide rules for files that arrive in a library. They apply to every library the same way.</summary>
public sealed class FileHandlingConfig
{
    /// <summary>What to do with an exact copy of a file already held: <c>keep_one</c> (default) or <c>keep_both</c>.</summary>
    [JsonPropertyName("duplicate_policy")]
    public string DuplicatePolicy { get; set; } = LibraryDuplicatePolicies.KeepOne;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>
/// The one managed filesystem root for every profile-owned View Personal Space.
/// Profile and source directories are derived from stable IDs beneath this root.
/// </summary>
public sealed class ViewStorageConfig
{
    [JsonPropertyName("storage_location_id")]
    public string StorageLocationId { get; set; } = "media";

    [JsonPropertyName("relative_root")]
    public string RelativeRoot { get; set; } = "View";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>
/// An administrator-approved server/container root that may be exposed by the
/// folder browser. Paths outside these roots are never browsable through the UI.
/// </summary>
public sealed class ServerStorageLocationConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("allow_write")]
    public bool AllowWrite { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>Administrator-controlled capabilities for personal libraries in View.</summary>
public sealed class PersonalLibraryPolicyConfig
{
    [JsonPropertyName("allow_mobile_backup")]
    public bool AllowMobileBackup { get; set; } = true;

    [JsonPropertyName("allow_browser_upload")]
    public bool AllowBrowserUpload { get; set; } = true;

    [JsonPropertyName("allow_drag_and_drop")]
    public bool AllowDragAndDrop { get; set; } = true;

    [JsonPropertyName("allow_connected_device_import")]
    public bool AllowConnectedDeviceImport { get; set; } = true;

    [JsonPropertyName("allow_managed_storage")]
    public bool AllowManagedStorage { get; set; } = true;

    [JsonPropertyName("allow_existing_folder_attachment")]
    public bool AllowExistingFolderAttachment { get; set; } = true;

    /// <summary>Most characters of embedded text stored per document; the rest is truncated.</summary>
    [JsonPropertyName("document_text_max_characters")]
    public int DocumentTextMaxCharacters { get; set; } = 1_000_000;

    /// <summary>Most PDF pages whose text layer is read.</summary>
    [JsonPropertyName("document_text_max_pdf_pages")]
    public int DocumentTextMaxPdfPages { get; set; } = 2000;

    /// <summary>Longest time one document may take to read before it is abandoned.</summary>
    [JsonPropertyName("document_text_read_timeout_seconds")]
    public int DocumentTextReadTimeoutSeconds { get; set; } = 60;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>A logical catalogued library and its independently governed sources.</summary>
public sealed class LibraryFolderConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Structured subtype such as Books, Movies, or Music. Empty for personal libraries.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = LibraryKinds.Catalogued;

    /// <summary>The library kind (<c>library_kinds.json</c>) this library holds, for example <c>movies</c> or <c>other_videos</c>. Required for catalogued libraries.</summary>
    [JsonPropertyName("kind_key")]
    public string KindKey { get; set; } = string.Empty;

    [JsonPropertyName("area")]
    public string Area { get; set; } = LibraryAreas.Read;

    [JsonPropertyName("presentation")]
    public string Presentation { get; set; } = LibraryPresentations.Catalogue;

    [JsonPropertyName("metadata_policy")]
    public string MetadataPolicy { get; set; } = LibraryMetadataPolicies.Enriched;

    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>Videos libraries only: ask YouTube about videos named by a YouTube ID (sends only the ID). The library's kind decides it: the YouTube kind asks, a plain kind (Other videos) never does and must say false here, and a library with no kind follows this field.</summary>
    [JsonPropertyName("youtube_lookup")]
    public bool YoutubeLookup { get; set; } = true;

    /// <summary>When on, deleting a title from disk moves its files to the recycle bin; they leave the disk only when the bin is emptied. On for Videos by default, off for the other types.</summary>
    [JsonPropertyName("recycle_bin")]
    public bool RecycleBin { get; set; }

    [JsonPropertyName("sources")]
    public List<LibrarySourceConfig> Sources { get; set; } = [];

    /// <summary>
    /// The one folder new media is filed into: a stable source ID (never an array position) of a managed folder,
    /// or <see langword="null"/> when the library has no managed folder. Normalised on save by
    /// <see cref="LibrariesConfigurationNormalizer"/>.
    /// </summary>
    [JsonPropertyName("primary_destination_source_id")]
    public string? PrimaryDestinationSourceId { get; set; }

    [JsonPropertyName("accepted_intake_modes")]
    public List<string> AcceptedIntakeModes { get; set; } = [];

    [JsonPropertyName("organization_policy")]
    public LibraryOrganizationPolicyConfig OrganizationPolicy { get; set; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

    [JsonIgnore]
    public IEnumerable<LibrarySourceConfig> ScannableSources =>
        Sources.Where(source => !string.IsNullOrWhiteSpace(source.Path) && !source.IsMergedAway);

    /// <summary>The destination folder. An as-is folder is never returned, even if the id points at one.</summary>
    [JsonIgnore]
    public LibrarySourceConfig? PrimaryDestination =>
        string.IsNullOrWhiteSpace(PrimaryDestinationSourceId)
            ? null
            : Sources.FirstOrDefault(source =>
                source.IsManaged
                && string.Equals(source.Id, PrimaryDestinationSourceId, StringComparison.OrdinalIgnoreCase));
}

public sealed class LibrarySourceConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// <c>managed_by_tuvima</c>: Tuvima moves, renames and tags files here, including ones already there.
    /// <c>existing_library</c> (as-is): read-only, never touched and never a destination.
    /// </summary>
    [JsonPropertyName("management_mode")]
    public string ManagementMode { get; set; } = LibrarySourceManagementModes.ExistingLibrary;

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = LibrarySourceTypes.LocalFolder;

    [JsonPropertyName("include_subdirectories")]
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>
    /// Unrated items from this folder are shown to profiles with a rating limit, whatever those profiles' "allow unrated" says.
    /// Rated items still follow each profile's limit. Off by default; never offered for a Vault folder.
    /// </summary>
    [JsonPropertyName("ok_for_limited_profiles")]
    public bool OkForLimitedProfiles { get; set; }

    /// <summary>Derived from <see cref="ManagementMode"/> whenever the file is saved: managed is writable, as-is is read-only.</summary>
    [JsonPropertyName("access_mode")]
    public string AccessMode { get; set; } = LibrarySourceAccessModes.ReadOnly;

    /// <summary>Progress of switching this folder from as-is to managed: <c>none</c>, <c>converting</c> or <c>converted</c>.</summary>
    [JsonPropertyName("conversion_state")]
    public string ConversionState { get; set; } = LibrarySourceConversionStates.None;

    /// <summary>The media operation that is converting this folder, set only while <see cref="ConversionState"/> is <c>converting</c>.</summary>
    [JsonPropertyName("conversion_operation_id")]
    public string? ConversionOperationId { get; set; }

    /// <summary>
    /// Progress of merging this folder into another managed folder: <c>none</c>, <c>merging</c> or <c>merged</c>. Like the
    /// conversion state it belongs to the Engine, which keeps it when the Dashboard saves the folder settings.
    /// </summary>
    [JsonPropertyName("merge_state")]
    public string MergeState { get; set; } = LibrarySourceMergeStates.None;

    /// <summary>The media operation that is merging this folder, set only while <see cref="MergeState"/> is <c>merging</c>.</summary>
    [JsonPropertyName("merge_operation_id")]
    public string? MergeOperationId { get; set; }

    /// <summary>The managed folder this folder is being (or was) merged into.</summary>
    [JsonPropertyName("merge_target_source_id")]
    public string? MergeTargetSourceId { get; set; }

    /// <summary><c>copy</c> keeps every original file; <c>move</c> removes each one once its copy has been checked.</summary>
    [JsonPropertyName("merge_mode")]
    public string? MergeMode { get; set; }

    /// <summary>When the merge finished, set only while <see cref="MergeState"/> is <c>merged</c>.</summary>
    [JsonPropertyName("merged_at")]
    public DateTimeOffset? MergedAt { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("profile_id")]
    public string? ProfileId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

    /// <summary>True once this folder has been merged into another one: its files are catalogued as the target's copies and it is no longer scanned.</summary>
    [JsonIgnore]
    public bool IsMergedAway => MergeState == LibrarySourceMergeStates.Merged;

    [JsonIgnore]
    public bool IsWritable => AccessMode == LibrarySourceAccessModes.Writable;

    [JsonIgnore]
    public bool IsManaged => ManagementMode == LibrarySourceManagementModes.ManagedByTuvima;

    [JsonIgnore]
    public bool AllowsFileMutation => IsManaged && IsWritable;
}

public sealed class LibraryOrganizationPolicyConfig
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = LibraryOrganizationModes.TuvimaStandard;

    [JsonPropertyName("custom_template")]
    public string? CustomTemplate { get; set; }

    /// <summary>
    /// Write tags into this type's media files. Applies only in managed folders, only to formats that can carry tags,
    /// and only to fields allowed by <c>writeback-fields.json</c>. Switch off to leave files' own tags alone.
    /// </summary>
    [JsonPropertyName("write_tags")]
    public bool WriteTags { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

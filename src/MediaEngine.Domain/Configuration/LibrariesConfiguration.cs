using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>Root model for the clean, pre-beta <c>libraries.json</c> contract.</summary>
public sealed class LibrariesConfiguration
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "6.0";

    [JsonPropertyName("storage_locations")]
    public List<ServerStorageLocationConfig> StorageLocations { get; set; } = [];

    [JsonPropertyName("view_storage")]
    public ViewStorageConfig ViewStorage { get; set; } = new();

    [JsonPropertyName("libraries")]
    public List<LibraryFolderConfig> Libraries { get; set; } = [];

    [JsonPropertyName("personal_library_policy")]
    public PersonalLibraryPolicyConfig PersonalLibraryPolicy { get; set; } = new();

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

    [JsonPropertyName("default_visibility")]
    public string DefaultVisibility { get; set; } = LibraryVisibility.Private;

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

    [JsonPropertyName("area")]
    public string Area { get; set; } = LibraryAreas.Read;

    [JsonPropertyName("presentation")]
    public string Presentation { get; set; } = LibraryPresentations.Catalogue;

    [JsonPropertyName("metadata_policy")]
    public string MetadataPolicy { get; set; } = LibraryMetadataPolicies.Enriched;

    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    [JsonPropertyName("sources")]
    public List<LibrarySourceConfig> Sources { get; set; } = [];

    /// <summary>Stable source ID, never an array position.</summary>
    [JsonPropertyName("primary_destination_source_id")]
    public string? PrimaryDestinationSourceId { get; set; }

    [JsonPropertyName("owner_profile_id")]
    public string? OwnerProfileId { get; set; }

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = LibraryVisibility.Household;

    [JsonPropertyName("authorized_profile_ids")]
    public List<string> AuthorizedProfileIds { get; set; } = [];

    [JsonPropertyName("accepted_intake_modes")]
    public List<string> AcceptedIntakeModes { get; set; } = [];

    [JsonPropertyName("duplicate_policy")]
    public string DuplicatePolicy { get; set; } = LibraryDuplicatePolicies.SkipExact;

    [JsonPropertyName("organization_policy")]
    public LibraryOrganizationPolicyConfig OrganizationPolicy { get; set; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

    [JsonIgnore]
    public IEnumerable<LibrarySourceConfig> ScannableSources =>
        Sources.Where(source => !string.IsNullOrWhiteSpace(source.Path));

    [JsonIgnore]
    public LibrarySourceConfig? PrimaryDestination =>
        string.IsNullOrWhiteSpace(PrimaryDestinationSourceId)
            ? null
            : Sources.FirstOrDefault(source =>
                string.Equals(source.Id, PrimaryDestinationSourceId, StringComparison.OrdinalIgnoreCase));
}

public sealed class LibrarySourceConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = LibrarySourceRoles.Secondary;

    [JsonPropertyName("management_mode")]
    public string ManagementMode { get; set; } = LibrarySourceManagementModes.ExistingLibrary;

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = LibrarySourceTypes.LocalFolder;

    [JsonPropertyName("include_subdirectories")]
    public bool IncludeSubdirectories { get; set; } = true;

    [JsonPropertyName("access_mode")]
    public string AccessMode { get; set; } = LibrarySourceAccessModes.ReadOnly;

    [JsonPropertyName("writeback_override")]
    public bool? WritebackOverride { get; set; }

    [JsonPropertyName("participates_in_organization")]
    public bool ParticipatesInOrganization { get; set; }

    [JsonPropertyName("intake_role")]
    public string IntakeRole { get; set; } = LibrarySourceIntakeRoles.None;

    /// <summary>
    /// For an as-is (existing library) folder: new media may be added to it, for example from the import folder.
    /// Files already there are never renamed, moved or changed, and access stays read-only for them.
    /// </summary>
    [JsonPropertyName("accepts_new_media")]
    public bool AcceptsNewMedia { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("profile_id")]
    public string? ProfileId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

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

    [JsonPropertyName("preserve_originals")]
    public bool PreserveOriginals { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

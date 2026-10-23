using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

public sealed class AuthSettingsDto
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "Local";

    [JsonPropertyName("localhost_bypass")]
    public bool LocalhostBypass { get; init; }

    [JsonPropertyName("password_sign_in_enabled")] public bool PasswordSignInEnabled { get; init; }
    [JsonPropertyName("passkey_sign_in_enabled")] public bool PasskeySignInEnabled { get; init; }
    [JsonPropertyName("external_sign_in_enabled")] public bool ExternalSignInEnabled { get; init; }
    [JsonPropertyName("trusted_local_networks")] public List<string> TrustedLocalNetworks { get; init; } = [];
    [JsonPropertyName("invitation_lifetime_hours")] public int InvitationLifetimeHours { get; init; }
    [JsonPropertyName("session_lifetime_hours")] public int SessionLifetimeHours { get; init; }
    [JsonPropertyName("maximum_active_sessions")] public int MaximumActiveSessions { get; init; }
    [JsonPropertyName("public_address")] public string PublicAddress { get; init; } = string.Empty;
    [JsonPropertyName("canonical_origin_ready")] public bool CanonicalOriginReady { get; init; }
    [JsonPropertyName("passkey_ready")] public bool PasskeyReady { get; init; }
    [JsonPropertyName("recovery_delivery_ready")] public bool RecoveryDeliveryReady { get; init; }
    [JsonPropertyName("restart_required")] public bool RestartRequired { get; init; }

    [JsonPropertyName("external_providers")]
    public List<ExternalAuthProviderDto> ExternalProviders { get; init; } = [];

    [JsonPropertyName("password_reset")]
    public PasswordResetDeliveryDto PasswordReset { get; init; } = new();
}

public sealed class PasswordResetDeliveryDto
{
    [JsonPropertyName("mode")] public string Mode { get; init; } = "Disabled";
    [JsonPropertyName("smtp_host")] public string SmtpHost { get; init; } = string.Empty;
    [JsonPropertyName("smtp_port")] public int SmtpPort { get; init; }
    [JsonPropertyName("from_address")] public string FromAddress { get; init; } = string.Empty;
    [JsonPropertyName("configured")] public bool Configured { get; init; }
    [JsonPropertyName("ready")] public bool Ready { get; init; }
}

public sealed class ExternalAuthProviderDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("issuer")]
    public string Issuer { get; init; } = string.Empty;

    [JsonPropertyName("authority")]
    public string Authority { get; init; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; init; } = string.Empty;

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; init; } = [];

    [JsonPropertyName("use_pkce")] public bool UsePkce { get; init; } = true;
    [JsonPropertyName("authorization_endpoint")] public string AuthorizationEndpoint { get; init; } = string.Empty;
    [JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; init; } = string.Empty;
    [JsonPropertyName("user_information_endpoint")] public string UserInformationEndpoint { get; init; } = string.Empty;
    [JsonPropertyName("id_claim")] public string IdClaim { get; init; } = "id";
    [JsonPropertyName("name_claim")] public string NameClaim { get; init; } = "name";
    [JsonPropertyName("email_claim")] public string EmailClaim { get; init; } = "email";

    [JsonPropertyName("callback_path")]
    public string CallbackPath { get; init; } = string.Empty;

    [JsonPropertyName("configured")] public bool Configured { get; init; }
    [JsonPropertyName("ready")] public bool Ready { get; init; }
    [JsonPropertyName("restart_required")] public bool RestartRequired { get; init; }
}

public sealed class UpdateAuthSettingsRequest
{
    [JsonPropertyName("mode")] public string Mode { get; init; } = "Local";
    [JsonPropertyName("localhost_bypass")] public bool LocalhostBypass { get; init; }
    [JsonPropertyName("password_sign_in_enabled")] public bool PasswordSignInEnabled { get; init; }
    [JsonPropertyName("passkey_sign_in_enabled")] public bool PasskeySignInEnabled { get; init; }
    [JsonPropertyName("external_sign_in_enabled")] public bool ExternalSignInEnabled { get; init; }
    [JsonPropertyName("trusted_local_networks")] public List<string> TrustedLocalNetworks { get; init; } = [];
    [JsonPropertyName("invitation_lifetime_hours")] public int InvitationLifetimeHours { get; init; }
    [JsonPropertyName("session_lifetime_hours")] public int SessionLifetimeHours { get; init; }
    [JsonPropertyName("maximum_active_sessions")] public int MaximumActiveSessions { get; init; }
}

public sealed class UpdateExternalAuthProviderRequest
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "oidc";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("issuer")] public string Issuer { get; init; } = string.Empty;
    [JsonPropertyName("authority")] public string Authority { get; init; } = string.Empty;
    [JsonPropertyName("client_id")] public string ClientId { get; init; } = string.Empty;
    [JsonPropertyName("client_secret")] public string? ClientSecret { get; init; }
    [JsonPropertyName("clear_client_secret")] public bool ClearClientSecret { get; init; }
    [JsonPropertyName("scopes")] public List<string> Scopes { get; init; } = [];
    [JsonPropertyName("use_pkce")] public bool UsePkce { get; init; } = true;
    [JsonPropertyName("authorization_endpoint")] public string AuthorizationEndpoint { get; init; } = string.Empty;
    [JsonPropertyName("token_endpoint")] public string TokenEndpoint { get; init; } = string.Empty;
    [JsonPropertyName("user_information_endpoint")] public string UserInformationEndpoint { get; init; } = string.Empty;
    [JsonPropertyName("id_claim")] public string IdClaim { get; init; } = "id";
    [JsonPropertyName("name_claim")] public string NameClaim { get; init; } = "name";
    [JsonPropertyName("email_claim")] public string EmailClaim { get; init; } = "email";
}

public sealed record ServerGeneralSettingsDto(
    [property: JsonPropertyName("server_name")] string ServerName = "Library",
    [property: JsonPropertyName("language")] string Language = "en",
    [property: JsonPropertyName("display_language")] string DisplayLanguage = "en",
    [property: JsonPropertyName("metadata_language")] string MetadataLanguage = "en",
    [property: JsonPropertyName("additional_languages")] List<string>? AdditionalLanguages = null,
    [property: JsonPropertyName("accept_any_language")] bool AcceptAnyLanguage = true,
    [property: JsonPropertyName("country")] string Country = "US",
    [property: JsonPropertyName("date_format")] string DateFormat = "system",
    [property: JsonPropertyName("time_format")] string TimeFormat = "system");

public sealed class LibraryFolderDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "catalogued";

    /// <summary>The library kind (see <see cref="LibraryKindDto"/>) this library holds, for example <c>movies</c> or <c>other_videos</c>.</summary>
    [JsonPropertyName("kind_key")]
    public string KindKey { get; set; } = string.Empty;

    [JsonPropertyName("area")]
    public string Area { get; set; } = "read";

    [JsonPropertyName("presentation")]
    public string Presentation { get; set; } = "catalogue";

    [JsonPropertyName("metadata_policy")]
    public string MetadataPolicy { get; set; } = "enriched";

    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>Videos libraries only: ask YouTube about videos named by a YouTube ID. On by default.</summary>
    [JsonPropertyName("youtube_lookup")]
    public bool YoutubeLookup { get; set; } = true;

    /// <summary>When on, deleting a title from disk moves its files to the recycle bin; they leave the disk only when the bin is emptied. On for Videos by default, off for the other types.</summary>
    [JsonPropertyName("recycle_bin")]
    public bool RecycleBin { get; set; }

    [JsonPropertyName("sources")]
    public List<LibrarySourceDto> Sources { get; set; } = [];

    [JsonPropertyName("primary_destination_source_id")]
    public string? PrimaryDestinationSourceId { get; set; }

    [JsonPropertyName("accepted_intake_modes")]
    public List<string> AcceptedIntakeModes { get; set; } = [];

    [JsonPropertyName("organization_policy")]
    public LibraryOrganizationPolicyDto OrganizationPolicy { get; set; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Read-only: how many files in this library the Engine could not match (Library setup spec D34), kept-as-is files excluded. Filled
    /// on the list read for an administrator and ignored when the list is saved. Zero (and then left out of the JSON) when none, and for
    /// anyone who may not see it.
    /// </summary>
    [JsonPropertyName("unmatched_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int UnmatchedCount { get; set; }
}

public sealed class LibrarySourceDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("management_mode")]
    public string ManagementMode { get; set; } = "existing_library";

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = "local_folder";

    [JsonPropertyName("include_subdirectories")]
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>Unrated items from this folder are shown to profiles with a rating limit. Rated items still follow each profile's limit.</summary>
    [JsonPropertyName("ok_for_limited_profiles")]
    public bool OkForLimitedProfiles { get; set; }

    [JsonPropertyName("access_mode")]
    public string AccessMode { get; set; } = "read_only";

    [JsonPropertyName("conversion_state")]
    public string ConversionState { get; set; } = "none";

    [JsonPropertyName("conversion_operation_id")]
    public string? ConversionOperationId { get; set; }

    /// <summary>Progress of merging this folder into another managed folder: <c>none</c>, <c>merging</c> or <c>merged</c>.</summary>
    [JsonPropertyName("merge_state")]
    public string MergeState { get; set; } = "none";

    [JsonPropertyName("merge_operation_id")]
    public string? MergeOperationId { get; set; }

    /// <summary>The managed folder this folder is being (or was) merged into.</summary>
    [JsonPropertyName("merge_target_source_id")]
    public string? MergeTargetSourceId { get; set; }

    /// <summary><c>copy</c> or <c>move</c>.</summary>
    [JsonPropertyName("merge_mode")]
    public string? MergeMode { get; set; }

    /// <summary>When the merge finished; set only once the folder is <c>merged</c>.</summary>
    [JsonPropertyName("merged_at")]
    public DateTimeOffset? MergedAt { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("profile_id")]
    public string? ProfileId { get; set; }

    /// <summary>
    /// Read-only: the first check of this folder while it is checking or paused (Library setup spec D35), with its tried and Unmatched counts
    /// and the folder's total. Filled on the list read for an administrator and ignored when the list is saved; left out when there is none,
    /// for a Vault folder, and for anyone who may not see it.
    /// </summary>
    [JsonPropertyName("first_check")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaEngine.Contracts.Library.FolderFirstCheckDto? FirstCheck { get; set; }
}

public sealed class LibraryOrganizationPolicyDto
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "tuvima_standard";

    [JsonPropertyName("custom_template")]
    public string? CustomTemplate { get; set; }

    [JsonPropertyName("write_tags")]
    public bool WriteTags { get; set; } = true;
}

public sealed class FileHandlingDto
{
    [JsonPropertyName("duplicate_policy")]
    public string DuplicatePolicy { get; set; } = "keep_one";
}

public sealed class UpdateLibrariesRequest
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = "8.0";

    [JsonPropertyName("storage_locations")]
    public List<ServerStorageLocationDto> StorageLocations { get; init; } = [];

    [JsonPropertyName("view_storage")]
    public ViewStorageDto ViewStorage { get; init; } = new();

    [JsonPropertyName("libraries")]
    public List<LibraryFolderDto> Libraries { get; init; } = [];

    [JsonPropertyName("personal_library_policy")]
    public PersonalLibraryPolicyDto PersonalLibraryPolicy { get; init; } = new();

    [JsonPropertyName("file_handling")]
    public FileHandlingDto FileHandling { get; init; } = new();
}

public sealed class LibrariesConfigurationDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = "8.0";

    [JsonPropertyName("storage_locations")]
    public List<ServerStorageLocationDto> StorageLocations { get; init; } = [];

    [JsonPropertyName("view_storage")]
    public ViewStorageDto ViewStorage { get; init; } = new();

    [JsonPropertyName("libraries")]
    public List<LibraryFolderDto> Libraries { get; init; } = [];

    [JsonPropertyName("personal_library_policy")]
    public PersonalLibraryPolicyDto PersonalLibraryPolicy { get; init; } = new();

    [JsonPropertyName("file_handling")]
    public FileHandlingDto FileHandling { get; init; } = new();

    /// <summary>Read-only: whether the optional import folder is set and every media type has a destination for it.</summary>
    [JsonPropertyName("import_folder_status")]
    public ImportFolderStatusDto ImportFolderStatus { get; set; } = new();
}

public sealed class ImportFolderStatusDto
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>True when an import folder is set and every media type has a destination. Import does not run otherwise.</summary>
    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    /// <summary>Plain names of the media types that still need a destination.</summary>
    [JsonPropertyName("missing_types")]
    public List<string> MissingTypes { get; set; } = [];

    /// <summary>One entry per media type: the folder its imported files go to, or none yet.</summary>
    [JsonPropertyName("destinations")]
    public List<ImportFolderDestinationDto> Destinations { get; set; } = [];
}

public sealed class ImportFolderDestinationDto
{
    /// <summary>Plain name of the media type, for example "TV Shows".</summary>
    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("library_id")]
    public string? LibraryId { get; set; }

    /// <summary>The managed folder imported files of this type are filed into; null when the type has none.</summary>
    [JsonPropertyName("destination_source_id")]
    public string? DestinationSourceId { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public sealed class ViewStorageDto
{
    [JsonPropertyName("storage_location_id")]
    public string StorageLocationId { get; set; } = "media";

    [JsonPropertyName("relative_root")]
    public string RelativeRoot { get; set; } = "View";
}

public sealed class PersonalLibraryPolicyDto
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
}

public sealed class TestPathRequest
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}

public sealed class PathTestResultDto
{
    public PathTestResultDto()
    {
    }

    public PathTestResultDto(string path, bool exists, bool hasRead, bool hasWrite)
    {
        Path = path;
        Exists = exists;
        HasRead = hasRead;
        HasWrite = hasWrite;
    }

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("exists")]
    public bool Exists { get; init; }

    [JsonPropertyName("has_read")]
    public bool HasRead { get; init; }

    [JsonPropertyName("has_write")]
    public bool HasWrite { get; init; }
}

public sealed class ServerStorageLocationDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("allow_write")]
    public bool AllowWrite { get; set; }

    [JsonPropertyName("available_bytes")]
    public long? AvailableBytes { get; init; }

    [JsonPropertyName("file_system")]
    public string? FileSystem { get; init; }
}

public sealed class BrowseServerFoldersRequest
{
    [JsonPropertyName("storage_location_id")]
    public string StorageLocationId { get; init; } = string.Empty;

    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; init; }

    [JsonPropertyName("search")]
    public string? Search { get; init; }
}

public sealed class ServerFolderEntryDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("relative_path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("modified_at")]
    public DateTimeOffset? ModifiedAt { get; init; }
}

public sealed class BrowseServerFoldersResultDto
{
    [JsonPropertyName("storage_location")]
    public ServerStorageLocationDto StorageLocation { get; init; } = new();

    [JsonPropertyName("relative_path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("parent_relative_path")]
    public string? ParentRelativePath { get; init; }

    [JsonPropertyName("display_path")]
    public string DisplayPath { get; init; } = string.Empty;

    [JsonPropertyName("directories")]
    public List<ServerFolderEntryDto> Directories { get; init; } = [];
}

public sealed class ValidateServerFolderRequest
{
    [JsonPropertyName("storage_location_id")]
    public string StorageLocationId { get; init; } = string.Empty;

    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; init; }

    [JsonPropertyName("manual_path")]
    public string? ManualPath { get; init; }

    [JsonPropertyName("selection_mode")]
    public string SelectionMode { get; init; } = ServerFolderSelectionModes.ExistingLibrary;

    [JsonPropertyName("current_source_id")]
    public string? CurrentSourceId { get; init; }
}

public static class ServerFolderSelectionModes
{
    public const string ManagedLibrary = "managed_library";
    public const string ExistingLibrary = "existing_library";
    public const string PersonalSpaceManaged = "personal_space_managed";
    public const string PersonalSpaceExisting = "personal_space_existing";

    /// <summary>Choosing the folder that holds an outside tool such as FFmpeg: read-only, and not tied to any library.</summary>
    public const string ToolLocation = "tool_location";

    public static bool RequiresWrite(string? value) => value is
        ManagedLibrary or PersonalSpaceManaged;

    public static bool IsValid(string? value) => value is
        ManagedLibrary or ExistingLibrary or PersonalSpaceManaged or PersonalSpaceExisting or ToolLocation;
}

public sealed class ServerFolderValidationIssueDto
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("library_name")]
    public string? LibraryName { get; init; }

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "error";
}

public sealed class ServerFolderValidationResultDto
{
    [JsonPropertyName("storage_location_id")]
    public string StorageLocationId { get; init; } = string.Empty;

    [JsonPropertyName("relative_path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("exists")]
    public bool Exists { get; init; }

    [JsonPropertyName("has_read")]
    public bool HasRead { get; init; }

    [JsonPropertyName("has_write")]
    public bool HasWrite { get; init; }

    /// <summary>True when the folder (or a folder beneath it) already holds files.</summary>
    [JsonPropertyName("contains_files")]
    public bool ContainsFiles { get; init; }

    /// <summary>True when the folder looks like phone or camera footage, so adding it as Videos can suggest View instead.</summary>
    [JsonPropertyName("looks_like_phone_footage")]
    public bool LooksLikePhoneFootage { get; init; }

    [JsonPropertyName("available_bytes")]
    public long? AvailableBytes { get; init; }

    [JsonPropertyName("file_system")]
    public string? FileSystem { get; init; }

    [JsonPropertyName("can_select")]
    public bool CanSelect { get; init; }

    [JsonPropertyName("issues")]
    public List<ServerFolderValidationIssueDto> Issues { get; init; } = [];
}

public sealed class OrganizationTemplateDto
{
    public OrganizationTemplateDto()
    {
    }

    public OrganizationTemplateDto(
        string template,
        string? preview,
        Dictionary<string, string>? templates = null)
    {
        Template = template;
        Preview = preview;
        Templates = templates;
    }

    [JsonPropertyName("template")]
    public string Template { get; init; } = string.Empty;

    [JsonPropertyName("preview")]
    public string? Preview { get; init; }

    [JsonPropertyName("templates")]
    public Dictionary<string, string>? Templates { get; init; }
}

public sealed class UpdateOrganizationTemplateRequest
{
    [JsonPropertyName("template")]
    public string Template { get; init; } = string.Empty;

    [JsonPropertyName("templates")]
    public Dictionary<string, string>? Templates { get; init; }
}

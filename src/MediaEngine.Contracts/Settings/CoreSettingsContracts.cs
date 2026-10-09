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
    [property: JsonPropertyName("server_name")] string ServerName = "Tuvima Library",
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

    [JsonPropertyName("area")]
    public string Area { get; set; } = "read";

    [JsonPropertyName("presentation")]
    public string Presentation { get; set; } = "catalogue";

    [JsonPropertyName("metadata_policy")]
    public string MetadataPolicy { get; set; } = "enriched";

    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    [JsonPropertyName("sources")]
    public List<LibrarySourceDto> Sources { get; set; } = [];

    [JsonPropertyName("primary_destination_source_id")]
    public string? PrimaryDestinationSourceId { get; set; }

    [JsonPropertyName("owner_profile_id")]
    public string? OwnerProfileId { get; set; }

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "household";

    [JsonPropertyName("authorized_profile_ids")]
    public List<string> AuthorizedProfileIds { get; set; } = [];

    [JsonPropertyName("accepted_intake_modes")]
    public List<string> AcceptedIntakeModes { get; set; } = [];

    [JsonPropertyName("duplicate_policy")]
    public string DuplicatePolicy { get; set; } = "skip_exact";

    [JsonPropertyName("organization_policy")]
    public LibraryOrganizationPolicyDto OrganizationPolicy { get; set; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class LibrarySourceDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = "secondary";

    [JsonPropertyName("management_mode")]
    public string ManagementMode { get; set; } = "existing_library";

    [JsonPropertyName("source_type")]
    public string SourceType { get; set; } = "local_folder";

    [JsonPropertyName("include_subdirectories")]
    public bool IncludeSubdirectories { get; set; } = true;

    [JsonPropertyName("access_mode")]
    public string AccessMode { get; set; } = "read_only";

    [JsonPropertyName("writeback_override")]
    public bool? WritebackOverride { get; set; }

    [JsonPropertyName("participates_in_organization")]
    public bool ParticipatesInOrganization { get; set; }

    [JsonPropertyName("intake_role")]
    public string IntakeRole { get; set; } = "none";

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("profile_id")]
    public string? ProfileId { get; set; }
}

public sealed class LibraryOrganizationPolicyDto
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "tuvima_standard";

    [JsonPropertyName("custom_template")]
    public string? CustomTemplate { get; set; }

    [JsonPropertyName("preserve_originals")]
    public bool PreserveOriginals { get; set; } = true;
}

public sealed class UpdateLibrariesRequest
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = "6.0";

    [JsonPropertyName("storage_locations")]
    public List<ServerStorageLocationDto> StorageLocations { get; init; } = [];

    [JsonPropertyName("view_storage")]
    public ViewStorageDto ViewStorage { get; init; } = new();

    [JsonPropertyName("libraries")]
    public List<LibraryFolderDto> Libraries { get; init; } = [];

    [JsonPropertyName("personal_library_policy")]
    public PersonalLibraryPolicyDto PersonalLibraryPolicy { get; init; } = new();
}

public sealed class LibrariesConfigurationDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = "6.0";

    [JsonPropertyName("storage_locations")]
    public List<ServerStorageLocationDto> StorageLocations { get; init; } = [];

    [JsonPropertyName("view_storage")]
    public ViewStorageDto ViewStorage { get; init; } = new();

    [JsonPropertyName("libraries")]
    public List<LibraryFolderDto> Libraries { get; init; } = [];

    [JsonPropertyName("personal_library_policy")]
    public PersonalLibraryPolicyDto PersonalLibraryPolicy { get; init; } = new();
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

    [JsonPropertyName("default_visibility")]
    public string DefaultVisibility { get; set; } = "private";
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

    public static bool RequiresWrite(string? value) => value is
        ManagedLibrary or PersonalSpaceManaged;

    public static bool IsValid(string? value) => value is
        ManagedLibrary or ExistingLibrary or PersonalSpaceManaged or PersonalSpaceExisting;
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

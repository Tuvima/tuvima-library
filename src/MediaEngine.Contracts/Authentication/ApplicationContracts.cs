using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

[JsonConverter(typeof(JsonStringEnumConverter<ApplicationTypeDto>))]
public enum ApplicationTypeDto
{
    UserClient,
    ServerIntegration,
    Automation,
    Other,
}

public sealed record ApplicationPermissionDefinitionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("risk")] string Risk,
    [property: JsonPropertyName("application_types")] IReadOnlyList<ApplicationTypeDto> ApplicationTypes,
    [property: JsonPropertyName("requires_user_context")] bool RequiresUserContext,
    [property: JsonPropertyName("provenance")] string Provenance,
    [property: JsonPropertyName("plugin_id")] string? PluginId,
    [property: JsonPropertyName("sort_order")] int SortOrder,
    [property: JsonPropertyName("is_available")] bool IsAvailable,
    [property: JsonPropertyName("unavailable_reason")] string? UnavailableReason);

public sealed record ApplicationPermissionPresetDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("permission_ids")] IReadOnlyList<string> PermissionIds,
    [property: JsonPropertyName("is_administrator")] bool IsAdministrator);

public sealed record ApplicationResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("application_type")] ApplicationTypeDto ApplicationType,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("is_administrator")] bool IsAdministrator,
    [property: JsonPropertyName("authorization_version")] long AuthorizationVersion,
    [property: JsonPropertyName("permission_ids")] IReadOnlyList<string> PermissionIds,
    [property: JsonPropertyName("client_ids")] IReadOnlyList<string> ClientIds,
    [property: JsonPropertyName("credentials")] IReadOnlyList<ApplicationCredentialResponse> Credentials,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt);

public sealed record ApplicationCredentialResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("application_id")] Guid ApplicationId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt,
    [property: JsonPropertyName("revoked_at")] DateTimeOffset? RevokedAt);

public sealed record CreateApplicationRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("application_type")] ApplicationTypeDto ApplicationType,
    [property: JsonPropertyName("is_administrator")] bool IsAdministrator,
    [property: JsonPropertyName("permission_ids")] IReadOnlyList<string> PermissionIds);


public sealed record UpdateApplicationRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("application_type")] ApplicationTypeDto ApplicationType,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("is_administrator")] bool IsAdministrator);

public sealed record SetApplicationPermissionsRequest(
    [property: JsonPropertyName("permission_ids")] IReadOnlyList<string> PermissionIds);

public sealed record SetApplicationClientBindingsRequest(
    [property: JsonPropertyName("client_ids")] IReadOnlyList<string> ClientIds);

public sealed record CreateApplicationCredentialRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("approval_hash")] string? ApprovalHash = null);

public sealed record ApplicationCredentialIssuedResponse(
    [property: JsonPropertyName("credential")] ApplicationCredentialResponse Credential,
    [property: JsonPropertyName("plaintext_credential")] string PlaintextCredential);

public sealed record RotateApplicationCredentialRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

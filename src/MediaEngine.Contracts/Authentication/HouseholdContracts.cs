using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

/// <summary>A household as the Profiles page lists it: its name and how many people and sign-ins it holds.</summary>
public sealed record HouseholdResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("profile_count")] int ProfileCount,
    [property: JsonPropertyName("account_count")] int AccountCount,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("hidden_library_ids")] IReadOnlyList<Guid>? HiddenLibraryIds = null);

/// <summary>
/// The libraries hidden for a whole household (<c>GET /access/households/{id}/libraries</c>, library access D2). Nothing is hidden by default.
/// A library hidden here is absent for every profile in the household and cannot be shown to a person by any administrator. A Vault library
/// is never listed.
/// </summary>
public sealed record HouseholdLibraryAccessResponse(
    [property: JsonPropertyName("household_id")] Guid HouseholdId,
    [property: JsonPropertyName("hidden_library_ids")] IReadOnlyList<Guid> HiddenLibraryIds);

/// <summary>
/// Replaces the libraries hidden for a household (<c>PUT /access/households/{id}/libraries</c>, server administrator only). Both an unknown
/// library and a Vault library are refused with a 400.
/// </summary>
public sealed record ReplaceHouseholdLibrariesRequest(
    [property: JsonPropertyName("hidden_library_ids")] IReadOnlyList<Guid> HiddenLibraryIds);

/// <summary>Adds an empty household (server administrator only).</summary>
public sealed record CreateHouseholdRequest(
    [property: JsonPropertyName("name")] string Name);

/// <summary>Renames a household (server administrator only).</summary>
public sealed record RenameHouseholdRequest(
    [property: JsonPropertyName("name")] string Name);

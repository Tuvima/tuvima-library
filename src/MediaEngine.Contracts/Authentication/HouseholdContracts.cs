using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

/// <summary>A household as the Profiles page lists it: its name and how many people and sign-ins it holds.</summary>
public sealed record HouseholdResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("profile_count")] int ProfileCount,
    [property: JsonPropertyName("account_count")] int AccountCount,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

/// <summary>Adds an empty household (server administrator only).</summary>
public sealed record CreateHouseholdRequest(
    [property: JsonPropertyName("name")] string Name);

/// <summary>Renames a household (server administrator only).</summary>
public sealed record RenameHouseholdRequest(
    [property: JsonPropertyName("name")] string Name);

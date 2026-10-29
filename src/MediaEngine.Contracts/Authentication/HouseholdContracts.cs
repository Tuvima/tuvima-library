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

/// <summary>
/// What a household's Kid's profiles may see (<c>GET</c> and <c>PUT /access/households/{id}/kid-settings</c>, library access D9 and D10): a
/// maximum level for each area, one of <c>G</c>, <c>PG</c>, <c>PG-13</c> or <c>R</c> (<c>level_choices</c>, lowest first), and whether items
/// with no rating are shown. Defaults: <c>PG</c> for all three, unrated shown. Every Kid's profile of the household follows these live.
/// </summary>
public sealed record HouseholdKidSettingsResponse(
    [property: JsonPropertyName("household_id")] Guid HouseholdId,
    [property: JsonPropertyName("watch_level")] string WatchLevel,
    [property: JsonPropertyName("read_level")] string ReadLevel,
    [property: JsonPropertyName("listen_level")] string ListenLevel,
    [property: JsonPropertyName("show_unrated")] bool ShowUnrated,
    [property: JsonPropertyName("level_choices")] IReadOnlyList<string> LevelChoices);

/// <summary>Replaces all of a household's Kid settings (server administrator or that household's administrator). An unknown level is refused with a 400.</summary>
public sealed record UpdateHouseholdKidSettingsRequest(
    [property: JsonPropertyName("watch_level")] string WatchLevel,
    [property: JsonPropertyName("read_level")] string ReadLevel,
    [property: JsonPropertyName("listen_level")] string ListenLevel,
    [property: JsonPropertyName("show_unrated")] bool ShowUnrated);

/// <summary>Adds an empty household (server administrator only).</summary>
public sealed record CreateHouseholdRequest(
    [property: JsonPropertyName("name")] string Name);

/// <summary>
/// The order people are listed in (Who's watching and the profile menu): every person of the household exactly once, first to last.
/// </summary>
public sealed record ReorderHouseholdProfilesRequest(
    [property: JsonPropertyName("profile_ids")] IReadOnlyList<Guid> ProfileIds);

/// <summary>Renames a household (server administrator only).</summary>
public sealed record RenameHouseholdRequest(
    [property: JsonPropertyName("name")] string Name);

using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Backups;

/// <summary>
/// What an administrator sees before importing a user's file (backup and restore spec D19). Nothing has been written to the data store.
/// The file is kept for a short while under <see cref="OperationId"/> so the next step imports exactly what was shown.
/// </summary>
/// <param name="OperationId">Names the checked file for the import step.</param>
/// <param name="Email">The email the person signs in with.</param>
/// <param name="HouseholdName">The household they belonged to, when the file holds one.</param>
/// <param name="Profiles">The names of the profiles under the account.</param>
/// <param name="PersonalItems">How many items of progress, bookmarks, favourites and collection items they carry.</param>
/// <param name="BlockedReason">One plain sentence when the import cannot go ahead; null when it can.</param>
public sealed record UserImportPreviewDto(
    [property: JsonPropertyName("operation_id")] Guid OperationId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("household_name")] string? HouseholdName,
    [property: JsonPropertyName("profiles")] IReadOnlyList<string> Profiles,
    [property: JsonPropertyName("personal_items")] int PersonalItems,
    [property: JsonPropertyName("blocked_reason")] string? BlockedReason);

/// <summary>What a user import did. The person can sign in but sees nothing until an administrator gives them access.</summary>
/// <param name="AccountId">The new account.</param>
/// <param name="DisplayName">The email the person signs in with.</param>
/// <param name="Profiles">The names of the profiles that came with them.</param>
/// <param name="WaitingItems">Items that wait for their titles to be found, and attach when they are.</param>
public sealed record UserImportResultDto(
    [property: JsonPropertyName("account_id")] Guid AccountId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("profiles")] IReadOnlyList<string> Profiles,
    [property: JsonPropertyName("waiting_items")] int WaitingItems);

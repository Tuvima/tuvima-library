using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

/// <summary>
/// "If these files move into this library, who would stop seeing them?" (simpler access D13). Name the files one way: a folder
/// (<c>source_id</c>, or <c>folder_path</c> with an optional <c>library_id</c> it is in now) or a list of <c>asset_ids</c>, at most 5,000.
/// <c>target_library_id</c> must be a configured library that is not in the Vault. Administrators only.
/// </summary>
public sealed record VisibilityAfterMoveRequest(
    [property: JsonPropertyName("target_library_id")] Guid TargetLibraryId,
    [property: JsonPropertyName("source_id")] string? SourceId = null,
    [property: JsonPropertyName("folder_path")] string? FolderPath = null,
    [property: JsonPropertyName("library_id")] Guid? LibraryId = null,
    [property: JsonPropertyName("asset_ids")] IReadOnlyList<Guid>? AssetIds = null);

/// <summary>The people (profiles) who see the files now and would not after the move, one per profile with at least one such file. Never lists administrators.</summary>
public sealed record VisibilityAfterMoveResponse(
    [property: JsonPropertyName("people")] IReadOnlyList<VisibilityLossDto> People);

/// <summary>
/// One profile that would lose sight of <c>file_count</c> files (files, not titles). <c>display_name</c> is the profile's name. When the
/// household's own list of libraries (library access D2) hides the target library, every person in that household loses the files, so the
/// household is named once instead: <c>household_id</c> is set, <c>profile_id</c> is empty and <c>display_name</c> is the household's name;
/// <c>file_count</c> is the most any one of its people loses.
/// </summary>
public sealed record VisibilityLossDto(
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("household_id")] Guid? HouseholdId = null);

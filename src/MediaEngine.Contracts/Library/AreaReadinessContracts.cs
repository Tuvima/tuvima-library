using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

/// <summary>The areas <c>GET /library/areas/{area}/readiness</c> answers for (honest-waiting-messages WP1).</summary>
public static class AreaReadinessAreas
{
    public const string Home = "home";
    public const string Watch = "watch";
    public const string Read = "read";
    public const string Listen = "listen";
}

/// <summary>The values of <see cref="AreaReadinessDto.Audience"/>: which version of the answer the asking profile gets (spec D6).</summary>
public static class AreaReadinessAudiences
{
    public const string Administrator = "administrator";
    public const string Member = "member";
    public const string Kids = "kids";
}

/// <summary>
/// The values of <see cref="AreaReadinessDto.Reason"/>, in the order Library picks them (spec D1): the cause comes before its effects.
/// <c>ready</c> means files are ready and nothing is waiting, so an empty page is only showing a different tab.
/// </summary>
public static class AreaReadinessReasons
{
    public const string SetupUnfinished = "setup_unfinished";
    public const string NoLibrary = "no_library";
    public const string FolderUnreachable = "folder_unreachable";
    public const string StillLooking = "still_looking";
    public const string WaitingForSource = "waiting_for_source";
    public const string WaitingInReview = "waiting_in_review";
    public const string NothingFound = "nothing_found";
    public const string Ready = "ready";
    /// <summary>Only for a member or Kids profile, who is told no reason (spec D6).</summary>
    public const string Hidden = "hidden";
}

/// <summary>
/// Why an area may be empty (<c>GET /library/areas/{area}/readiness</c>; spec honest-waiting-messages WP1). Read-only and counted from
/// what the Engine already keeps; nothing new is stored. A member or Kids profile gets only <c>audience</c> and <c>hidden</c> as the
/// reason, with no counts, names or flags (D6). <c>files_*</c> count media files, never titles.
/// </summary>
public sealed record AreaReadinessDto(
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("setup_finished")] bool? SetupFinished,
    [property: JsonPropertyName("library_count")] int LibraryCount,
    [property: JsonPropertyName("unreachable_folders")] IReadOnlyList<string> UnreachableFolders,
    [property: JsonPropertyName("files_found")] int FilesFound,
    [property: JsonPropertyName("files_in_progress")] int FilesInProgress,
    [property: JsonPropertyName("files_waiting_for_source")] int FilesWaitingForSource,
    [property: JsonPropertyName("files_in_review")] int FilesInReview,
    [property: JsonPropertyName("files_ready")] int FilesReady,
    [property: JsonPropertyName("files_being_scanned")] int FilesBeingScanned,
    [property: JsonPropertyName("media_types")] IReadOnlyList<AreaReadinessMediaTypeDto> MediaTypes);

/// <summary>The same counts for one kind of media in the area (for example Movies or Videos), so the Dashboard can word them and point at a tab that works (spec D4, D9).</summary>
public sealed record AreaReadinessMediaTypeDto(
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("files_found")] int FilesFound,
    [property: JsonPropertyName("files_in_progress")] int FilesInProgress,
    [property: JsonPropertyName("files_waiting_for_source")] int FilesWaitingForSource,
    [property: JsonPropertyName("files_in_review")] int FilesInReview,
    [property: JsonPropertyName("files_ready")] int FilesReady);

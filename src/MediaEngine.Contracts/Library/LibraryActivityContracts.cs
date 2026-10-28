using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

/// <summary>The values of <see cref="LibraryActivityEntryDto.Kind"/>: what sort of event a line on a library's Activity tab is.</summary>
public static class LibraryActivityKinds
{
    public const string Added = "added";
    public const string Review = "review";
    public const string Failed = "failed";
    public const string Scan = "scan";
    public const string Conversion = "conversion";
    public const string Merge = "merge";
    public const string Move = "move";
}

/// <summary>
/// What has happened in one library (<c>GET /library/libraries/{id}/activity</c>, administrators only; Library pages D16): a view of events
/// the Engine already records (what was added, what needs review, what failed, folder checks, renames, merges and moves), newest first,
/// for this library only. The Engine writes the words; the Dashboard shows them as they are. A Vault library is never returned.
/// </summary>
public sealed record LibraryActivityDto(
    [property: JsonPropertyName("library_id")] Guid LibraryId,
    [property: JsonPropertyName("entries")] IReadOnlyList<LibraryActivityEntryDto> Entries);

/// <summary>
/// One line of a library's Activity tab: when it happened, its kind (for the icon and colour), and one plain sentence. A line that needs a
/// person (<c>needs_review</c>) carries the Dashboard address of Needs Review in <c>route</c>.
/// </summary>
public sealed record LibraryActivityEntryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("needs_review")] bool NeedsReview,
    [property: JsonPropertyName("route")] string? Route);

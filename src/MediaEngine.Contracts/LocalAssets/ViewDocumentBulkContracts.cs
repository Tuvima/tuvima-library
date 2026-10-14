using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>What a bulk edit does to every selected document. Sent and stored as these exact lowercase words.</summary>
public static class ViewDocumentBulkOperations
{
    /// <summary>Adds the listed tags (by id or by name; a new name is added to the global list). Tags already there stay.</summary>
    public const string AddTags = "add_tags";

    /// <summary>Takes the listed tags (by id) off. Other tags stay. Takes nothing else away.</summary>
    public const string RemoveTags = "remove_tags";

    public const string SetCorrespondent = "set_correspondent";
    public const string SetDocumentType = "set_document_type";
    public const string SetDocumentDate = "set_document_date";

    /// <summary>Files documents waiting in the Inbox exactly as they are, without taking the suggestions.</summary>
    public const string MarkFiled = "mark_filed";

    /// <summary>Moves to the trash. The file itself is never deleted.</summary>
    public const string MoveToTrash = "move_to_trash";

    /// <summary>Takes documents out of the trash again.</summary>
    public const string Restore = "restore";

    public static readonly IReadOnlyList<string> All =
        [AddTags, RemoveTags, SetCorrespondent, SetDocumentType, SetDocumentDate, MarkFiled, MoveToTrash, Restore];

    /// <summary>The most documents one bulk call may change.</summary>
    public const int MaximumItems = 500;
}

/// <summary>How one document fared in a bulk edit.</summary>
public static class ViewDocumentBulkStatuses
{
    /// <summary>The document was changed.</summary>
    public const string Done = "done";

    /// <summary>Nothing was changed on purpose: it was already as asked, or this person may not change it.</summary>
    public const string Skipped = "skipped";

    /// <summary>The change was tried and did not work; the document is as it was.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One bulk edit. <c>item_ids</c> holds up to 500 documents; <c>operation</c> is one of
/// <see cref="ViewDocumentBulkOperations"/>. Only the value the operation needs is read. For the set operations a blank
/// value is refused unless <c>clear</c> is true, which takes the value away.
/// </summary>
public sealed record ViewDocumentBulkRequest(
    [property: JsonPropertyName("item_ids")] IReadOnlyList<Guid> ItemIds,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("tag_ids")] IReadOnlyList<Guid>? TagIds = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("correspondent")] string? Correspondent = null,
    [property: JsonPropertyName("document_type")] string? DocumentType = null,
    [property: JsonPropertyName("document_date")] DateOnly? DocumentDate = null,
    [property: JsonPropertyName("clear")] bool Clear = false);

public sealed record ViewDocumentBulkItemResultDto(
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>
/// What happened to every document in a bulk edit, one result per requested id in the order sent. A document that
/// could not be changed is reported, never silently left out, and is left exactly as it was.
/// </summary>
public sealed record ViewDocumentBulkResultDto(
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("requested")] int Requested,
    [property: JsonPropertyName("done")] int Done,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("items")] IReadOnlyList<ViewDocumentBulkItemResultDto> Items);

/// <summary>The remembered Documents sort for the signed-in profile. A blank sort means the default order.</summary>
public sealed record ViewDocumentsSortDto(
    [property: JsonPropertyName("sort")] string? Sort);

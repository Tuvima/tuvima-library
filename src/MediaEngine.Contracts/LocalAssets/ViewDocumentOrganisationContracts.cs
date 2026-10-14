using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>The kinds a custom document field can have. Stored and sent as these exact lowercase words.</summary>
public static class ViewDocumentFieldKinds
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";

    /// <summary>An amount (<c>number_value</c>) with its ISO 4217 currency code (<c>text_value</c>).</summary>
    public const string Money = "money";

    public const string Bool = "bool";

    /// <summary>One value from the field's list of choices, kept in <c>text_value</c>.</summary>
    public const string Choice = "choice";

    public static readonly IReadOnlyList<string> All = [Text, Number, Date, Money, Bool, Choice];
}

/// <summary>Where a document is in the filing flow. Documents added before filing existed count as filed.</summary>
public static class ViewDocumentFilingStates
{
    public const string Inbox = "inbox";
    public const string Filed = "filed";
}

/// <summary>
/// How the Documents list is ordered. Leaving the sort out keeps the usual order (newest date first). Stored and sent
/// as these exact lowercase words.
/// </summary>
public static class ViewDocumentSorts
{
    /// <summary>The day the document was added, newest first.</summary>
    public const string Added = "added";

    /// <summary>The document's own date (or the day it was added when it has none), newest first.</summary>
    public const string DocumentDate = "document_date";

    /// <summary>Title from A to Z, falling back to the file name.</summary>
    public const string Title = "title";

    /// <summary>Correspondent from A to Z; documents with no correspondent last.</summary>
    public const string Correspondent = "correspondent";

    public static readonly IReadOnlyList<string> All = [Added, DocumentDate, Title, Correspondent];
}

/// <summary>A tag, correspondent or document type as attached to one item: the name plus enough to filter by.</summary>
public sealed record ViewCatalogueRefDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("colour")] string? Colour = null);

/// <summary>
/// One value of a custom field on one document. Text and choice use <c>text_value</c>; a number uses
/// <c>number_value</c>; a date uses <c>date_value</c>; money uses <c>number_value</c> for the amount and
/// <c>text_value</c> for the ISO 4217 code; a yes/no field uses <c>bool_value</c>.
/// </summary>
public sealed record ViewDocumentFieldValueDto(
    [property: JsonPropertyName("field_id")] Guid FieldId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text_value")] string? TextValue = null,
    [property: JsonPropertyName("number_value")] double? NumberValue = null,
    [property: JsonPropertyName("date_value")] DateOnly? DateValue = null,
    [property: JsonPropertyName("bool_value")] bool? BoolValue = null);

/// <summary>A value the editor sends for one custom field. Sending a field with no value clears it.</summary>
public sealed record ViewDocumentFieldValueRequest(
    [property: JsonPropertyName("field_id")] Guid FieldId,
    [property: JsonPropertyName("text_value")] string? TextValue = null,
    [property: JsonPropertyName("number_value")] double? NumberValue = null,
    [property: JsonPropertyName("date_value")] DateOnly? DateValue = null,
    [property: JsonPropertyName("bool_value")] bool? BoolValue = null);

/// <summary>
/// Replaces one document's organising details. Tags and the correspondent and type are named, not numbered: a name
/// that already exists is reused and a new name is added to the list. Anything not sent is cleared, except
/// <c>field_values</c>: leaving it out keeps the custom field values as they are, an empty list clears them.
/// </summary>
public sealed record UpdateViewDocumentDetailsRequest(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("document_date")] DateOnly? DocumentDate = null,
    [property: JsonPropertyName("correspondent")] string? Correspondent = null,
    [property: JsonPropertyName("document_type")] string? DocumentType = null,
    [property: JsonPropertyName("archive_number")] string? ArchiveNumber = null,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("filing_state")] string? FilingState = null,
    [property: JsonPropertyName("tags")] IReadOnlyCollection<string>? Tags = null,
    [property: JsonPropertyName("tag_ids")] IReadOnlyCollection<Guid>? TagIds = null,
    [property: JsonPropertyName("field_values")] IReadOnlyCollection<ViewDocumentFieldValueRequest>? FieldValues = null);

/// <summary>A tag with the number of items the viewer can see it on.</summary>
public sealed record ViewTagDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("colour")] string? Colour,
    [property: JsonPropertyName("usage_count")] int UsageCount,
    [property: JsonPropertyName("can_manage")] bool CanManage);

public sealed record ViewCorrespondentDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("colour")] string? Colour,
    [property: JsonPropertyName("usage_count")] int UsageCount,
    [property: JsonPropertyName("can_manage")] bool CanManage);

public sealed record ViewDocumentTypeDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("usage_count")] int UsageCount,
    [property: JsonPropertyName("can_manage")] bool CanManage);

public sealed record ViewDocumentFieldDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("choices")] IReadOnlyList<string> Choices,
    [property: JsonPropertyName("usage_count")] int UsageCount,
    [property: JsonPropertyName("can_manage")] bool CanManage);

/// <summary>
/// The tags, correspondents, document types and custom fields one viewer may see: those in use on items they can
/// reach, plus ones they made themselves that are not used yet. Counts are the viewer's own, never install-wide.
/// </summary>
public sealed record ViewDocumentCatalogueDto(
    [property: JsonPropertyName("tags")] IReadOnlyList<ViewTagDto> Tags,
    [property: JsonPropertyName("correspondents")] IReadOnlyList<ViewCorrespondentDto> Correspondents,
    [property: JsonPropertyName("document_types")] IReadOnlyList<ViewDocumentTypeDto> DocumentTypes,
    [property: JsonPropertyName("fields")] IReadOnlyList<ViewDocumentFieldDto> Fields);

/// <summary>Adds or renames a tag, correspondent or document type. Case-insensitive duplicates are refused.</summary>
public sealed record ViewCatalogueEntryRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("colour")] string? Colour = null);

/// <summary>Adds or edits a custom field. The kind cannot change after the field is created.</summary>
public sealed record ViewDocumentFieldRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("choices")] IReadOnlyList<string>? Choices = null);

/// <summary>One condition on a custom field in a document filter.</summary>
public sealed record ViewDocumentFieldFilterDto(
    [property: JsonPropertyName("field_id")] Guid FieldId,
    [property: JsonPropertyName("value")] string Value);

/// <summary>
/// What the Documents page asks for on top of the usual search: every tag listed must be present, plus an optional
/// correspondent, type, date range (the document's date, or the day it was added when it has none), filing state
/// and custom field values. Sent to <c>GET /view/assets</c> as query values; this is the same shape in one place.
/// </summary>
public sealed record ViewDocumentFilterDto(
    [property: JsonPropertyName("tag_ids")] IReadOnlyList<Guid>? TagIds = null,
    [property: JsonPropertyName("correspondent_id")] Guid? CorrespondentId = null,
    [property: JsonPropertyName("document_type_id")] Guid? DocumentTypeId = null,
    [property: JsonPropertyName("date_from")] DateOnly? DateFrom = null,
    [property: JsonPropertyName("date_to")] DateOnly? DateTo = null,
    [property: JsonPropertyName("filing_state")] string? FilingState = null,
    [property: JsonPropertyName("fields")] IReadOnlyList<ViewDocumentFieldFilterDto>? Fields = null,
    [property: JsonPropertyName("sort")] string? Sort = null);

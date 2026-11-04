using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>
/// One Save from the photos, videos and documents editor (spec photos-documents-editor, WP1): everything the person changed on one or
/// many items, applied together. A field left null is left alone. All of it is checked first; if anything is refused nothing is
/// written, and if a step fails part-way the steps already taken are put back, so a failed Save never leaves 20 photos half-tagged.
/// </summary>
public sealed record ViewItemsEditRequest(
    [property: JsonPropertyName("item_ids")] IReadOnlyList<Guid> ItemIds,
    [property: JsonPropertyName("preview_only")] bool PreviewOnly = false,
    [property: JsonPropertyName("set_description")] bool SetDescription = false,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("tags_add")] IReadOnlyList<string>? TagsAdd = null,
    [property: JsonPropertyName("tags_remove")] IReadOnlyList<string>? TagsRemove = null,
    [property: JsonPropertyName("people_add")] IReadOnlyList<string>? PeopleAdd = null,
    [property: JsonPropertyName("people_remove")] IReadOnlyList<string>? PeopleRemove = null,
    [property: JsonPropertyName("captured_at")] DateTimeOffset? CapturedAt = null,
    [property: JsonPropertyName("captured_shift_seconds")] long? CapturedShiftSeconds = null,
    [property: JsonPropertyName("captured_reset")] bool CapturedReset = false,
    [property: JsonPropertyName("location")] UpdateLocalAssetLocationRequest? Location = null,
    [property: JsonPropertyName("gallery_add_ids")] IReadOnlyList<Guid>? GalleryAddIds = null,
    [property: JsonPropertyName("gallery_remove_ids")] IReadOnlyList<Guid>? GalleryRemoveIds = null,
    [property: JsonPropertyName("create_gallery_name")] string? CreateGalleryName = null,
    [property: JsonPropertyName("document")] ViewItemsDocumentEdit? Document = null);

/// <summary>
/// The document fields of one Save. A group offers only Correspondent, Type and Date; Title, Archive number and Notes are single-item only
/// and are refused for a group. <c>clear_*</c> takes the value away.
/// </summary>
public sealed record ViewItemsDocumentEdit(
    [property: JsonPropertyName("set_title")] bool SetTitle = false,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("set_correspondent")] bool SetCorrespondent = false,
    [property: JsonPropertyName("correspondent")] string? Correspondent = null,
    [property: JsonPropertyName("set_document_type")] bool SetDocumentType = false,
    [property: JsonPropertyName("document_type")] string? DocumentType = null,
    [property: JsonPropertyName("set_document_date")] bool SetDocumentDate = false,
    [property: JsonPropertyName("document_date")] DateOnly? DocumentDate = null,
    [property: JsonPropertyName("set_archive_number")] bool SetArchiveNumber = false,
    [property: JsonPropertyName("archive_number")] string? ArchiveNumber = null,
    [property: JsonPropertyName("set_notes")] bool SetNotes = false,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("field_values")] IReadOnlyList<ViewDocumentFieldValueRequest>? FieldValues = null);

/// <summary>Why one item stopped a Save (the item named, in plain words). Nothing was written when any exist.</summary>
public sealed record ViewItemsEditRefusalDto(
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>
/// The answer to <see cref="ViewItemsEditRequest"/>. <c>saved</c> is true only when every change was written (never on a preview).
/// <c>refusals</c> lists the items that stopped it; <c>created_gallery_id</c> is the new gallery when one was asked for;
/// <c>day_moves</c> counts photos whose date moved onto another calendar day, for Review.
/// </summary>
public sealed record ViewItemsEditResultDto(
    [property: JsonPropertyName("saved")] bool Saved,
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("changed_count")] int ChangedCount,
    [property: JsonPropertyName("refusals")] IReadOnlyList<ViewItemsEditRefusalDto> Refusals,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("created_gallery_id")] Guid? CreatedGalleryId = null,
    [property: JsonPropertyName("day_moves")] int DayMoves = 0);

/// <summary>Limits of one Save.</summary>
public static class ViewItemsEditLimits
{
    /// <summary>The most items one Save may change (the selection bar's own limit).</summary>
    public const int MaximumItems = 500;
}

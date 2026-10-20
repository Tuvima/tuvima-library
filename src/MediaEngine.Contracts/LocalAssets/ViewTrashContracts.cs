using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>
/// What each trashed item would do if its owner emptied Trash. Only <see cref="Delete"/> items leave the disk; every other
/// outcome is a reason the item stays in Trash.
/// </summary>
public static class ViewTrashOutcomes
{
    /// <summary>Every file of the item sits in a managed folder inside the owner's own folder and the safety gate allows removing it.</summary>
    public const string Delete = "delete";

    /// <summary>At least one file is in a linked folder Tuvima only reads.</summary>
    public const string SkipReadOnly = "skip_read_only";

    /// <summary>The item is waiting to be added to the Shared Library, or is being copied there.</summary>
    public const string SkipContributionPending = "skip_contribution_pending";

    /// <summary>The safety gate (or the folder containment check) refuses at least one of the item's files.</summary>
    public const string SkipRefused = "skip_refused";
}

/// <summary>What is in the signed-in person's own Trash, for the size line on the Trash view. Nothing here is a disk path.</summary>
public sealed record ViewTrashSummaryDto(
    [property: JsonPropertyName("item_count")] int ItemCount,
    [property: JsonPropertyName("photo_count")] int PhotoCount,
    [property: JsonPropertyName("video_count")] int VideoCount,
    [property: JsonPropertyName("document_count")] int DocumentCount,
    [property: JsonPropertyName("other_count")] int OtherCount,
    [property: JsonPropertyName("bytes")] long Bytes);

/// <summary>One trashed item in the Empty Trash preview.</summary>
public sealed record ViewTrashPreviewItemDto(
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("media_kind")] string MediaKind,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>A Manual Gallery that would lose items, with how many of the items being deleted it holds.</summary>
public sealed record ViewTrashPreviewGalleryDto(
    [property: JsonPropertyName("gallery_id")] Guid GalleryId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("item_count")] int ItemCount);

/// <summary>Items that stay in Trash for one reason.</summary>
public sealed record ViewTrashPreviewSkippedDto(
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("count")] int Count);

/// <summary>
/// Exactly what Empty Trash would do right now, worked out by the Engine. The person confirms this summary by sending
/// <see cref="Fingerprint"/> back; if anything changed in between, the Engine refuses. No disk paths are included.
/// </summary>
public sealed record ViewTrashEmptyPreviewDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ViewTrashPreviewItemDto> Items,
    [property: JsonPropertyName("galleries")] IReadOnlyList<ViewTrashPreviewGalleryDto> Galleries,
    [property: JsonPropertyName("delete_count")] int DeleteCount,
    [property: JsonPropertyName("photo_count")] int PhotoCount,
    [property: JsonPropertyName("video_count")] int VideoCount,
    [property: JsonPropertyName("document_count")] int DocumentCount,
    [property: JsonPropertyName("other_count")] int OtherCount,
    [property: JsonPropertyName("skipped")] IReadOnlyList<ViewTrashPreviewSkippedDto> Skipped,
    [property: JsonPropertyName("bytes_freed")] long BytesFreed,
    [property: JsonPropertyName("has_compound")] bool HasCompound,
    [property: JsonPropertyName("fingerprint")] string Fingerprint);

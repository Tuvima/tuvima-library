using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>
/// One calendar month (UTC) of a paired app's photo timeline. Items are the shared
/// <see cref="LocalAssetDto"/>, newest first. A month can continue on the next page, so a client
/// merges buckets that carry the same <c>year</c> and <c>month</c>.
/// </summary>
public sealed record ViewClientTimelineMonthDto(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("items")] IReadOnlyList<LocalAssetDto> Items);

/// <summary>
/// A page of the paired app's own photo and video timeline, newest first, grouped by month.
/// <c>next_cursor</c> is opaque and is sent back unchanged as <c>cursor</c>.
/// </summary>
public sealed record ViewClientTimelinePageDto(
    [property: JsonPropertyName("months")] IReadOnlyList<ViewClientTimelineMonthDto> Months,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore);

/// <summary>
/// A cursor page of photos and videos (the items of one Gallery or one person), newest first.
/// <c>next_cursor</c> is opaque and is sent back unchanged as <c>cursor</c>.
/// </summary>
public sealed record ViewClientItemPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<LocalAssetDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore);

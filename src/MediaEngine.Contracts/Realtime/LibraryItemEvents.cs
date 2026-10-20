using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Realtime;

/// <summary>
/// Titles were deleted from the library (an administrator confirmed it). Open screens on every device and profile drop them
/// without a reload. Not the same as <c>MediaRemoved</c>, which reports one file that went missing.
/// </summary>
public sealed record LibraryItemsRemovedEvent(
    [property: JsonPropertyName("work_ids")] IReadOnlyList<Guid> WorkIds,
    [property: JsonPropertyName("asset_ids")] IReadOnlyList<Guid> AssetIds);

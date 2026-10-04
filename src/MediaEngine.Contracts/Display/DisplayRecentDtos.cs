namespace MediaEngine.Contracts.Display;

public sealed record DisplayRecentPageDto(string Type, IReadOnlyList<DisplayRecentItemDto> Items, string? NextCursor, bool HasMore);

/// <summary>Exactly one source branch is populated. View identities never become catalogue works.</summary>
public sealed record DisplayRecentItemDto(string Key, DateTimeOffset AddedAt, DisplayCardDto? Catalogue, DisplayRecentViewAssetDto? ViewAsset);

/// <summary>Stable metadata only; the Dashboard grants media access for the current profile.</summary>
public sealed record DisplayRecentViewAssetDto(Guid AssetId, Guid LibraryId, string Title, string FileName, string MediaKind, int? Width, int? Height, double? DurationSeconds, DateTimeOffset AddedAt);

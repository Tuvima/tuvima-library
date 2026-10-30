namespace MediaEngine.Contracts.Display;

public sealed record DisplayRecentPageDto(string Type, IReadOnlyList<DisplayRecentItemDto> Items, string? NextCursor, bool HasMore)
{
    /// <summary>
    /// Types (watch, read, listen, view) that have recent items for this profile, whatever filter is on, so the
    /// Dashboard can hide pills with nothing behind them. Filled on the first page only; null on later pages.
    /// </summary>
    public IReadOnlyList<string>? AvailableTypes { get; init; }
}

/// <summary>Exactly one source branch is populated. View identities never become catalogue works.</summary>
public sealed record DisplayRecentItemDto(string Key, DateTimeOffset AddedAt, DisplayCardDto? Catalogue, DisplayRecentViewAssetDto? ViewAsset);

/// <summary>Stable metadata only; the Dashboard grants media access for the current profile.</summary>
public sealed record DisplayRecentViewAssetDto(Guid AssetId, Guid LibraryId, string Title, string FileName, string MediaKind, int? Width, int? Height, double? DurationSeconds, DateTimeOffset AddedAt);

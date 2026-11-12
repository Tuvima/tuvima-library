namespace MediaEngine.Web.Models.ViewDTOs;

public enum MediaHubSectionType
{
    Read,
    Watch,
    Listen,
}

public enum MediaArtworkShape
{
    Landscape,
    LandscapeWide,
    Square,
    Portrait,
    BookPortrait,
    Stack,
}

public sealed record MediaHubModeViewModel(
    string Id,
    string Label,
    string Route,
    string Icon);

public sealed class MediaHubShelfViewModel
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public IReadOnlyList<MediaTileViewModel> Items { get; init; } = [];
    public string? SeeAllRoute { get; init; }
    public MediaTileShape? ShapeOverride { get; init; }
    public bool IsContinueShelf { get; init; }
    public bool SupportsMixedArtwork { get; init; } = true;

    public MediaTileShelfViewModel ToMediaTileShelf() => new()
    {
        Key = Key,
        Title = Title,
        Subtitle = Subtitle,
        Items = Items,
        SeeAllRoute = SeeAllRoute,
    };

    public static MediaHubShelfViewModel FromShelf(
        MediaTileShelfViewModel shelf,
        MediaTileShape? shapeOverride = null) => new()
        {
            Key = shelf.Key,
            Title = shelf.Title,
            Subtitle = shelf.Subtitle,
            Items = shelf.Items,
            SeeAllRoute = shelf.SeeAllRoute,
            IsContinueShelf = shelf.Kind == MediaTileShelfKind.Continue,
            ShapeOverride = shapeOverride,
        };
}

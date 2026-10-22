namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>The shape a Vault card takes (Videos spec D41): scenes are landscape, performers portrait, studios a logo on a neutral tile, tags chips.</summary>
public enum VaultCardShape
{
    Scene,
    Performer,
    Studio,
    Tag,

    /// <summary>A Vault image (Videos spec D44): a square thumbnail that opens the viewer.</summary>
    Image,
}

/// <summary>
/// One Vault video, image or tile as the Vault screens draw it. Dashboard-only; it is mapped from whatever the Engine sends.
/// The scene facts (<see cref="AssetId"/> onward) are set for scenes; <see cref="Count"/> is the number of owned scenes for a
/// performer, studio, tag or folder tile.
/// </summary>
public sealed record VaultCatalogueItem(string Id, string Title, string? Subtitle, string? CoverUrl, string Route)
{
    public VaultCardShape Shape { get; init; } = VaultCardShape.Scene;

    /// <summary>True for a scene with no stash-box record: it shows a small "Unmatched" mark (D40).</summary>
    public bool Unmatched { get; init; }

    /// <summary>The scene's media asset id (Identify and the scene page use it).</summary>
    public Guid? AssetId { get; init; }

    public string? FileName { get; init; }
    public string? Folder { get; init; }
    public string? FolderId { get; init; }
    public double? DurationSeconds { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? AddedAt { get; init; }
    public string? ReleaseDate { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Up to four saved stills for the optional hover cycling; empty unless the scene has more than one.</summary>
    public IReadOnlyList<string> HoverUrls { get; init; } = [];

    /// <summary>The larger web-sized copy (scene 1280, performer 800 wide), offered to high-density screens. Null when there is no larger copy.</summary>
    public string? CoverUrl2x { get; init; }

    public int? Count { get; init; }

    /// <summary>The Vault image's id; set only for an image card (the viewer asks for its picture by this id).</summary>
    public Guid? ImageId { get; init; }

    public bool Favorite { get; init; }
}

/// <summary>
/// What a Vault list or search answered. <see cref="Supported"/> is false when the Engine could not answer (it is unreachable or
/// the session is locked), so the screens can say the Vault is empty without pretending a search ran.
/// <see cref="Heading"/> is the name of the performer, studio or tag a list was filtered by.
/// </summary>
public sealed record VaultCatalogueResult(bool Supported, IReadOnlyList<VaultCatalogueItem> Items)
{
    public static VaultCatalogueResult NotYetAvailable { get; } = new(false, []);

    public string? Heading { get; init; }

    /// <summary>How many items match before paging; equals <see cref="Items"/> count when everything came back.</summary>
    public int Total { get; init; } = Items.Count;

    /// <summary>Asks for the next page of images (images page by cursor, not by count); null when there is no more.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>One scene's own page: the card facts, the description and the linked names (each a link to its owned scenes).</summary>
public sealed record VaultSceneDetail(
    VaultCatalogueItem Scene,
    Guid WorkId,
    string? Description,
    string? MatchedBy,
    IReadOnlyList<VaultLinkedName> Performers,
    IReadOnlyList<VaultLinkedName> Studios,
    IReadOnlyList<VaultLinkedName> Tags);

public sealed record VaultLinkedName(Guid Id, string Name);

/// <summary>Which scenes a Vault list asks for beyond the view itself (Videos spec D39).</summary>
public sealed record VaultListFilter
{
    /// <summary>"all", "matched" or "unmatched".</summary>
    public string Match { get; init; } = "all";

    /// <summary>"all", "videos" or "images".</summary>
    public string Kind { get; init; } = "all";

    /// <summary>On the Images view: "all" for every image, or a gallery's id for the images of that gallery.</summary>
    public string? ImageGallery { get; init; }

    /// <summary>The cursor an image page was answered with, to ask for the page after it.</summary>
    public string? Cursor { get; init; }

    public Guid? PerformerId { get; init; }
    public Guid? StudioId { get; init; }
    public Guid? TagId { get; init; }
    public string? FolderId { get; init; }

    /// <summary>How many scenes to skip and to ask for (the list is shown a page at a time).</summary>
    public int Skip { get; init; }

    public int Take { get; init; }
}

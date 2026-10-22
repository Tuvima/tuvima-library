using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Vault;

/// <summary>The three kinds of Vault image gallery (Videos spec D44).</summary>
public static class VaultImageGalleryKinds
{
    /// <summary>A folder of images on disk. It appears by itself and cannot be edited.</summary>
    public const string Folder = "folder";

    /// <summary>A gallery filled by hand.</summary>
    public const string Manual = "manual";

    /// <summary>A gallery filled by a rule, the same rule document View's Smart Galleries use.</summary>
    public const string Smart = "smart";

    public static bool IsValid(string? value) => value is Folder or Manual or Smart;

    /// <summary>True for the two kinds a person can make or change.</summary>
    public static bool IsEditable(string? value) => value is Manual or Smart;
}

/// <summary>
/// One Vault image as a thumbnail, a row or the viewer draws it (Videos spec D44). Pictures are asked for by id
/// (<c>GET /api/v1/vault/images/{id}/thumbnail</c>), never by address or path, and only a session with Vault mode on is answered.
/// </summary>
public sealed class VaultImageDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>The stored title, else the file's name without its extension.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("mime_type")]
    public string MimeType { get; init; } = string.Empty;

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    /// <summary>When the picture was taken (else when the file was made); the timeline orders and groups by it.</summary>
    [JsonPropertyName("captured_at")]
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>When the image was added to the Vault.</summary>
    [JsonPropertyName("added_at")]
    public DateTimeOffset AddedAt { get; init; }

    [JsonPropertyName("favorite")]
    public bool Favorite { get; init; }

    /// <summary>True while the image is in the recycle bin.</summary>
    [JsonPropertyName("deleted")]
    public bool Deleted { get; init; }

    /// <summary>The folder gallery the image sits in; null when it has none.</summary>
    [JsonPropertyName("folder_id")]
    public Guid? FolderId { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A page of Vault images, newest picture first. <c>next_cursor</c> asks for the next page.</summary>
public sealed class VaultImagePageDto
{
    [JsonPropertyName("items")]
    public IReadOnlyList<VaultImageDto> Items { get; init; } = [];

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }
}

public sealed class VaultImageTagDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("image_count")]
    public int ImageCount { get; init; }
}

/// <summary>One image's own details for the viewer: the card facts, its tags and how it was shot.</summary>
public sealed class VaultImageDetailDto
{
    [JsonPropertyName("image")]
    public VaultImageDto Image { get; init; } = new();

    [JsonPropertyName("tags")]
    public IReadOnlyList<VaultImageTagDto> Tags { get; init; } = [];

    /// <summary>The folder gallery's name; null when the image has none.</summary>
    [JsonPropertyName("folder")]
    public string? Folder { get; init; }

    [JsonPropertyName("size_bytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("device")]
    public string? Device { get; init; }
}

/// <summary>A gallery in the Vault's image list: a folder, a hand-made gallery or a rule gallery.</summary>
public sealed class VaultImageGalleryDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>One of <see cref="VaultImageGalleryKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = VaultImageGalleryKinds.Folder;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("image_count")]
    public int ImageCount { get; init; }

    /// <summary>The image shown on the gallery's tile; null for an empty gallery.</summary>
    [JsonPropertyName("cover_image_id")]
    public Guid? CoverImageId { get; init; }

    /// <summary>The rule of a smart gallery, in the same rule document View's Smart Galleries use.</summary>
    [JsonPropertyName("smart_rule_json")]
    public string? SmartRuleJson { get; init; }

    [JsonPropertyName("editable")]
    public bool Editable { get; init; }
}

public sealed class VaultImageGalleryRequest
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary><see cref="VaultImageGalleryKinds.Manual"/> or <see cref="VaultImageGalleryKinds.Smart"/>; ignored when a gallery is changed.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = VaultImageGalleryKinds.Manual;

    [JsonPropertyName("smart_rule_json")]
    public string? SmartRuleJson { get; init; }

    [JsonPropertyName("cover_image_id")]
    public Guid? CoverImageId { get; init; }
}

public sealed class VaultImageIdsRequest
{
    [JsonPropertyName("image_ids")]
    public IReadOnlyList<Guid> ImageIds { get; init; } = [];
}

public sealed class VaultImageTagsRequest
{
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class VaultImageFavoriteRequest
{
    [JsonPropertyName("favorite")]
    public bool Favorite { get; init; }
}

/// <summary>Searches the Vault's images. It is a body, never an address, so the words are never put in a URL or a log line.</summary>
public sealed class VaultImageSearchRequest
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>Limits the search to one gallery (a folder, a hand-made gallery or a rule gallery).</summary>
    [JsonPropertyName("gallery_id")]
    public Guid? GalleryId { get; init; }

    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }

    [JsonPropertyName("take")]
    public int? Take { get; init; }
}

/// <summary>What looking for new Vault images found. Counts only.</summary>
public sealed class VaultImageScanDto
{
    [JsonPropertyName("images_found")]
    public int ImagesFound { get; init; }

    [JsonPropertyName("images_added")]
    public int ImagesAdded { get; init; }

    [JsonPropertyName("errors")]
    public int Errors { get; init; }
}

/// <summary>What emptying the Vault's image recycle bin removed.</summary>
public sealed class VaultImageEmptiedDto
{
    [JsonPropertyName("removed")]
    public int Removed { get; init; }
}

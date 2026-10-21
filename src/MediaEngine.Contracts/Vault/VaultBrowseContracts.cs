using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Vault;

/// <summary>
/// One Vault scene as a card or a row draws it (Videos spec D39, D40, D41). Pictures are artwork ids, never addresses: the Dashboard
/// asks for them by id (<c>GET /api/v1/vault/artwork/{id}?w=</c>), and only a session with Vault mode on is ever answered.
/// </summary>
public sealed class VaultSceneDto
{
    /// <summary>The media asset id. The scene's own page and Identify use it.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>The work id the player opens (<c>/watch/player/{work_id}</c>).</summary>
    [JsonPropertyName("work_id")]
    public Guid WorkId { get; init; }

    /// <summary>The stored title, else a readable form of the file name.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>True when a stash-box record is attached. An unmatched scene shows a small "Unmatched" mark (D40).</summary>
    [JsonPropertyName("matched")]
    public bool Matched { get; init; }

    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    /// <summary>The folder (series or section) the file sits in. Its name only, never a path.</summary>
    [JsonPropertyName("folder")]
    public string Folder { get; init; } = string.Empty;

    [JsonPropertyName("folder_id")]
    public string FolderId { get; init; } = string.Empty;

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("size_bytes")]
    public long? SizeBytes { get; init; }

    [JsonPropertyName("added_at")]
    public DateTimeOffset? AddedAt { get; init; }

    /// <summary>The release date (yyyy-MM-dd) of a matched scene.</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }

    [JsonPropertyName("studio")]
    public string? Studio { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The scene image: the stash-box image of a matched scene, else the still frame saved once at ingestion.</summary>
    [JsonPropertyName("image_id")]
    public Guid? ImageId { get; init; }

    /// <summary>Up to four saved stills, for the optional hover cycling (off by default).</summary>
    [JsonPropertyName("still_ids")]
    public IReadOnlyList<Guid> StillIds { get; init; } = [];
}

public sealed class VaultSceneListDto
{
    [JsonPropertyName("items")]
    public IReadOnlyList<VaultSceneDto> Items { get; init; } = [];

    /// <summary>How many scenes the filters match, before paging.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>The performer, studio or tag the list was filtered by, so its page can carry that name.</summary>
    [JsonPropertyName("heading")]
    public string? Heading { get; init; }
}

public sealed class VaultNameLinkDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

/// <summary>One scene's own page: the card facts, the description, the linked names and how it was matched.</summary>
public sealed class VaultSceneDetailDto
{
    [JsonPropertyName("scene")]
    public VaultSceneDto Scene { get; init; } = new();

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>"fingerprint", "product_code", "title" or "identify"; null when the scene is unmatched.</summary>
    [JsonPropertyName("matched_by")]
    public string? MatchedBy { get; init; }

    [JsonPropertyName("performers")]
    public IReadOnlyList<VaultNameLinkDto> Performers { get; init; } = [];

    [JsonPropertyName("studios")]
    public IReadOnlyList<VaultNameLinkDto> Studios { get; init; } = [];

    [JsonPropertyName("tags")]
    public IReadOnlyList<VaultNameLinkDto> Tags { get; init; } = [];
}

/// <summary>A series or section of the folder layout (D39). The id carries no path.</summary>
public sealed class VaultFolderDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("scene_count")]
    public int SceneCount { get; init; }

    [JsonPropertyName("image_id")]
    public Guid? ImageId { get; init; }
}

/// <summary>A performer, a studio or a tag with at least one owned scene (D39). Tags carry no image.</summary>
public sealed class VaultNameTileDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("scene_count")]
    public int SceneCount { get; init; }

    /// <summary>A performer's portrait or a studio's logo; null shows initials.</summary>
    [JsonPropertyName("image_id")]
    public Guid? ImageId { get; init; }
}

public sealed class VaultSearchRequest
{
    /// <summary>The words to look for. They travel in the body and are never logged.</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("take")]
    public int? Take { get; init; }
}

/// <summary>What the Vault search group shows: matching scenes, and the performers and studios whose names match.</summary>
public sealed class VaultSearchResultDto
{
    [JsonPropertyName("scenes")]
    public IReadOnlyList<VaultSceneDto> Scenes { get; init; } = [];

    [JsonPropertyName("performers")]
    public IReadOnlyList<VaultNameTileDto> Performers { get; init; } = [];

    [JsonPropertyName("studios")]
    public IReadOnlyList<VaultNameTileDto> Studios { get; init; } = [];
}

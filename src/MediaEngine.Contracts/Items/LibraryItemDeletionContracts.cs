using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Items;

/// <summary>Asks the Engine what deleting these titles from the library would do. Nothing is changed.</summary>
public sealed class LibraryItemDeletionPreviewRequest
{
    /// <summary>One to 200 Work ids. Duplicates are collapsed.</summary>
    [JsonPropertyName("work_ids")] public IReadOnlyList<Guid> WorkIds { get; init; } = [];
}

/// <summary>Every outcome a file in a deletion preview can have.</summary>
public static class LibraryItemDeletionOutcomes
{
    /// <summary>The file is in a managed folder and will be removed from disk.</summary>
    public const string Delete = "delete";

    /// <summary>The file is in an as-is folder, so Tuvima only reads it and leaves it on disk.</summary>
    public const string KeepAsIs = "keep_as_is";

    /// <summary>The safety gate refuses to delete the file (a merge original, an unwritable folder, a path outside any library folder).</summary>
    public const string KeepProtected = "keep_protected";

    /// <summary>The file is no longer on disk, so there is nothing to delete.</summary>
    public const string KeepMissing = "keep_missing";
}

/// <summary>What the Engine will do if the titles are deleted, worked out fresh for each request.</summary>
public sealed class LibraryItemDeletionPreview
{
    [JsonPropertyName("items")] public List<LibraryItemDeletionItemDto> Items { get; init; } = [];
    [JsonPropertyName("files")] public List<LibraryItemDeletionFileDto> Files { get; init; } = [];
    [JsonPropertyName("curated_collections")] public List<LibraryItemDeletionCollectionDto> CuratedCollections { get; init; } = [];

    /// <summary>How many different profiles have progress, bookmarks, history or ratings on these titles.</summary>
    [JsonPropertyName("profiles_with_progress")] public int ProfilesWithProgress { get; init; }

    /// <summary>True when identification or enrichment work for these titles is still queued or running.</summary>
    [JsonPropertyName("processing_running")] public bool ProcessingRunning { get; init; }

    /// <summary>Total size of the files whose outcome is <see cref="LibraryItemDeletionOutcomes.Delete"/>.</summary>
    [JsonPropertyName("bytes_freed")] public long BytesFreed { get; init; }

    /// <summary>Identifies this exact plan. Confirming a deletion sends it back so the Engine can tell nothing changed since the preview.</summary>
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = string.Empty;
}

public sealed class LibraryItemDeletionItemDto
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("media_type")] public string MediaType { get; init; } = string.Empty;

    /// <summary>How many media files the title has (sidecars are not counted).</summary>
    [JsonPropertyName("file_count")] public int FileCount { get; init; }
}

public sealed class LibraryItemDeletionFileDto
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }

    /// <summary>The media asset, or null for a sidecar file that travels with a media file.</summary>
    [JsonPropertyName("asset_id")] public Guid? AssetId { get; init; }

    /// <summary>The library the file sits in. Empty when no library folder contains it.</summary>
    [JsonPropertyName("library_name")] public string LibraryName { get; init; } = string.Empty;

    /// <summary>The library and the folder inside its source folder, for example "Movies › Dune Part One (2021)". Never a full disk path.</summary>
    [JsonPropertyName("folder_label")] public string FolderLabel { get; init; } = string.Empty;

    [JsonPropertyName("file_name")] public string FileName { get; init; } = string.Empty;

    /// <summary>Null when the file is missing.</summary>
    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; init; }

    /// <summary>One of <see cref="LibraryItemDeletionOutcomes"/>.</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = LibraryItemDeletionOutcomes.KeepProtected;

    /// <summary>Why a file stays on disk. Null for <see cref="LibraryItemDeletionOutcomes.Delete"/>.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; init; }

    [JsonPropertyName("is_sidecar")] public bool IsSidecar { get; init; }
}

public sealed class LibraryItemDeletionCollectionDto
{
    [JsonPropertyName("collection_id")] public Guid CollectionId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>How many of the requested titles are in this hand-picked collection.</summary>
    [JsonPropertyName("items_in_request")] public int ItemsInRequest { get; init; }

    /// <summary>True when every title in the collection is being deleted, so the collection goes too.</summary>
    [JsonPropertyName("will_be_deleted")] public bool WillBeDeleted { get; init; }
}

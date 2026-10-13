using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Ingestion;

/// <summary>
/// Wire shape for <c>POST /ingestion/reconcile</c>.
///
/// Property names are deliberately lower_snake_case with no
/// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/> — they reproduce, byte
/// for byte, the anonymous object this record replaces.
/// </summary>
public sealed class ReconciliationResultResponse
{
    [JsonPropertyName("total_scanned")]
    public int TotalScanned { get; set; }

    [JsonPropertyName("missing_count")]
    public int MissingCount { get; set; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("duplicate_read_works_merged")]
    public int DuplicateReadWorksMerged { get; set; }

    [JsonPropertyName("audiobook_authors_aligned")]
    public int AudiobookAuthorsAligned { get; set; }
}

/// <summary>
/// Wire shape for <c>GET /ingestion/batches/attention-count</c>.
/// </summary>
public sealed record BatchAttentionCountResponse(int count);

/// <summary>
/// Wire shape for <c>POST /ingestion/upload</c>.
/// </summary>
public sealed record UploadMediaResponse(string path, string mediaType, string destinationLibraryId);

/// <summary>Wire shape for <c>POST /ingestion/rescan</c>.</summary>
public sealed record RescanAcceptedResponse(string message, int paths_scanned);

public sealed class FileMetadataRereadResponse
{
    [JsonPropertyName("asset_id")] public Guid AssetId { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("refreshed")] public bool Refreshed { get; init; }
    [JsonPropertyName("content_hash_changed")] public bool ContentHashChanged { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Wire shape for <c>GET /ingestion/import-folder/waiting</c>: what is being left untouched in the Import folder because a
/// media type has nowhere to file it yet. This describes a situation, not a state of the Import folder.
/// </summary>
public sealed class ImportFolderWaitingResponse
{
    /// <summary>True when an Import folder is set.</summary>
    [JsonPropertyName("configured")] public bool Configured { get; init; }

    /// <summary>One entry per media type with files waiting; empty when nothing is.</summary>
    [JsonPropertyName("waiting")] public List<ImportFolderWaitingDto> Waiting { get; init; } = [];
}

public sealed class ImportFolderWaitingDto
{
    /// <summary>Plain name of the media type, for example "TV Shows".</summary>
    [JsonPropertyName("media_type")] public string MediaType { get; init; } = string.Empty;

    /// <summary>The per-type folder inside the Import folder, for example "TV".</summary>
    [JsonPropertyName("folder")] public string Folder { get; init; } = string.Empty;

    [JsonPropertyName("file_count")] public int FileCount { get; init; }

    /// <summary><c>no_managed_folder</c> (the type has no managed folder) or <c>folder_unavailable</c> (its New media folder is offline, full or not writable).</summary>
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;

    /// <summary>One plain sentence, ready to show in Review and in the Import panel.</summary>
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}

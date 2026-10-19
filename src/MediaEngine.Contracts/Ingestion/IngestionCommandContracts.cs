using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Ingestion;

public sealed class ScanRequest
{
    [JsonPropertyName("root_path")]
    public string? RootPath { get; init; }
}

public sealed class RescanRequest
{
    [JsonPropertyName("root_path")]
    public string? RootPath { get; init; }

    [JsonPropertyName("include_subdirectories")]
    public bool? IncludeSubdirectories { get; init; }
}

public sealed class ScanResponse
{
    [JsonPropertyName("operations")]
    public List<PendingOperationDto> Operations { get; init; } = [];

    [JsonPropertyName("total_count")]
    public int TotalCount => Operations.Count;
}

public sealed class PendingOperationDto
{
    [JsonPropertyName("source_path")]
    public string SourcePath { get; init; } = string.Empty;

    [JsonPropertyName("destination_path")]
    public string DestinationPath { get; init; } = string.Empty;

    [JsonPropertyName("operation_kind")]
    public string OperationKind { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class LibraryScanResponse
{
    [JsonPropertyName("collections_upserted")]
    public int CollectionsUpserted { get; init; }

    [JsonPropertyName("editions_upserted")]
    public int EditionsUpserted { get; init; }

    [JsonPropertyName("people_recovered")]
    public int PeopleRecovered { get; init; }

    [JsonPropertyName("entities_upserted")]
    public int EntitiesUpserted { get; init; }

    [JsonPropertyName("relationships_upserted")]
    public int RelationshipsUpserted { get; init; }

    [JsonPropertyName("errors")]
    public int Errors { get; init; }

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; init; }
}

public sealed class WatchFolderPageResponse
{
    [JsonPropertyName("watch_directory")]
    public string? WatchDirectory { get; init; }

    [JsonPropertyName("files")]
    public IReadOnlyList<WatchFolderFileDto> Files { get; init; } = [];

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}

public sealed class WatchFolderFileDto
{
    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("relative_path")]
    public string RelativePath { get; init; } = string.Empty;

    [JsonPropertyName("file_size_bytes")]
    public long FileSizeBytes { get; init; }

    [JsonPropertyName("last_modified")]
    public DateTimeOffset LastModified { get; init; }
}

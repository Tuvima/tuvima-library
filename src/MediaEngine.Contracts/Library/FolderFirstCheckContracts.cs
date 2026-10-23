using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

/// <summary>
/// The first check of a newly added folder as the library list shows it on a source (Library setup spec D35, D61): <c>checking</c> while its
/// first files are identified (nothing in the folder is renamed, moved or tagged), or <c>paused</c> when most of them did not match.
/// Absent for a folder with no check running. Read-only: it is filled on the read for an administrator and ignored when the list is saved.
/// </summary>
public sealed class FolderFirstCheckDto
{
    /// <summary><c>checking</c> or <c>paused</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>Files among the first ones that have reached a final identification outcome.</summary>
    [JsonPropertyName("tried")]
    public int Tried { get; set; }

    /// <summary>Of those, how many are Unmatched.</summary>
    [JsonPropertyName("unmatched")]
    public int Unmatched { get; set; }

    /// <summary>Every file of the folder the Engine knows about.</summary>
    [JsonPropertyName("total_files")]
    public int TotalFiles { get; set; }
}

/// <summary>One folder whose first check is running or paused, for the administrator's list.</summary>
public sealed class FolderFirstCheckItemDto
{
    [JsonPropertyName("library_id")]
    public string LibraryId { get; init; } = string.Empty;

    [JsonPropertyName("source_id")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary><c>checking</c> or <c>paused</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("tried")]
    public int Tried { get; init; }

    [JsonPropertyName("unmatched")]
    public int Unmatched { get; init; }

    [JsonPropertyName("total_files")]
    public int TotalFiles { get; init; }
}

/// <summary>The folders whose first check is running or paused.</summary>
public sealed class FolderFirstChecksDto
{
    [JsonPropertyName("items")]
    public List<FolderFirstCheckItemDto> Items { get; init; } = [];
}

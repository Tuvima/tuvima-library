using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>
/// A media file that stayed on disk when its title was deleted from the library, so scans skip it until it is added back. Only the
/// file name and a readable folder label travel; the real disk path never leaves the Engine.
/// </summary>
public sealed class ExcludedFileDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("library_id")]
    public string LibraryId { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>The library name, then the folders inside it, joined with " › "; the library name alone when the file is at the top.</summary>
    [JsonPropertyName("folder_label")]
    public string FolderLabel { get; set; } = string.Empty;

    /// <summary>The title that was deleted, as it was then; null when it was not recorded.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("excluded_at")]
    public DateTimeOffset ExcludedAt { get; set; }

    /// <summary>Who deleted the title, as named then; null when it was not recorded.</summary>
    [JsonPropertyName("excluded_by")]
    public string? ExcludedBy { get; set; }

    /// <summary>Why the file stayed: <c>as_is</c>, <c>protected</c> or <c>delete_failed</c>.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>What happened when a file was added back. The exclusion is gone either way; <see cref="Queued"/> says whether a scan was queued.</summary>
public sealed class ExcludedFileAddBackResultDto
{
    [JsonPropertyName("queued")]
    public bool Queued { get; set; }

    /// <summary>Plain words for the person, ready to show.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

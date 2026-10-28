using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Library;

/// <summary>Add files (Libraries spec D8): server files to copy into one library's import destination. The originals are never moved or changed.</summary>
public sealed class LibraryAddFilesRequest
{
    /// <summary>Full paths on the Library server; each must sit inside an administrator-approved storage location.</summary>
    [JsonPropertyName("paths")]
    public List<string> Paths { get; init; } = [];
}

/// <summary>How an Add files copy went.</summary>
public sealed class LibraryAddFilesResultDto
{
    [JsonPropertyName("copied")]
    public int Copied { get; init; }

    /// <summary>Files that were already in the library (same contents), left out under the duplicate rule.</summary>
    [JsonPropertyName("skipped_duplicates")]
    public int SkippedDuplicates { get; init; }

    /// <summary>Files that could not be read or had a name that cannot be saved.</summary>
    [JsonPropertyName("unreadable")]
    public int Unreadable { get; init; }

    /// <summary>Files the library's content type cannot take.</summary>
    [JsonPropertyName("unsupported")]
    public int Unsupported { get; init; }

    [JsonPropertyName("bytes_copied")]
    public long BytesCopied { get; init; }
}

/// <summary>The reasons an Add files request is refused before anything is copied; they travel as the problem's <c>code</c>.</summary>
public static class LibraryAddFilesRefusalCodes
{
    public const string NoImportDestination = "no_import_destination";
    public const string DestinationUnavailable = "destination_unavailable";
    public const string NotEnoughSpace = "not_enough_space";
    public const string PathNotApproved = "path_not_approved";
    public const string NothingSelected = "nothing_selected";
}

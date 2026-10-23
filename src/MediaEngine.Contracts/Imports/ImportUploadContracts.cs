using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Imports;

/// <summary>
/// Asks the Engine to start a resumable upload of one file into the Import folder (Library setup spec D44). The Engine decides
/// everything about the destination: this names only the kind, optionally a library of that kind, the file and its size.
/// </summary>
public sealed class ImportUploadStartRequest
{
    /// <summary>The kind the file belongs to (for example <c>movies</c>, <c>music</c>, <c>other_videos</c>); it picks the Import folder's sub-folder.</summary>
    [JsonPropertyName("kind_key")]
    public string KindKey { get; init; } = string.Empty;

    /// <summary>The library to attribute the file to, for a kind that has several. Null means the kind's own library.</summary>
    [JsonPropertyName("library_id")]
    public string? LibraryId { get; init; }

    /// <summary>The file's name only, never a path.</summary>
    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; init; }
}

/// <summary>
/// The answer to a start call that was accepted. A refusal is not this type: it is a problem body whose <c>code</c> is one of
/// <see cref="ImportUploadRefusalCodes"/>, and nothing has been created.
/// </summary>
public sealed class ImportUploadStartResponse
{
    [JsonPropertyName("upload_id")]
    public Guid UploadId { get; init; }

    /// <summary>The size of every piece but the last, in bytes. The client cuts the file with exactly this size.</summary>
    [JsonPropertyName("piece_bytes")]
    public long PieceBytes { get; init; }

    [JsonPropertyName("piece_count")]
    public int PieceCount { get; init; }

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; init; }
}

/// <summary>Where a resumable upload stands, so a client that lost its connection carries on without resending a received piece.</summary>
public sealed class ImportUploadStatusResponse
{
    [JsonPropertyName("upload_id")]
    public Guid UploadId { get; init; }

    /// <summary>One of <see cref="ImportUploadStates"/>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = ImportUploadStates.Open;

    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("piece_bytes")]
    public long PieceBytes { get; init; }

    [JsonPropertyName("piece_count")]
    public int PieceCount { get; init; }

    /// <summary>The indexes (from 0) of the pieces received so far, in order.</summary>
    [JsonPropertyName("received_pieces")]
    public List<int> ReceivedPieces { get; init; } = [];

    [JsonPropertyName("received_bytes")]
    public long ReceivedBytes { get; init; }
}

/// <summary>The answer to a complete call: the file is in the Import folder under this name and waits to be filed by the normal rules.</summary>
public sealed class ImportUploadCompleteResponse
{
    [JsonPropertyName("upload_id")]
    public Guid UploadId { get; init; }

    /// <summary>The name the file was saved under. It differs from the name asked for when that name was already taken.</summary>
    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    /// <summary>True when the saved name has a number added because the name asked for already existed.</summary>
    [JsonPropertyName("renamed")]
    public bool Renamed { get; init; }

    /// <summary>The full path of the saved file inside the Import folder. Shown to administrators only.</summary>
    [JsonPropertyName("destination_path")]
    public string DestinationPath { get; init; } = string.Empty;
}

/// <summary>The states of a resumable upload.</summary>
public static class ImportUploadStates
{
    public const string Open = "open";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// The reason codes of a refused upload call (the <c>code</c> of the problem body), so the Dashboard can show the right message
/// without reading text. A start call refused with any of these has created nothing.
/// </summary>
public static class ImportUploadRefusalCodes
{
    /// <summary>No Import folder is set. The Dashboard says "Set an Import folder first" with a link to where it is set.</summary>
    public const string NoImportFolder = "no_import_folder";

    /// <summary>The kind has no Managed folder to file into (or its library is not one files can be added to this way).</summary>
    public const string NoManagedDestination = "no_managed_destination";

    /// <summary>
    /// The kind's library is a Vault, and adding files to the Vault by dropping them is not available yet (Library setup spec D57
    /// defers it with the rest of the Vault extras).
    /// </summary>
    public const string VaultNotAvailable = "vault_not_available";

    /// <summary>The Import folder or the kind's Managed folder cannot be reached, is not writable, or is read-only.</summary>
    public const string DestinationUnavailable = "destination_unavailable";

    /// <summary>The drive does not have room for the file.</summary>
    public const string NotEnoughSpace = "not_enough_space";

    /// <summary>The file is above <c>max_upload_bytes</c>.</summary>
    public const string TooLarge = "too_large";

    /// <summary>An administrator turned off drag and drop (<c>allow_drag_and_drop</c>).</summary>
    public const string DragAndDropOff = "drag_and_drop_off";

    /// <summary>The kind is not one the Library knows or offers for files.</summary>
    public const string UnknownKind = "unknown_kind";

    /// <summary>The library named is not one of the kind's libraries.</summary>
    public const string UnknownLibrary = "unknown_library";

    /// <summary>The file name has path parts, a reserved or empty name, or characters a file name cannot hold.</summary>
    public const string InvalidFileName = "invalid_file_name";

    /// <summary>The size is zero or not a number of bytes.</summary>
    public const string InvalidSize = "invalid_size";

    /// <summary>The piece is not the size the upload expects, or its index is out of range.</summary>
    public const string InvalidPiece = "invalid_piece";

    /// <summary>There is no such upload for this caller. An upload someone else started answers the same.</summary>
    public const string UploadNotFound = "upload_not_found";

    /// <summary>The upload is no longer open (completed, cancelled or abandoned).</summary>
    public const string UploadClosed = "upload_closed";

    /// <summary>Complete was called before every piece arrived.</summary>
    public const string PiecesMissing = "pieces_missing";

    /// <summary>The hidden working file does not hold the whole file the upload promised.</summary>
    public const string SizeMismatch = "size_mismatch";
}

namespace MediaEngine.Ingestion.Services.ImportUploads;

/// <summary>What kind of problem a refused upload call is, so the Engine's action can pick the matching answer.</summary>
public enum ImportUploadRefusalKind
{
    /// <summary>The request itself is wrong (a bad name, size or piece).</summary>
    BadRequest,

    /// <summary>There is no such upload for this caller.</summary>
    NotFound,

    /// <summary>The Library's state does not allow it (no Import folder, no destination, the upload is closed).</summary>
    Conflict,

    /// <summary>The drive has no room.</summary>
    InsufficientStorage,
}

/// <summary>A refused upload call: a stable <see cref="Code"/> (one of <c>ImportUploadRefusalCodes</c>), words a person can read, and the kind of problem.</summary>
public sealed record ImportUploadRefusal(ImportUploadRefusalKind Kind, string Code, string Message, string? ImportFolderPath = null);

/// <summary>Either the answer to an upload call or the reason it was refused.</summary>
public sealed class ImportUploadResult<T>
{
    private ImportUploadResult(T? value, ImportUploadRefusal? refusal)
    {
        Value = value;
        Refusal = refusal;
    }

    public T? Value { get; }

    public ImportUploadRefusal? Refusal { get; }

    public bool Succeeded => Refusal is null;

    public static ImportUploadResult<T> Ok(T value) => new(value, null);

    public static ImportUploadResult<T> Refused(ImportUploadRefusalKind kind, string code, string message, string? importFolderPath = null) =>
        new(default, new ImportUploadRefusal(kind, code, message, importFolderPath));
}

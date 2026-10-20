namespace MediaEngine.Ingestion.Models;

/// <summary>
/// Stable routing context attached to a file before it enters the ingestion
/// pipeline. Direct intake callers set <see cref="DestinationLibraryId"/> so
/// the pipeline never has to rediscover a destination that is already known.
/// </summary>
public sealed record IntakeContext
{
    public string SourceKind { get; init; } = IntakeSourceKinds.Watcher;

    public string? SourceId { get; init; }

    public string? DestinationLibraryId { get; init; }

    public Guid? ActorProfileId { get; init; }

    public bool HasDestinationHint => !string.IsNullOrWhiteSpace(DestinationLibraryId);
}

/// <summary>Canonical intake origins used in durable logs and routing.</summary>
public static class IntakeSourceKinds
{
    public const string Watcher = "watcher";
    public const string DirectLibrary = "direct_library";
    public const string BrowserUpload = "browser_upload";
    public const string MobileBackup = "mobile_backup";
    public const string ConnectedDevice = "connected_device";
    public const string Api = "api";

    public static bool IsValid(string? value) => value is
        Watcher or DirectLibrary or BrowserUpload
        or MobileBackup or ConnectedDevice or Api;
}

/// <summary>A single file submitted through a non-watcher intake surface.</summary>
public sealed record IntakeFileRequest
{
    public required string Path { get; init; }

    public string SourceKind { get; init; } = IntakeSourceKinds.DirectLibrary;

    public string? SourceId { get; init; }

    public string? DestinationLibraryId { get; init; }

    public Guid? ActorProfileId { get; init; }
}

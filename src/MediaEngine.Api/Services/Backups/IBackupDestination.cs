namespace MediaEngine.Api.Services.Backups;

/// <summary>One backup file held at a destination.</summary>
public sealed record DestinationBackup(string Name, long SizeBytes, DateTimeOffset ModifiedAt);

/// <summary>The answer to "does this destination work?". <see cref="Message"/> is plain words with no secrets.</summary>
public sealed record DestinationTestResult(bool Success, string Message);

/// <summary>
/// A place a finished backup is copied to (Developer tools spec WP25, decision D-P). A destination can do exactly these things and
/// nothing else: test the connection, send a backup, list what it holds, fetch one for restore, delete old ones, and report free
/// space. Every destination receives the same plain backup file (backups are never encrypted).
/// </summary>
public interface IBackupDestination
{
    /// <summary>A stable id: <c>local</c>, or the destination's own id.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>True when the copy goes somewhere other than this computer's backup folder, so it only receives a copy when switched on.</summary>
    bool LeavesMachine { get; }

    /// <summary>False until the person has filled in what it needs. A destination that is not set up is shown but never used.</summary>
    bool IsConfigured { get; }

    Task<DestinationTestResult> TestAsync(CancellationToken ct);

    /// <summary>Stores <paramref name="content"/> under <paramref name="name"/>. The name is a plain file name with no folders.</summary>
    Task SendAsync(string name, Stream content, long length, CancellationToken ct);

    Task<IReadOnlyList<DestinationBackup>> ListAsync(CancellationToken ct);

    Task<Stream> FetchAsync(string name, CancellationToken ct);

    Task DeleteAsync(string name, CancellationToken ct);

    /// <summary>Free space in bytes, or null when the destination cannot say.</summary>
    Task<long?> FreeSpaceAsync(CancellationToken ct);
}

/// <summary>A destination could not do what it was asked. The message is plain words and is shown to the person.</summary>
public sealed class BackupDestinationException(string message, Exception? inner = null) : Exception(message, inner);

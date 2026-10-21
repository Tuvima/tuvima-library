namespace MediaEngine.Storage.Contracts;

/// <summary>One copy of a backup sent to a destination. <see cref="State"/> is <c>sent</c>, <c>verified</c>, <c>failed</c> or <c>deleted</c>.</summary>
public sealed record StoredBackupCopy(
    Guid Id,
    string DestinationId,
    string RemoteName,
    long SizeBytes,
    string Sha256,
    string State,
    DateTimeOffset? VerifiedAt,
    string? LastError,
    DateTimeOffset CreatedAt);

/// <summary>The backup schedule and destination settings, and the record of what was copied where.</summary>
public interface IBackupDestinationRepository
{
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);

    Task SetSettingAsync(string key, string json, DateTimeOffset now, CancellationToken ct = default);

    Task AddCopyAsync(StoredBackupCopy copy, CancellationToken ct = default);

    /// <summary>The newest copies first. With a destination id, only that destination's.</summary>
    Task<IReadOnlyList<StoredBackupCopy>> ListCopiesAsync(string? destinationId, int limit, CancellationToken ct = default);

    /// <summary>Marks the copy with this remote name at this destination as removed (retention or the person deleted it).</summary>
    Task MarkDeletedAsync(string destinationId, string remoteName, CancellationToken ct = default);
}

namespace MediaEngine.Domain.Contracts;

/// <summary>How a Vault video's details came to be applied (Videos spec D30, D38).</summary>
public static class VaultAppliedVia
{
    /// <summary>The Vault ingestion path matched the file by itself, with a strong match.</summary>
    public const string Automatic = "automatic";

    /// <summary>A person picked the record and ticked the fields in Identify.</summary>
    public const string Identify = "identify";

    public static bool IsValid(string? value) => value is Automatic or Identify;
}

/// <summary>The facts about one Vault file that matching needs. Read from the file's own records, never from a provider.</summary>
public sealed record VaultFileFacts(Guid AssetId, string LibraryId, string FilePath, double? DurationSeconds, long? SizeBytes);

/// <summary>The record a Vault video was matched to. A video with no such row is Unmatched (Videos spec D32).</summary>
public sealed record VaultMatch(
    Guid AssetId,
    string ServerId,
    string RecordId,
    string MatchedBy,
    string AppliedVia,
    DateTimeOffset MatchedAt);

/// <summary>Where one field of a Vault video came from: which server, which record, how it was matched, and when (Videos spec D30).</summary>
public sealed record VaultFieldSource(
    Guid AssetId,
    string Field,
    string ServerId,
    string RecordId,
    string MatchedBy,
    string AppliedVia,
    string? Language,
    DateTimeOffset AppliedAt);

/// <summary>One fingerprint computed from a Vault file on this machine, kept so the file is not read again.</summary>
public sealed record VaultStoredFingerprint(string Algorithm, string Hash, long FileSize, DateTimeOffset ComputedAt);

/// <summary>The fields Identify can apply, and the keys provenance is recorded under.</summary>
public static class VaultFieldNames
{
    public const string Title = "title";
    public const string Description = "description";
    public const string ReleaseDate = "release_date";
    public const string Cover = "cover";
    public const string Studio = "studio";
    public const string Performers = "performers";
    public const string Tags = "tags";

    public static readonly IReadOnlyList<string> All = [Title, Description, ReleaseDate, Cover, Studio, Performers, Tags];

    public static bool IsValid(string? value) => value is Title or Description or ReleaseDate or Cover or Studio or Performers or Tags;
}

/// <summary>
/// What the Library knows about how each Vault video was matched (Videos spec D30, D37). Kept in tables that only hold Vault files:
/// the data structure refuses a row for any other file.
/// </summary>
public interface IVaultMatchRepository
{
    /// <summary>The file facts of a Vault file, or null when the file is not in a Vault library.</summary>
    Task<VaultFileFacts?> GetFileFactsAsync(Guid assetId, CancellationToken ct = default);

    Task<VaultMatch?> GetMatchAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Records the match and replaces the provenance of exactly the fields listed (other fields keep theirs).</summary>
    Task SaveMatchAsync(VaultMatch match, IReadOnlyList<VaultFieldSource> fields, CancellationToken ct = default);

    Task<IReadOnlyList<VaultFieldSource>> GetFieldSourcesAsync(Guid assetId, CancellationToken ct = default);

    Task<IReadOnlyList<VaultStoredFingerprint>> GetFingerprintsAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Replaces the stored fingerprints of one file.</summary>
    Task SaveFingerprintsAsync(Guid assetId, IReadOnlyList<VaultStoredFingerprint> fingerprints, CancellationToken ct = default);
}

/// <summary>What a line of the quiet Vault activity list says happened.</summary>
public static class VaultActivityKinds
{
    public const string Added = "added";
    public const string Matched = "matched";
    public const string Unmatched = "unmatched";
    public const string Identified = "identified";
    public const string Failed = "failed";

    public static bool IsValid(string? value) => value is Added or Matched or Unmatched or Identified or Failed;
}

/// <summary>One line of the Vault activity list. The detail is fixed plain words; it never carries a title or a path.</summary>
public sealed record VaultActivityItem(Guid Id, Guid AssetId, string Kind, string FileName, string Detail, DateTimeOffset At);

/// <summary>
/// The quiet "Vault activity" list that shows Vault ingestion inside Vault mode (Videos spec D32). It is the only place Vault ingestion
/// progress is shown: nothing about it reaches Needs Review, its banner or count, notifications or the live ingestion page.
/// </summary>
public interface IVaultActivityRepository
{
    Task AddAsync(Guid assetId, string kind, string fileName, string detail, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The most recent lines, newest first.</summary>
    Task<IReadOnlyList<VaultActivityItem>> GetRecentAsync(int limit, CancellationToken ct = default);
}

/// <summary>A file that has just been registered in a Vault library, handed to the Vault ingestion path.</summary>
/// <param name="StillFrame">A still frame picked from the video, if one was made. It is stored with the Vault's own artwork.</param>
public sealed record VaultRegisteredFile(Guid AssetId, string LibraryId, string FilePath, byte[]? StillFrame, string? StillFrameMimeType);

/// <summary>
/// The Vault's part of ingestion (Videos spec WP12). The ingestion engine asks whether a library is a Vault library and, if it is,
/// hands over each file once it is registered. Nothing here raises a review item or an event.
/// </summary>
public interface IVaultIngestionHook
{
    Task<bool> IsVaultLibraryAsync(string? libraryId, CancellationToken ct = default);

    /// <summary>True when the id is a file in a Vault library (such a file never gets a Needs Review item).</summary>
    Task<bool> IsVaultAssetAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Stores the still frame with the Vault's artwork, then looks the file up. Never throws for a failed lookup.</summary>
    Task OnFileRegisteredAsync(VaultRegisteredFile file, CancellationToken ct = default);
}

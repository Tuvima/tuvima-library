namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Writes resolved canonical metadata back into the physical media file.
/// Implementations resolve the file path, load canonical values, select the
/// appropriate tagger, and optionally create a backup before modifying.
///
/// Called from:
/// <list type="bullet">
///   <item><c>HydrationPipelineService</c> after Stage 1 (auto-match) and Stage 2 (universe enrichment).</item>
///   <item><c>MetadataEndpoints</c> after manual override (<c>PUT /metadata/{entityId}/override</c>).</item>
/// </list>
/// </summary>
public interface IWriteBackService
{
    /// <summary>
    /// Writes current canonical metadata into the physical file for the given asset.
    /// No-op if write-back is disabled or no tagger supports the file format.
    /// </summary>
    /// <param name="assetId">The <c>media_assets.id</c> identifying the file.</param>
    /// <param name="trigger">The write-back trigger (e.g. "auto_match", "manual_override", "universe_enrichment").</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteMetadataAsync(Guid assetId, string trigger, CancellationToken ct = default, Guid? ingestionRunId = null);
}

/// <summary>Outcome-bearing contract for durable write-back dispatch.</summary>
public interface IWriteBackOutcomeService : IWriteBackService
{
    Task<WriteBackOutcome> WriteMetadataWithOutcomeAsync(
        Guid assetId, string trigger, CancellationToken ct = default, Guid? ingestionRunId = null);
}

public enum WriteBackOutcomeKind
{
    Verified,
    Unverified,
    Blocked,
    Unsupported,
    Failed,
}

public sealed record WriteBackOutcome(WriteBackOutcomeKind Kind, string? Reason = null)
{
    public static WriteBackOutcome Verified() => new(WriteBackOutcomeKind.Verified);
    public static WriteBackOutcome Unverified(string? reason = null) => new(WriteBackOutcomeKind.Unverified, reason);
    public static WriteBackOutcome Blocked(string reason) => new(WriteBackOutcomeKind.Blocked, reason);
    public static WriteBackOutcome Unsupported(string reason) => new(WriteBackOutcomeKind.Unsupported, reason);
    public static WriteBackOutcome Failed(string reason) => new(WriteBackOutcomeKind.Failed, reason);
}

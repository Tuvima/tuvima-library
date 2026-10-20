namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Moves a staged media asset into the organised library structure.
///
/// Called by the hydration pipeline after re-scoring lifts an entity's
/// confidence above the auto-organize threshold (0.85). The implementation
/// loads canonical values, calculates the destination path, moves the file,
/// and writes sidecar XML.
///
/// Implementations live in <c>MediaEngine.Ingestion</c>.
/// </summary>
public interface IAutoOrganizeService
{
    /// <summary>
    /// Attempts to organize the asset identified by <paramref name="assetId"/>
    /// from the staging directory into the library. No-op if the asset is
    /// already in the library or if configuration is incomplete.
    /// </summary>
    Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default);
}

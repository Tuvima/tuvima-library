using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Computes a SHA-256 hash for a media file without loading the file into memory.
/// Spec: Phase 7 – Asset Integrity; Hash-Based Uniqueness.
///
/// The returned <see cref="HashResult.Hex"/> is used as
/// <c>media_assets.content_hash</c>, the Hash Dominance reconciliation key.
/// </summary>
public interface IAssetHasher
{
    /// <summary>
    /// Streams <paramref name="filePath"/> in fixed-size chunks and returns a
    /// <see cref="HashResult"/> containing the lowercase hex digest.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the file does not exist or is not readable.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="ct"/> is cancelled mid-stream.
    /// </exception>
    Task<HashResult> ComputeAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// The SHA-256 of every byte. <see cref="ComputeAsync"/> may sample a very large file to stay quick on a slow drive, which is
    /// enough to recognise a file but not to prove two files are the same, so anything that deletes a copy, verifies a copy or checks
    /// a move must use this. A hasher that always reads whole files need not override it.
    /// </summary>
    Task<HashResult> ComputeFullAsync(string filePath, CancellationToken ct = default) => ComputeAsync(filePath, ct);
}

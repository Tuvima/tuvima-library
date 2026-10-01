namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Defines the contract for embedding metadata and images back into physical files.
/// Spec: Phase 7 – Interfaces § IMetadataTagger.
///
/// Concrete implementations register themselves with the ingestion engine via the
/// "Dynamic Registration" extension point, keyed by file signature / MIME type.
/// Examples: ID3v2 tagger for MP3, XMP for EPUB, OPF for EPUB OPF packages.
/// Spec: Phase 7 – Extension Points § Metadata Taggers.
///
/// Failure handling: "If a metadata write-back operation fails, the system MUST
/// attempt to restore the file from a temporary backup or mark the asset as
/// Write-Failed."
/// </summary>
public interface IMetadataTagger
{
    /// <summary>
    /// Returns <see langword="true"/> when this tagger supports the file at
    /// <paramref name="filePath"/> (typically determined by extension or magic bytes).
    /// </summary>
    bool CanHandle(string filePath);

    /// <summary>Returns physical write capabilities for this file's adapter.</summary>
    MetadataTaggerCapabilities GetCapabilities(string filePath);

    /// <summary>
    /// Writes key-value metadata tags into the file.
    /// Implementations MUST create a temp backup before modifying and restore on failure.
    /// TagLibSharp-backed implementations currently perform their file writes
    /// synchronously under this async contract because the underlying library exposes
    /// synchronous save APIs only.
    /// </summary>
    Task WriteTagsAsync(
        string filePath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default);

    /// <summary>
    /// Reopens the physical file and checks every requested field. Adapters
    /// without a proven reader report unverified, even when writing succeeded.
    /// </summary>
    Task<MetadataTagReadbackResult> VerifyTagsAsync(
        string filePath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default) =>
        Task.FromResult(MetadataTagReadbackResult.Unverified("This adapter has no verified metadata reader."));

    /// <summary>
    /// Embeds <paramref name="imageData"/> as cover art / thumbnail in the file.
    /// Implementations MUST create a temp backup before modifying and restore on failure.
    /// TagLibSharp-backed implementations currently perform their file writes
    /// synchronously under this async contract because the underlying library exposes
    /// synchronous save APIs only.
    /// </summary>
    Task WriteCoverArtAsync(
        string filePath,
        byte[] imageData,
        CancellationToken ct = default);
}

public sealed record MetadataTagReadbackResult(bool IsVerified, string? Reason)
{
    public static MetadataTagReadbackResult Verified() => new(true, null);
    public static MetadataTagReadbackResult Unverified(string reason) => new(false, reason);
}
